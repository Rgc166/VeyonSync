using System.Net;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using VeyonSync.Shared;

namespace VeyonSync.Student;

/// <summary>
/// Núcleo do STD: coleta, fila persistente, envio com confirmação, presença, modo manual, pareamento e ligação.
/// Um único envio por vez (sendLock) evita envios concorrentes e duplicados.
/// </summary>
public sealed class Agent
{
    readonly ILogger<Agent> log;
    readonly SemaphoreSlim sendLock = new(1, 1);
    readonly State st;
    Snapshot? last;
    bool startHandled;
    int fails;
    DateTimeOffset nextRetry = DateTimeOffset.MinValue, lastSent = DateTimeOffset.MinValue;
    volatile bool networkChanged;
    /// <summary>Sinaliza mudança de endereço para o laço acordar imediatamente.</summary>
    public bool ConsumeNetworkChanged() { var v = networkChanged; networkChanged = false; return v; }
    HttpClient? http;
    string? httpPin = "\0";
    Tls.Probe probe = new();
    public static string ForceFlag => Path.Combine(AgentConfig.Dir, "force.flag");
    const string CollectFlag = "collect";
    public event Func<SignedManifest, CancellationToken, Task>? UpdateOffered;

    public Agent(ILogger<Agent> log)
    {
        this.log = log;
        st = State.Load();
        st.AgentVersion = AppInfo.Version;
        st.ServiceStarted = DateTimeOffset.Now;
        ServiceMarker.Write(AppInfo.StudentData);
        if (st.Status == "Enviando") st.Status = st.Pending != null ? "Aguardando" : st.Status;
        st.Save();
        NetworkChange.NetworkAddressChanged += (_, _) => networkChanged = true;
    }

    HttpClient Client(AgentConfig cfg)
    {
        if (http == null || httpPin != cfg.TeacherCertSha256)
        {
            http?.Dispose();
            probe = new Tls.Probe();
            http = Tls.CreateClient(cfg.TeacherCertSha256, probe, TimeSpan.FromSeconds(15));
            httpPin = cfg.TeacherCertSha256;
        }
        probe.Failure = null;
        return http;
    }

    static string Base(AgentConfig cfg) => cfg.TeacherUrl.TrimEnd('/');

    // ------------------------------------------------------------------ ciclo periódico
    public async Task TickAsync(CancellationToken ct)
    {
        AgentConfig cfg;
        try { cfg = AgentConfig.Load(); }
        catch (Exception e) { Fail("Configuração inválida: " + e.Message); return; }

        var snap = Collector.Collect(cfg.TeacherUrl);
        RememberSnapshot(snap);
        if (!cfg.Paired)
        {
            if (st.Status != "Aguardando credenciamento") { st.Status = "Aguardando credenciamento"; st.Save(); }
            last = snap;
            return;
        }

        bool force = File.Exists(ForceFlag);
        string? reason = null;
        if (!startHandled && snap.Ip != null) { startHandled = true; if (cfg.SendOnStart) reason = ReportReasons.Start; }
        else if (last != null)
        {
            if (cfg.SendOnLogon && snap.User != last.User && snap.User != null) reason = ReportReasons.Logon;
            else if (snap.User != last.User) reason = "mudança de usuário";
            if (cfg.SendOnIpChange && snap.Ip != last.Ip) reason ??= ReportReasons.IpChange;
        }
        last = snap;

        var now = DateTimeOffset.UtcNow;
        if (force)
        {
            // "collect" = pedido de coleta do professor (o THC correlaciona a resposta pelo motivo).
            string flag = "";
            try { flag = File.ReadAllText(ForceFlag).Trim(); } catch (IOException) { }
            await SendAsync(cfg, flag == CollectFlag ? ReportReasons.Collect : ReportReasons.Manual, false, true, ct);
            return;
        }
        if (!cfg.AutoSend) return; // modo manual: sem presença, sem eventos automáticos e sem novas tentativas
        if (now < nextRetry) return;
        if (reason != null || st.Pending != null) { await SendAsync(cfg, reason ?? st.Pending?.Reason ?? "pendência", false, false, ct); return; }
        if (now - lastSent >= TimeSpan.FromSeconds(Math.Max(15, cfg.PresenceSeconds)))
            await SendAsync(cfg, ReportReasons.Presence, true, false, ct);
    }

