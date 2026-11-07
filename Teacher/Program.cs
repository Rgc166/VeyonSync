using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using VeyonSync.Shared;
using VeyonSync.Teacher;
using VeyonSync.Teacher.Veyon;

// Atualizador auxiliar (cópia do serviço fora da pasta de instalação).
if (args.Length == 4 && args[0] == "--apply-release")
    Environment.Exit(UpdateRunner.Run("Teacher", args[1], args[2], args[3]));

// Comandos de linha de comando da Etapa 1 preservados.
if (args.Contains("--new-code"))
{
    var db0 = new Db();
    Console.WriteLine($"Código de pareamento (vale 15 min): {db0.NewPairingCode()}");
    return;
}
if (args.Contains("--request-collect")) { new Db().RequestCollectAll(); Console.WriteLine("Solicitação criada; agentes atendem na próxima comunicação."); return; }
if (args.Contains("--health-check")) { new Db(); Console.WriteLine("Banco OK (" + AppInfo.Version + ")."); return; }

var b = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
b.Host.UseWindowsService(o => o.ServiceName = AppInfo.TeacherService);
b.Services.AddSingleton<Db>();
b.Services.AddSingleton<TeacherContext>();
b.Services.AddSingleton<VeyonEngine>();
b.Services.AddSingleton<Peers>();
b.Services.AddSingleton<Updates>();
b.Services.AddSingleton<AdminHandler>();
b.Services.AddSingleton(sp => new ReleaseService("Teacher", m => sp.GetRequiredService<ILogger<ReleaseService>>().LogInformation("{M}", m)));
b.Services.AddHostedService<TeacherBackground>();
b.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNameCaseInsensitive = true);

var thumb = b.Configuration["Tls:Thumbprint"];
if (string.IsNullOrWhiteSpace(thumb)) throw new InvalidOperationException("Tls:Thumbprint ausente em appsettings.json");
b.WebHost.ConfigureKestrel(k =>
{
    k.Limits.MaxRequestBodySize = 1024 * 1024;
    k.ListenAnyIP(b.Configuration.GetValue("Port", 5443), l => l.UseHttps(h => h.ServerCertificate = Keys.TlsCertificate(thumb)));
});

var app = b.Build();
var ctx = app.Services.GetRequiredService<TeacherContext>();
var db = ctx.Db;
var lab = ctx.Lab;
var macRx = new Regex("^([0-9A-F]{2}-){5}[0-9A-F]{2}$");
var limiter = new AttemptLimiter(5, TimeSpan.FromMinutes(1));

static IResult Err(int status, string msg) => Results.Json(new ApiError(msg), statusCode: status);
static string Ip(HttpContext h) => h.Connection.RemoteIpAddress?.ToString() ?? "?";
static string Bearer(HttpRequest r) { var v = r.Headers.Authorization.ToString(); return v.StartsWith("Bearer ", StringComparison.Ordinal) ? v[7..].Trim() : ""; }

bool AgentAuth(HttpRequest req, string agentId) =>
    req.Headers.TryGetValue("X-Agent-Id", out var id) && id == agentId && db.CheckToken(agentId, Bearer(req), lab);

app.MapGet("/", () => Results.Text($"{AppInfo.TeacherShort} {AppInfo.Version} — laboratório {lab}"));

// ------------- agentes (STD) -------------
app.MapPost("/api/pair", (HttpContext h, PairRequest r) =>
{
    if (!limiter.Allow("pair:" + Ip(h))) return Err(429, "Muitas tentativas de código. Aguarde 1 minuto.");
    if (!string.Equals(r.Lab, lab, StringComparison.OrdinalIgnoreCase))
        return Err(403, $"Este professor ({ctx.TeacherName}) atende o laboratório {lab}, mas o aluno está configurado para o laboratório {r.Lab}. Use o arquivo de ligação correto.");
    if (r.ReceiverId != null && r.ReceiverId != db.ReceiverId())
        return Err(403, "O arquivo de ligação usado pertence a outra instalação de professor. Gere um novo arquivo neste THC.");
    if (!Guid.TryParse(r.AgentId, out _)) return Err(400, "Identificador do agente inválido.");
    if (string.IsNullOrWhiteSpace(r.Code) || r.Code.Length > 16) return Err(401, "Código inválido.");
    var res = db.Pair(r.Code.Trim(), r.AgentId, lab, r.Position, r.MachineName.Length > 63 ? r.MachineName[..63] : r.MachineName);
    if (res.Token == null)
    {
        if (res.Status == 401) limiter.Fail("pair:" + Ip(h));
        return Err(res.Status, res.Error ?? "Recusado.");
    }
    return Results.Ok(new PairResponse(res.Token, lab, ctx.TeacherName, db.ReceiverId(), Keys.UpdatePublicKey()));
});

