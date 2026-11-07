using VeyonSync.Shared;

namespace VeyonSync.Student;

public class AgentWorker(Agent agent, UpdateAgent updater, ReleaseService release, ILogger<AgentWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        agent.UpdateOffered += updater.HandleAsync;
        _ = Task.Run(() => Ipc.RunServerAsync(AppInfo.StudentPipe, HandleAsync, m => log.LogWarning("{M}", m), ct), ct);
        _ = Task.Run(() => release.AutoCheckLoopAsync(ct), ct);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        var resultCheck = DateTime.MinValue;
        do
        {
            try
            {
                await agent.TickAsync(ct);
                agent.ConsumeNetworkChanged();
                if (DateTime.UtcNow - resultCheck > TimeSpan.FromMinutes(1))
                {
                    resultCheck = DateTime.UtcNow;
                    await updater.ReportPendingResultAsync(ct);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException) { log.LogError(e, "Falha no ciclo do agente"); }
        } while (await timer.WaitForNextTickAsync(ct));
    }

    async Task<object?> HandleAsync(IpcRequest req, CancellationToken ct)
    {
        var a = req.Args;
        switch (req.Cmd)
        {
            case "status": return agent.Status();
            case "send": return await agent.SendNowAsync(ct);
            case "settings.save":
                agent.SaveSettings(IpcJson.From<StudentSettings>(a) ?? throw new IpcException("Configurações ausentes."));
                return agent.Status();
            case "link.inspect":
                var l = LinkFile.Parse(a.Str("content"));
                return new { lab = l.LabId, labName = l.LabName, teacher = l.TeacherName, url = l.Url, sha = l.CertificateSha256 };
            case "link.import": return agent.ImportLink(a.Str("content"), a.Bool("confirmChange"));
            case "pair": return await agent.PairAsync(a.Str("position"), a.Str("code"), ct);
            case "errors.clear": ErrorLog.ResolveAll(); return true;
            case "release.status": return release.Status();
            case "release.settings": return release.SaveSettings(IpcJson.From<ReleaseSettings>(a) ?? new ReleaseSettings());
            case "release.check": return await release.CheckAsync(ct);
            case "release.install": return await release.InstallFromGitHubAsync(ct);
            case "release.installFile": return release.InstallFromFile(a.Str("path"));
            default: throw new IpcException("Comando desconhecido: " + req.Cmd);
        }
    }
}
