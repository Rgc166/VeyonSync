using System.Text;
using System.Text.Json.Nodes;
using VeyonSync.Shared;
using VeyonSync.Teacher.Veyon;

namespace VeyonSync.Teacher;

/// <summary>Comandos administrativos recebidos pelo pipe local (somente administradores elevados e SYSTEM).</summary>
public sealed class AdminHandler(TeacherContext ctx, VeyonEngine engine, Peers peers, Updates updates, ReleaseService release)
{
    public async Task<object?> HandleAsync(IpcRequest req, CancellationToken ct)
    {
        var a = req.Args;
        switch (req.Cmd)
        {
            case "overview": return Overview();
            case "settings.get": return ctx.Settings();
            case "settings.save":
                ctx.SaveSettings(IpcJson.From<TeacherSettings>(a) ?? throw new IpcException("Configurações ausentes."));
                return ctx.Settings();
            case "code.new":
                var code = ctx.Db.NewPairingCode();
                return new { code, expires = ctx.Db.CodeExpires() };
            case "code.status": return new { expires = ctx.Db.CodeExpires() };
            case "link.preview": return BuildLinkPreview();
            case "link.export": return BuildLinkExport();
            case "collect.request":
                var ids = IpcJson.From<List<string>>(a?["agentIds"]);
                ctx.Db.RequestCollect(ids is { Count: > 0 } ? ids : null);
                return ctx.Db.LastCollect(ctx.Settings().RecentSeconds).round;
            case "machine.friendly": ctx.Db.SetFriendlyOverride(a.Str("agentId"), a.Str("name")); return true;
            case "machine.remove": ctx.Db.RemoveMachine(a.Str("agentId")); return true;
            case "history.query": return ctx.Db.History(IpcJson.From<HistoryQuery>(a) ?? new HistoryQuery());
            case "history.kinds": return ctx.Db.HistoryKinds();
            case "errors.list": return ctx.Db.Errors(a.Bool("includeResolved"));
            case "errors.resolve": ctx.Db.ResolveError(a.Long("id")); return true;
            case "map.get": return ctx.Db.GetMap(a.Str("lab", ctx.Lab));
            case "map.save": ctx.Db.SaveMap(IpcJson.From<LabMap>(a) ?? throw new IpcException("Mapa ausente.")); return true;
            case "veyon.status": return await VeyonStatus(ct);
            case "veyon.preview": return await engine.PreviewAsync(a.Bool("onlyPending", true), IpcJson.From<List<string>>(a?["agentIds"]), ct);
            case "veyon.apply": return await engine.ApplyAsync(a.Bool("retryFailedOnly"), IpcJson.From<List<string>>(a?["agentIds"]), ct);
            case "veyon.backup": return await engine.BackupAsync("Backup manual", ct);
            case "veyon.backups": return ctx.Db.Backups();
            case "veyon.restore": await engine.RestoreAsync(a.Long("id"), ct); return true;
            case "batch.preview":
                return await peers.BatchPreviewAsync(a.Bool("includeLocal", true), IpcJson.From<List<long>>(a?["peerIds"]) ?? new List<long>(), ct);
            case "batch.apply":
                return await peers.BatchApplyAsync(a.Bool("includeLocal", true), IpcJson.From<List<long>>(a?["peerIds"]) ?? new List<long>(), a.Bool("retryFailedOnly"), ct);
            case "peers.list": return ctx.Db.Peers();
            case "peers.add": return await peers.AddAsync(a.Str("linkContent"), a.Str("code"), ct);
            case "peers.enable": ctx.Db.SetPeerEnabled(a.Long("id"), a.Bool("enabled", true)); return true;
            case "peers.remove": ctx.Db.RemovePeer(a.Long("id")); return true;
            case "updates.list": return ctx.Db.Updates();
            case "updates.publish":
                return updates.Publish(a.Str("packagePath"), IpcJson.From<List<string>>(a?["agentIds"]) ?? new List<string>());
            case "updates.retry": ctx.Db.RetryFailedUpdates(a.Long("id")); return true;
            case "diagnostics": return Diagnostics();
            case "release.status": return release.Status();
            case "release.settings": return release.SaveSettings(IpcJson.From<ReleaseSettings>(a) ?? new ReleaseSettings());
            case "release.check": return await release.CheckAsync(ct);
            case "release.install": return await release.InstallFromGitHubAsync(ct);
            case "release.installFile": return release.InstallFromFile(a.Str("path"));
            case "release.stdLatest": return await release.DownloadStdFromGitHubAsync(ct);
            default: throw new IpcException("Comando desconhecido: " + req.Cmd);
        }
    }

