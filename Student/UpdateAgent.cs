using System.Diagnostics;
using System.Text.Json;
using VeyonSync.Shared;

namespace VeyonSync.Student;

/// <summary>
/// Atualização pela rede. O serviço valida assinatura/integridade/versão, baixa para área temporária protegida
/// e inicia uma cópia de si mesmo fora da pasta de instalação (atualizador auxiliar), que faz backup,
/// para o serviço, substitui arquivos, reinicia, confere a versão e reverte em caso de falha.
/// </summary>
public sealed class UpdateAgent(Agent agent, ILogger<UpdateAgent> log)
{
    static string Work => Path.Combine(AppInfo.StudentData, "update-tmp");
    static string Runner => Path.Combine(AppInfo.StudentData, "update-runner");
    static string BackupRoot => Path.Combine(AppInfo.StudentData, "update-backup");
    public static string ResultFile => Path.Combine(AppInfo.StudentData, "update-result.json");
    static readonly SemaphoreSlim busy = new(1, 1);

    public async Task HandleAsync(SignedManifest sm, CancellationToken ct)
    {
        if (!await busy.WaitAsync(0, ct)) return;
        var cfg = AgentConfig.Load();
        try
        {
            if (string.IsNullOrEmpty(cfg.UpdatePublicKey))
                throw new InvalidDataException("Sem chave pública de atualização: importe o arquivo de ligação atual do THC.");
            var m = UpdateSigning.Verify(sm.ManifestJson, sm.Signature, cfg.UpdatePublicKey)
                    ?? throw new InvalidDataException("Assinatura da atualização inválida: pacote recusado.");
            if (m.Product != "STD") throw new InvalidDataException("Pacote não é do STD.");
            if (cfg.ReceiverId != null && m.ReceiverId != cfg.ReceiverId) throw new InvalidDataException("Atualização assinada por outro professor.");
            if (AppInfo.CompareVersions(m.Version, AppInfo.Version) <= 0) throw new InvalidDataException($"Versão {m.Version} não é mais nova que a instalada ({AppInfo.Version}).");
            if (AppInfo.CompareVersions(AppInfo.Version, m.MinVersion) < 0) throw new InvalidDataException($"Esta atualização exige a versão {m.MinVersion} ou superior.");
            if (m.Size <= 0 || m.Size > 1_500_000_000) throw new InvalidDataException("Tamanho do pacote inválido.");

            agent.SetUpdateStatus($"Baixando atualização {m.Version}…");
            Directory.CreateDirectory(Work);
            var zip = Path.Combine(Work, $"pkg-{sm.UpdateId}.zip");
            await using (var src = await agent.DownloadAsync(cfg, sm.UpdateId, ct))
            await using (var dst = File.Create(zip))
            {
                var buf = new byte[81920];
                long total = 0;
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    total += n;
                    if (total > m.Size) throw new InvalidDataException("Pacote maior que o informado no manifesto.");
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                }
            }
            if (new FileInfo(zip).Length != m.Size || !string.Equals(UpdateSigning.FileSha256(zip), m.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Integridade do pacote não confere (SHA-256).");
            var info = PackageInfo.Read(zip);
            if (info.Product != "STD" || info.Version != m.Version) throw new InvalidDataException("Conteúdo do pacote não corresponde ao manifesto.");
            await agent.ReportStageAsync(cfg, sm.UpdateId, ct);

            // Atualizador auxiliar: cópia do executável atual fora da pasta que será substituída.
            var install = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            if (Directory.Exists(Runner)) Directory.Delete(Runner, true);
            UpdateRunner.CopyDir(install, Runner, skipSub: "App");
            var exe = Path.Combine(Runner, "VeyonSync.Student.exe");
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Runner };
            foreach (var a in new[] { "--apply-update", zip, sm.UpdateId.ToString(), m.Version, install }) psi.ArgumentList.Add(a);
            agent.SetUpdateStatus($"Aplicando atualização {m.Version}: o serviço será reiniciado.");
            Process.Start(psi);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogWarning("Atualização recusada: {M}", e.Message);
            agent.SetUpdateStatus("Atualização recusada: " + e.Message);
            ErrorLog.Add("Atualização: " + e.Message);
            try { await agent.ReportUpdateAsync(cfg, new UpdateResult(sm.UpdateId, false, AppInfo.Version, e.Message), ct); } catch (Exception) { }
        }
        finally { busy.Release(); }
    }

    /// <summary>Envia ao professor o resultado gravado pelo atualizador auxiliar.</summary>
    public async Task ReportPendingResultAsync(CancellationToken ct)
    {
        if (!File.Exists(ResultFile)) return;
        try
        {
            var r = JsonSerializer.Deserialize<UpdateResult>(File.ReadAllText(ResultFile));
            if (r == null) { File.Delete(ResultFile); return; }
            agent.SetUpdateStatus(r.Ok ? $"Atualizado para {r.Version}." : "Atualização revertida: " + r.Detail);
            await agent.ReportUpdateAsync(AgentConfig.Load(), r, ct);
            File.Delete(ResultFile);
        }
        catch (Exception e) when (e is not OperationCanceledException) { log.LogInformation("Resultado da atualização ainda não enviado: {M}", e.Message); }
    }

    static void CopyDir(string from, string to, string? skipSub = null)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
        foreach (var d in Directory.GetDirectories(from))
        {
            var name = Path.GetFileName(d);
            if (skipSub != null && string.Equals(name, skipSub, StringComparison.OrdinalIgnoreCase)) continue;
            CopyDir(d, Path.Combine(to, name));
        }
    }

    // ===================================================== modo atualizador auxiliar
    /// <summary>Distribuição THC → STD: usa o mesmo atualizador (backup de programa e dados, reversão) das Releases.</summary>
    public static int ApplyMode(string zip, long updateId, string version, string target)
    {
        var spec = UpdateRunner.For("Student");
        Directory.CreateDirectory(Path.Combine(AppInfo.StudentData, "logs"));
        var logFile = Path.Combine(AppInfo.StudentData, "logs", $"update-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        void Log(string s) { try { File.AppendAllText(logFile, $"{DateTime.Now:HH:mm:ss} {s}{Environment.NewLine}"); } catch (Exception) { } }
        var r = UpdateRunner.Apply(spec, zip, version, "Rede (THC)", Log);
        var result = new UpdateResult(updateId, r.Ok, r.Ok ? version : AppInfo.Version, r.Detail);
        try { File.WriteAllText(ResultFile, JsonSerializer.Serialize(result)); } catch (Exception) { }
        try { File.WriteAllText(UpdateRunner.ResultFile(spec), JsonSerializer.Serialize(r)); } catch (Exception) { }
        try { File.Delete(zip); } catch (Exception) { }
        return r.Ok ? 0 : 1;
    }
}