app.MapPost("/api/report", (HttpRequest req, ReportDto d) =>
{
    if (!AgentAuth(req, d.AgentId)) return Err(401, "Credencial do agente inválida: pareie novamente.");
    if (!string.Equals(d.Lab, lab, StringComparison.OrdinalIgnoreCase)) return Err(403, $"Este professor atende o laboratório {lab}.");
    if (d.MachineName.Length is 0 or > 63 || d.Position.Length > 64 || (d.User?.Length ?? 0) > 256 || (d.SessionNote?.Length ?? 0) > 256)
        return Err(400, "Campos inválidos.");
    if (d.Ip != null && !IPAddress.TryParse(d.Ip, out _)) return Err(400, "IP inválido.");
    if (d.Mac != null && !macRx.IsMatch(d.Mac)) return Err(400, "MAC inválido.");
    return Results.Ok(db.Upsert(d));
});

app.MapGet("/api/update/manifest", (HttpRequest req) =>
{
    var agent = req.Headers["X-Agent-Id"].ToString();
    if (!AgentAuth(req, agent)) return Err(401, "Credencial inválida.");
    var m = app.Services.GetRequiredService<Updates>().PendingFor(agent);
    return m == null ? Results.NoContent() : Results.Ok(m);
});

app.MapGet("/api/update/package/{id:long}", (HttpRequest req, long id) =>
{
    var agent = req.Headers["X-Agent-Id"].ToString();
    if (!AgentAuth(req, agent)) return Err(401, "Credencial inválida.");
    if (!db.IsTarget(id, agent)) return Err(403, "Esta atualização não foi autorizada para este computador.");
    var p = db.PendingUpdateFor(agent);
    if (p == null || p.Value.id != id || !File.Exists(p.Value.file)) return Err(404, "Pacote indisponível.");
    db.UpdateTarget(id, agent, "baixando", "Pacote enviado ao agente");
    return Results.File(p.Value.file, "application/zip");
});

app.MapPost("/api/update/result", (HttpRequest req, UpdateResult r) =>
{
    var agent = req.Headers["X-Agent-Id"].ToString();
    if (!AgentAuth(req, agent)) return Err(401, "Credencial inválida.");
    if (!db.IsTarget(r.UpdateId, agent)) return Err(403, "Atualização desconhecida.");
    var detail = (r.Detail ?? "").Length > 500 ? r.Detail![..500] : r.Detail ?? "";
    db.UpdateTarget(r.UpdateId, agent, r.Ok ? "concluída" : "falhou", (r.Ok ? $"Atualizado para {r.Version}. " : "Falhou: ") + detail);
    return Results.Ok();
});

app.MapPost("/api/update/stage", (HttpRequest req, UpdateStage st) =>
{
    // 2.2: o agente avisa que conferiu o pacote e começou a instalar (transferência ≠ instalação ≠ confirmação).
    var agent = req.Headers["X-Agent-Id"].ToString();
    if (!AgentAuth(req, agent)) return Err(401, "Credencial inválida.");
    if (!db.IsTarget(st.UpdateId, agent)) return Err(403, "Atualização desconhecida.");
    if (st.Stage != "instalando") return Err(400, "Etapa inválida.");
    db.UpdateTarget(st.UpdateId, agent, "instalando", "Pacote transferido e conferido; instalando no computador");
    return Results.Ok();
});