    TeacherOverview Overview()
    {
        var s = ctx.Settings();
        var machines = ctx.Db.Machines(s.NameTemplate, s.RecentSeconds);
        var cli = VeyonCli.Locate(s.VeyonCliPath);
        return new TeacherOverview
        {
            Lab = ctx.Lab, LabName = s.LabName, TeacherName = ctx.TeacherName, Url = ctx.Url(s), RecentSeconds = s.RecentSeconds,
            NameTemplate = s.NameTemplate, Machines = machines,
            Recent = machines.Count(m => m.Recent), NotRecent = machines.Count(m => !m.Recent),
            VeyonPending = machines.Count(m => m.VeyonState != VeyonStates.Applied),
            ErrorsPending = ctx.Db.Errors(false).Count,
            VeyonCliFound = cli != null, VeyonCliPath = cli,
            LastCollect = ctx.Db.LastCollect(s.RecentSeconds).round,
            RecentEvents = ctx.Db.RecentEvents(8),
            Map = ctx.Db.GetMap(ctx.Lab)
        };
    }

    LinkPreview BuildLinkPreview()
    {
        var s = ctx.Settings();
        using var cert = Keys.TlsCertificate(ctx.Thumbprint);
        var p = new LinkPreview
        {
            Lab = ctx.Lab, LabName = s.LabName, TeacherName = ctx.TeacherName, Url = ctx.Url(s),
            CertNames = Tls.Names(cert).ToList(), CertSha256 = Tls.Sha256Hex(cert.RawData), CertNotAfter = cert.NotAfter
        };
        if (!Tls.MatchesHost(cert, s.PublicHost))
            p.Warnings.Add($"O endereço '{s.PublicHost}' não está entre os nomes do certificado ({string.Join(", ", p.CertNames)}). " +
                           "Os alunos vão recusar a conexão. Use um desses nomes em Configurações ou reinstale o professor para gerar um certificado novo.");
        if (!string.Equals(s.PublicHost, Environment.MachineName, StringComparison.OrdinalIgnoreCase) && Tls.MatchesHost(cert, s.PublicHost))
            p.Warnings.Add("O endereço público é diferente do nome atual deste Windows; confira se os alunos conseguem resolvê-lo.");
        if (!Tls.MatchesHost(cert, Environment.MachineName))
            p.Warnings.Add($"Este computador se chama {Environment.MachineName}, mas o certificado foi emitido para outro nome (o PC foi renomeado). Reinstale o professor com a opção de novo certificado e gere novos arquivos de ligação.");
        if (cert.NotAfter < DateTime.Now.AddDays(60))
            p.Warnings.Add($"O certificado vence em {cert.NotAfter:d}.");
        return p;
    }

    object BuildLinkExport()
    {
        var s = ctx.Settings();
        using var cert = Keys.TlsCertificate(ctx.Thumbprint);
        var link = LinkFile.Create(ctx.Db.ReceiverId(), ctx.TeacherName, ctx.Lab, s.LabName, ctx.Url(s), cert, Keys.UpdatePublicKey());
        ctx.Db.Hist("", ctx.Lab, ctx.TeacherName, "Ação administrativa", "Arquivo de ligação gerado");
        return new { fileName = LinkFile.SafeFileName(ctx.Lab), content = link.Serialize() };
    }

    async Task<object> VeyonStatus(CancellationToken ct)
    {
        var path = VeyonCli.Locate(ctx.Settings().VeyonCliPath);
        var last = ctx.Db.Backups().FirstOrDefault();
        if (path == null) return new { found = false, path = (string?)null, version = (string?)null, check = "veyon-cli.exe não encontrado.", ok = false, busy = engine.Busy, lastBackup = last };
        var cli = new VeyonCli(path);
        var (ok, detail) = await cli.CheckAsync(ct);
        return new { found = true, path, version = cli.Version(), check = ok ? "Comandos necessários disponíveis." : detail, ok, busy = engine.Busy, lastBackup = last };
    }

    string Diagnostics()
    {
        var s = ctx.Settings();
        var sb = new StringBuilder();
        sb.AppendLine($"{AppInfo.TeacherDisplayName} {AppInfo.Version}");
        sb.AppendLine($"Computador: {Environment.MachineName}  Laboratório: {ctx.Lab} ({s.LabName})");
        sb.AppendLine($"Endereço publicado: {ctx.Url(s)}  Porta: {ctx.Port}");
        try
        {
            using var cert = Keys.TlsCertificate(ctx.Thumbprint);
            sb.AppendLine($"Certificado: {cert.Subject}  Nomes: {string.Join(", ", Tls.Names(cert))}  Validade: {cert.NotAfter:d}");
            sb.AppendLine($"SHA-256: {Tls.Sha256Hex(cert.RawData)}");
        }
        catch (Exception e) { sb.AppendLine("Certificado: ERRO " + e.Message); }
        sb.AppendLine($"veyon-cli: {VeyonCli.Locate(s.VeyonCliPath) ?? "não encontrado"}");
        var m = ctx.Db.Machines(s.NameTemplate, s.RecentSeconds);
        sb.AppendLine($"Computadores: {m.Count} (recentes {m.Count(x => x.Recent)})");
        sb.AppendLine($"Erros pendentes: {ctx.Db.Errors(false).Count}");
        sb.AppendLine("Tokens, códigos e chaves privadas não são incluídos neste diagnóstico.");
        return sb.ToString();
    }
}