    void RememberSnapshot(Snapshot s)
    {
        st.LastCollect = DateTimeOffset.Now;
        st.LastIp = s.Ip; st.LastUser = s.User; st.LastMac = s.Mac; st.SessionNote = s.SessionNote;
    }

    /// <summary>Tentativas recentes mostradas na tela "Atualizar e enviar" (sem sinais de presença).</summary>
    void Attempt(bool ok, string reason, string message)
    {
        st.Attempts.Insert(0, new SendAttempt { Ts = DateTimeOffset.Now, Ok = ok, Reason = reason, Message = message.Length > 300 ? message[..300] : message });
        if (st.Attempts.Count > 10) st.Attempts.RemoveRange(10, st.Attempts.Count - 10);
    }

    /// <summary>Serviço local do Veyon neste PC. Só informa se está em execução; não prova conectividade com o Master.</summary>
    static string VeyonServiceState()
    {
        try
        {
            using var sc = new System.ServiceProcess.ServiceController("VeyonService");
            return sc.Status == System.ServiceProcess.ServiceControllerStatus.Running ? "Em execução" : "Parado";
        }
        catch (InvalidOperationException) { return "Não encontrado"; }
        catch (Exception) { return "Não verificado"; }
    }

    void Fail(string msg)
    {
        st.Status = "Falhou"; st.LastError = msg; st.Save();
        ErrorLog.Add(msg);
        log.LogWarning("{M}", msg);
    }