// ------------- professores parceiros (THC central) -------------
string? PeerAuth(HttpRequest req) { var t = Bearer(req); return t.Length == 0 ? null : db.CheckPeerToken(t); }

app.MapPost("/api/peer/pair", (HttpContext h, PeerPairRequest r) =>
{
    if (!limiter.Allow("peer:" + Ip(h))) return Err(429, "Muitas tentativas. Aguarde 1 minuto.");
    if (string.IsNullOrWhiteSpace(r.Code) || !db.ConsumeCode(r.Code.Trim()))
    {
        limiter.Fail("peer:" + Ip(h));
        return Err(401, "Código inválido, expirado ou já usado. Gere um código novo no THC deste laboratório.");
    }
    var token = db.IssuePeerToken((r.CallerName ?? "?").Length > 63 ? r.CallerName![..63] : r.CallerName ?? "?", r.CallerLab ?? "?");
    return Results.Ok(new PeerPairResponse(token, lab, ctx.TeacherName));
});

app.MapGet("/api/peer/info", (HttpRequest req) =>
{
    if (PeerAuth(req) == null) return Err(401, "Professor não autorizado.");
    var s = ctx.Settings();
    var m = db.Machines(s.NameTemplate, s.RecentSeconds);
    return Results.Ok(new PeerInfo(lab, s.LabName, ctx.TeacherName, AppInfo.Version, m.Count, m.Count(x => x.VeyonState != VeyonStates.Applied)));
});

app.MapGet("/api/peer/veyon/preview", async (HttpRequest req, CancellationToken ct) =>
{
    if (PeerAuth(req) == null) return Err(401, "Professor não autorizado.");
    return Results.Ok(await app.Services.GetRequiredService<VeyonEngine>().PreviewAsync(true, null, ct));
});

app.MapPost("/api/peer/veyon/apply", async (HttpRequest req, PeerApplyRequest r, CancellationToken ct) =>
{
    var caller = PeerAuth(req);
    if (caller == null) return Err(401, "Professor não autorizado.");
    db.Hist("", lab, caller, "Ação administrativa", $"Integração em lote solicitada pelo professor {caller}");
    // Executa localmente, no Veyon deste professor, somente o laboratório deste professor.
    return Results.Ok(await app.Services.GetRequiredService<VeyonEngine>().ApplyAsync(r.RetryFailedOnly, null, ct));
});

app.Run();

/// <summary>Limita tentativas de código por origem.</summary>
sealed class AttemptLimiter(int max, TimeSpan window)
{
    readonly ConcurrentDictionary<string, List<DateTime>> fails = new();
    public bool Allow(string key)
    {
        if (!fails.TryGetValue(key, out var l)) return true;
        lock (l) { l.RemoveAll(t => DateTime.UtcNow - t > window); return l.Count < max; }
    }
    public void Fail(string key)
    {
        var l = fails.GetOrAdd(key, _ => new List<DateTime>());
        lock (l) l.Add(DateTime.UtcNow);
        if (fails.Count > 10000) fails.Clear();
    }
}

/// <summary>Pipe administrativo, detecção de perda de comunicação e retenção.</summary>
sealed class TeacherBackground(TeacherContext ctx, AdminHandler admin, ReleaseService release, ILogger<TeacherBackground> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        ServiceMarker.Write(AppInfo.TeacherData);
        _ = Task.Run(() => Ipc.RunServerAsync(AppInfo.TeacherPipe, admin.HandleAsync, m => log.LogWarning("{M}", m), ct), ct);
        _ = Task.Run(() => release.AutoCheckLoopAsync(ct), ct);
        var lastPrune = DateTime.MinValue;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            try
            {
                var s = ctx.Settings();
                ctx.Db.MarkLost(s.RecentSeconds);
                if (DateTime.UtcNow - lastPrune > TimeSpan.FromHours(24)) { ctx.Db.Prune(s.RetentionDays); lastPrune = DateTime.UtcNow; }
            }
            catch (Exception e) { log.LogError(e, "Manutenção periódica falhou"); }
        } while (await timer.WaitForNextTickAsync(ct));
    }
}
