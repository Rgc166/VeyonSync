using System.Net.Http.Json;
using VeyonSync.Shared;
using VeyonSync.Teacher.Veyon;

namespace VeyonSync.Teacher;

/// <summary>
/// Coordenação entre professores para "Integrar todos". Cada laboratório continua aplicando no próprio Veyon:
/// o THC central apenas pede, por HTTPS com certificado fixado e token de professor, que cada professor
/// autorizado execute a integração local dele.
/// </summary>
public sealed class Peers(TeacherContext ctx, VeyonEngine engine, ILogger<Peers> log)
{
    static readonly TimeSpan PeerTimeout = TimeSpan.FromMinutes(10);
    const int MaxParallel = 3;

    public async Task<PeerRow> AddAsync(string linkContent, string code, CancellationToken ct)
    {
        var link = LinkFile.Parse(linkContent);
        if (string.Equals(link.LabId, ctx.Lab, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Este arquivo é do próprio laboratório deste professor.");
        if (string.IsNullOrWhiteSpace(code)) throw new InvalidDataException("Informe o código gerado no THC daquele laboratório.");
        var probe = new Tls.Probe();
        using var http = Tls.CreateClient(link.CertificateSha256, probe, TimeSpan.FromSeconds(20));
        HttpResponseMessage r;
        try { r = await http.PostAsJsonAsync(link.Url.TrimEnd('/') + "/api/peer/pair", new PeerPairRequest(code.Trim(), ctx.TeacherName, ctx.Lab), ct); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { throw new InvalidOperationException(Tls.Explain(e, probe)); }
        if (!r.IsSuccessStatusCode) throw new InvalidOperationException(await ErrorText(r, ct));
        var resp = await r.Content.ReadFromJsonAsync<PeerPairResponse>(ct) ?? throw new InvalidOperationException("Resposta vazia do professor.");
        if (!string.Equals(resp.Lab, link.LabId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"O professor respondeu como laboratório {resp.Lab}, mas o arquivo indica {link.LabId}.");
        ctx.Db.AddPeer(resp.TeacherName, resp.Lab, link.Url, link.CertificateSha256, Keys.Protect(resp.Token));
        return ctx.Db.Peers().First(p => string.Equals(p.Lab, resp.Lab, StringComparison.OrdinalIgnoreCase));
    }

    public static async Task<string> ErrorText(HttpResponseMessage r, CancellationToken ct)
    {
        try
        {
            var e = await r.Content.ReadFromJsonAsync<ApiError>(ct);
            if (!string.IsNullOrEmpty(e?.Error)) return e.Error;
        }
        catch (Exception) { }
        return $"O professor recusou o pedido (HTTP {(int)r.StatusCode}).";
    }

    HttpClient ClientFor(long peerId, out PeerRow row, out Tls.Probe probe)
    {
        var p = ctx.Db.Peer(peerId) ?? throw new InvalidDataException("Professor não cadastrado.");
        row = p.row;
        if (!row.Enabled) throw new InvalidOperationException("Professor desativado no cadastro.");
        probe = new Tls.Probe();
        var http = Tls.CreateClient(p.certSha256, probe, PeerTimeout);
        http.DefaultRequestHeaders.Authorization = new("Bearer", Keys.Unprotect(p.tokenProtected ?? throw new InvalidOperationException("Credencial ausente.")));
        return http;
    }

    public async Task<VeyonPreview> PreviewPeerAsync(long peerId, CancellationToken ct)
    {
        PeerRow row = new();
        Tls.Probe probe = new();
        try
        {
            using var http = ClientFor(peerId, out row, out probe);
            var r = await http.GetAsync(row.Url.TrimEnd('/') + "/api/peer/veyon/preview", ct);
            if (!r.IsSuccessStatusCode) throw new InvalidOperationException(await ErrorText(r, ct));
            ctx.Db.PeerStatus(peerId, "Disponível");
            return await r.Content.ReadFromJsonAsync<VeyonPreview>(ct) ?? new VeyonPreview { Lab = row.Lab, Error = "Resposta vazia." };
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var msg = e is HttpRequestException or TaskCanceledException ? Tls.Explain(e, probe) : e.Message;
            ctx.Db.PeerStatus(peerId, "Indisponível: " + msg);
            return new VeyonPreview { Lab = row.Lab, Destination = row.Name, Error = "Professor indisponível: " + msg };
        }
    }

    async Task<BatchSummary> ApplyPeerAsync(long peerId, bool retryFailedOnly, CancellationToken ct)
    {
        PeerRow row = new();
        Tls.Probe probe = new();
        try
        {
            using var http = ClientFor(peerId, out row, out probe);
            var r = await http.PostAsJsonAsync(row.Url.TrimEnd('/') + "/api/peer/veyon/apply", new PeerApplyRequest(retryFailedOnly), ct);
            if (!r.IsSuccessStatusCode) throw new InvalidOperationException(await ErrorText(r, ct));
            var s = await r.Content.ReadFromJsonAsync<BatchSummary>(ct) ?? throw new InvalidOperationException("Resposta vazia.");
            ctx.Db.PeerStatus(peerId, $"Último lote: {s.Integrated} integrados, {s.Errors} erros");
            return s;
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var msg = e is HttpRequestException or TaskCanceledException ? Tls.Explain(e, probe) : e.Message;
            ctx.Db.PeerStatus(peerId, "Indisponível: " + msg);
            ctx.Db.Error(row.Lab, "", row.Name, "Professor parceiro", msg);
            log.LogWarning("Professor {Lab} indisponível: {Msg}", row.Lab, msg);
            return new BatchSummary { Lab = row.Lab, Destination = row.Name, Unavailable = true, Detail = msg };
        }
    }

    public async Task<List<VeyonPreview>> BatchPreviewAsync(bool includeLocal, IReadOnlyCollection<long> peerIds, CancellationToken ct)
    {
        var tasks = new List<Task<VeyonPreview>>();
        if (includeLocal) tasks.Add(engine.PreviewAsync(true, null, ct));
        tasks.AddRange(peerIds.Select(id => PreviewPeerAsync(id, ct)));
        return (await Task.WhenAll(tasks)).ToList();
    }

    /// <summary>Uma confirmação por lote; cada destino em paralelo limitado; falha de um não interrompe os outros.</summary>
    public async Task<List<BatchSummary>> BatchApplyAsync(bool includeLocal, IReadOnlyCollection<long> peerIds, bool retryFailedOnly, CancellationToken ct)
    {
        using var sem = new SemaphoreSlim(MaxParallel);
        async Task<BatchSummary> Run(Func<Task<BatchSummary>> f)
        {
            await sem.WaitAsync(ct);
            try { return await f(); }
            finally { sem.Release(); }
        }
        var tasks = new List<Task<BatchSummary>>();
        if (includeLocal) tasks.Add(Run(() => engine.ApplyAsync(retryFailedOnly, null, ct)));
        foreach (var id in peerIds.Distinct()) tasks.Add(Run(() => ApplyPeerAsync(id, retryFailedOnly, ct)));
        var results = (await Task.WhenAll(tasks)).ToList();
        ctx.Db.Hist("", ctx.Lab, ctx.TeacherName, "Integração Veyon",
            "Lote: " + string.Join(" | ", results.Select(r => r.Unavailable ? $"Lab {r.Lab}: indisponível" : $"Lab {r.Lab}: {r.Integrated} integrados, {r.AlreadyUpdated} já atualizados, {r.Conflicts} conflitos, {r.Errors} erros")),
            results.Any(r => r.Unavailable || r.Errors > 0) ? "aviso" : "ok");
        return results;
    }
}