    /// <summary>Coleta o estado atual, persiste como pendência e envia. Retorna true quando o professor confirma a gravação.</summary>
    public async Task<bool> SendAsync(AgentConfig cfg, string reason, bool heartbeat, bool manual, CancellationToken ct)
    {
        if (!await sendLock.WaitAsync(TimeSpan.FromSeconds(manual ? 30 : 0), ct)) return false;
        try
        {
            var token = cfg.Token;
            if (token == null) { st.Status = "Aguardando credenciamento"; st.Save(); return false; }
            var snap = Collector.Collect(cfg.TeacherUrl);
            RememberSnapshot(snap);
            var now = DateTimeOffset.UtcNow;
            st.Seq = Math.Max(st.Seq + 1, now.ToUnixTimeMilliseconds());
            // sempre o estado atual (nunca reenvia IP antigo); pendência salva antes do envio
            st.Pending = new ReportDto(cfg.AgentId, Environment.MachineName, cfg.Lab, cfg.Position, snap.User, snap.Ip, snap.Mac, now, st.Seq,
                AppInfo.Version, reason, heartbeat && st.Pending == null, cfg.AutoSend, snap.SessionNote, cfg.MachineGuidHash);
            st.Status = "Enviando";
            st.Save();

            using var req = new HttpRequestMessage(HttpMethod.Post, Base(cfg) + "/api/report") { Content = JsonContent.Create(st.Pending) };
            req.Headers.Add("X-Agent-Id", cfg.AgentId);
            req.Headers.Authorization = new("Bearer", token);
            var client = Client(cfg);
            using var resp = await client.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) throw new HttpStatusException(resp.StatusCode, await ErrorText(resp, ct));
            var ack = await resp.Content.ReadFromJsonAsync<ReportAck>(ct);
            if (ack is not { Received: true }) throw new InvalidOperationException("O professor não confirmou a gravação.");

            st.Pending = null;
            st.Status = "Confirmado pelo professor";
            st.LastConfirmed = DateTimeOffset.Now;
            st.LastError = null;
            st.VeyonState = ack.VeyonState ?? (ack.VeyonApplied ? VeyonStates.Applied : VeyonStates.Pending);
            st.VeyonAvailability = string.IsNullOrEmpty(ack.VeyonAvailability) || ack.VeyonAvailability == "Sem confirmação"
                ? VeyonStates.AvailabilityUnknown : ack.VeyonAvailability;
            st.Warning = ack.Warning;
            if (!heartbeat) Attempt(true, reason, "Recebimento confirmado pelo professor.");
            st.Save();
            fails = 0; nextRetry = DateTimeOffset.MinValue; lastSent = now;
            ErrorLog.ResolveAll();
            if (manual) try { File.Delete(ForceFlag); } catch (IOException) { }
            if (ack.CollectRequested) File.WriteAllText(ForceFlag, CollectFlag);
            if (ack.UpdateAvailable) _ = Task.Run(() => CheckUpdateAsync(cfg, ct), ct);
            return true;
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            fails++;
            var delay = Math.Min(300, 5 * Math.Pow(2, Math.Min(fails, 6)));
            nextRetry = DateTimeOffset.UtcNow.AddSeconds(delay);
            var msg = e is HttpStatusException hs ? hs.Message : Tls.Explain(e, probe);
            if (manual) try { File.Delete(ForceFlag); } catch (IOException) { } // envio manual: o resultado é mostrado; não fica repetindo
            Attempt(false, reason, msg);
            Fail(msg + (cfg.AutoSend && !manual ? $" Nova tentativa em {delay:0} s." : ""));
            return false;
        }
        finally { sendLock.Release(); }
    }

    sealed class HttpStatusException(HttpStatusCode code, string message) : Exception(message)
    {
        public HttpStatusCode Code { get; } = code;
    }

    static async Task<string> ErrorText(HttpResponseMessage r, CancellationToken ct)
    {
        string? detail = null;
        try { detail = (await r.Content.ReadFromJsonAsync<ApiError>(ct))?.Error; } catch (Exception) { }
        return r.StatusCode switch
        {
            HttpStatusCode.Unauthorized => detail ?? "Credencial recusada pelo professor: faça o credenciamento novamente.",
            HttpStatusCode.Forbidden => detail ?? "O professor recusou: laboratório diferente.",
            HttpStatusCode.Conflict => detail ?? "Conflito de posição no laboratório.",
            (HttpStatusCode)429 => detail ?? "Muitas tentativas. Aguarde 1 minuto.",
            _ => detail ?? $"O professor respondeu HTTP {(int)r.StatusCode}."
        };
    }

    public async Task<PairOutcome> SendNowAsync(CancellationToken ct)
    {
        var cfg = AgentConfig.Load();
        if (!cfg.Paired) return new PairOutcome { Ok = false, Message = "Este computador ainda não foi credenciado." };
        var ok = await SendAsync(cfg, ReportReasons.Manual, false, true, ct);
        return new PairOutcome { Ok = ok, Message = ok ? "Dados atualizados e confirmados pelo professor." : st.LastError ?? "Falhou.", SendStatus = st.Status, LastError = st.LastError };
    }

    // ------------------------------------------------------------------ atualização
    async Task CheckUpdateAsync(AgentConfig cfg, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, Base(cfg) + "/api/update/manifest");
            req.Headers.Add("X-Agent-Id", cfg.AgentId);
            req.Headers.Authorization = new("Bearer", cfg.Token);
            using var resp = await Client(cfg).SendAsync(req, ct);
            if (resp.StatusCode == HttpStatusCode.NoContent) return;
            if (!resp.IsSuccessStatusCode) throw new InvalidOperationException(await ErrorText(resp, ct));
            var m = await resp.Content.ReadFromJsonAsync<SignedManifest>(ct);
            if (m != null && UpdateOffered != null) await UpdateOffered(m, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            st.UpdateStatus = "Falha ao consultar atualização: " + e.Message; st.Save();
        }
    }

    public async Task<Stream> DownloadAsync(AgentConfig cfg, long updateId, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, Base(cfg) + $"/api/update/package/{updateId}");
        req.Headers.Add("X-Agent-Id", cfg.AgentId);
        req.Headers.Authorization = new("Bearer", cfg.Token);
        var resp = await Client(cfg).SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException(await ErrorText(resp, ct));
        return await resp.Content.ReadAsStreamAsync(ct);
    }

    public async Task ReportUpdateAsync(AgentConfig cfg, UpdateResult r, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, Base(cfg) + "/api/update/result") { Content = JsonContent.Create(r) };
        req.Headers.Add("X-Agent-Id", cfg.AgentId);
        req.Headers.Authorization = new("Bearer", cfg.Token);
        using var resp = await Client(cfg).SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
    }

    public void SetUpdateStatus(string s) { st.UpdateStatus = s; st.Save(); }

    /// <summary>Avisa o professor que a instalação começou. Professores anteriores ao 2.2 não têm a rota: ignorado.</summary>
    public async Task ReportStageAsync(AgentConfig cfg, long updateId, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, Base(cfg) + "/api/update/stage") { Content = JsonContent.Create(new UpdateStage(updateId, "instalando")) };
            req.Headers.Add("X-Agent-Id", cfg.AgentId);
            req.Headers.Authorization = new("Bearer", cfg.Token);
            using var resp = await Client(cfg).SendAsync(req, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException) { }
    }

    // ------------------------------------------------------------------ administração
    public StudentStatus Status()
    {
        AgentConfig? cfg = null;
        string? cfgError = null;
        try { cfg = AgentConfig.Load(); } catch (Exception e) { cfgError = e.Message; }
        var s = new StudentStatus
        {
            Paired = cfg?.Paired ?? false,
            LinkImported = cfg?.LinkImportedAt != null,
            CertificatePinned = !string.IsNullOrEmpty(cfg?.TeacherCertSha256),
            Lab = cfg?.Lab ?? "", LabName = cfg?.LabName ?? (cfg != null ? "Laboratório " + cfg.Lab : ""),
            TeacherName = cfg?.TeacherName ?? (cfg != null && Uri.TryCreate(cfg.TeacherUrl, UriKind.Absolute, out var u) ? u.Host : ""),
            TeacherUrl = cfg?.TeacherUrl ?? "", Position = cfg?.Position ?? "", WindowsName = Environment.MachineName,
            User = st.LastUser, UserNote = st.LastUser == null ? "Sem usuário conectado" + (st.SessionNote != null ? " — " + st.SessionNote : "") : st.SessionNote,
            Ip = st.LastIp, Mac = st.LastMac, LastCollect = st.LastCollect, LastConfirmed = st.LastConfirmed,
            SendStatus = st.Status, LastError = cfgError ?? st.LastError, VeyonState = st.VeyonState, VeyonAvailability = st.VeyonAvailability,
            AutoSend = cfg?.AutoSend ?? true, SendOnStart = cfg?.SendOnStart ?? true, SendOnLogon = cfg?.SendOnLogon ?? true,
            SendOnIpChange = cfg?.SendOnIpChange ?? true, PresenceSeconds = cfg?.PresenceSeconds ?? 60,
            AgentIdShort = cfg == null ? "" : cfg.AgentId[..8], PendingReport = st.Pending != null, UpdateStatus = st.UpdateStatus,
            Errors = ErrorLog.Load().OrderByDescending(e => e.Ts).ToList(),
            VeyonService = VeyonServiceState(),
            Attempts = st.Attempts.ToList()
        };
        if (st.Warning != null && s.LastError == null) s.LastError = st.Warning;
        return s;
    }

    public void SaveSettings(StudentSettings ss)
    {
        var cfg = AgentConfig.Load();
        cfg.AutoSend = ss.AutoSend;
        cfg.SendOnStart = ss.SendOnStart;
        cfg.SendOnLogon = ss.SendOnLogon;
        cfg.SendOnIpChange = ss.SendOnIpChange;
        cfg.PresenceSeconds = Math.Clamp(ss.PresenceSeconds, 15, 3600);
        cfg.Save();
        if (cfg.AutoSend) { nextRetry = DateTimeOffset.MinValue; File.WriteAllText(ForceFlag, "1"); } // comunica a mudança ao professor
    }

    /// <summary>
    /// Importa o arquivo de ligação. Mesmo professor (ReceiverId): atualiza endereço/certificado e mantém o pareamento.
    /// Outro professor/laboratório: só com confirmação explícita do administrador, e exige novo pareamento.
    /// </summary>
    public object ImportLink(string content, bool confirmChange)
    {
        var link = LinkFile.Parse(content);
        var cfg = AgentConfig.Load();
        bool sameTeacher = cfg.ReceiverId != null ? cfg.ReceiverId == link.ReceiverId
            : string.Equals(cfg.Lab, link.LabId, StringComparison.OrdinalIgnoreCase);
        if (cfg.Paired && !sameTeacher && !confirmChange)
            return new
            {
                needsConfirmation = true,
                message = $"Este computador está ligado ao laboratório {cfg.Lab} ({cfg.TeacherName ?? cfg.TeacherUrl}). O arquivo é do laboratório {link.LabId} ({link.TeacherName}). " +
                          "Trocar exige novo credenciamento. Os dados locais e a identidade deste PC são preservados."
            };
        if (cfg.Paired && !sameTeacher) cfg.TokenProtected = null;
        if (cfg.Paired && sameTeacher && !string.Equals(cfg.Lab, link.LabId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("O arquivo indica outro laboratório para o mesmo professor. Gere um arquivo novo no THC.");
        var changed = cfg.TeacherUrl != link.Url || cfg.TeacherCertSha256 != link.CertificateSha256;
        cfg.TeacherUrl = link.Url;
        cfg.Lab = link.LabId;
        cfg.LabName = link.LabName;
        cfg.TeacherName = link.TeacherName;
        cfg.ReceiverId = link.ReceiverId;
        cfg.TeacherCertSha256 = link.CertificateSha256;
        if (!string.IsNullOrEmpty(link.UpdatePublicKey)) cfg.UpdatePublicKey = link.UpdatePublicKey;
        cfg.LinkImportedAt = DateTimeOffset.Now;
        cfg.Save();
        http?.Dispose(); http = null;
        if (cfg.Paired && changed) File.WriteAllText(ForceFlag, "1");
        return new
        {
            needsConfirmation = false,
            paired = cfg.Paired,
            message = cfg.Paired ? (changed ? "Ligação atualizada (endereço/certificado). O pareamento foi mantido." : "Ligação já estava atualizada.")
                                 : "Arquivo importado. Informe o número do PC e o código.",
            lab = link.LabId, labName = link.LabName, teacher = link.TeacherName, url = link.Url
        };
    }

    public async Task<PairOutcome> PairAsync(string position, string code, CancellationToken ct)
    {
        var cfg = AgentConfig.Load();
        if (cfg.LinkImportedAt == null && string.IsNullOrEmpty(cfg.TeacherCertSha256) && cfg.TeacherUrl.Contains("professor-lab-a", StringComparison.OrdinalIgnoreCase) && cfg.Lab == "A")
            return new PairOutcome { Ok = false, Message = "Importe primeiro o arquivo de ligação (.vsync) do laboratório." };
        var pos = Naming.NormalizePosition(position);
        if (string.IsNullOrWhiteSpace(position) || pos.Length > 16) return new PairOutcome { Ok = false, Message = "Informe o número do PC (ex.: 3 ou PC03)." };
        code = new string((code ?? "").Where(char.IsDigit).ToArray());
        if (code.Length != 8) return new PairOutcome { Ok = false, Message = "O código tem 8 dígitos." };

        await sendLock.WaitAsync(ct);
        PairResponse? p;
        try
        {
            var r = await Client(cfg).PostAsJsonAsync(Base(cfg) + "/api/pair",
                new PairRequest(code, cfg.AgentId, cfg.Lab, Environment.MachineName, pos, AppInfo.Version, cfg.ReceiverId), ct);
            if (!r.IsSuccessStatusCode) return new PairOutcome { Ok = false, Message = await ErrorText(r, ct) };
            p = await r.Content.ReadFromJsonAsync<PairResponse>(ct);
            if (p == null) return new PairOutcome { Ok = false, Message = "Resposta vazia do professor." };
            if (!string.Equals(p.Lab, cfg.Lab, StringComparison.OrdinalIgnoreCase))
                return new PairOutcome { Ok = false, Message = $"O professor respondeu como laboratório {p.Lab}; este PC está configurado para {cfg.Lab}. Pareamento não concluído." };
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return new PairOutcome { Ok = false, Message = Tls.Explain(e, probe) };
        }
        finally { sendLock.Release(); }

        cfg.SetToken(p.Token);
        cfg.Position = pos; // não renomeia o Windows; só o número do posto
        if (p.TeacherName != null) cfg.TeacherName = p.TeacherName;
        if (p.ReceiverId != null) cfg.ReceiverId = p.ReceiverId;
        if (!string.IsNullOrEmpty(p.UpdatePublicKey) && string.IsNullOrEmpty(cfg.UpdatePublicKey)) cfg.UpdatePublicKey = p.UpdatePublicKey;
        cfg.Save();
        st.Pending = null;
        startHandled = true;

        // Coleta e envio inicial: o resultado real é mostrado na interface.
        var sent = await SendAsync(cfg, ReportReasons.Pairing, false, true, ct);
        return new PairOutcome
        {
            Ok = sent,
            Message = sent ? "Credenciado e confirmado pelo professor." : "Credenciado, mas o primeiro envio falhou: " + st.LastError,
            SendStatus = st.Status, LastError = st.LastError
        };
    }
}
