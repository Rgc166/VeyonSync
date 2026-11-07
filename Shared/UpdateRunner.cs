using System.Diagnostics;
using System.ServiceProcess;
using System.Text.Json;

namespace VeyonSync.Shared;

/// <summary>Arquivo que cada serviço grava ao iniciar: o atualizador confere por ele que a nova versão subiu.</summary>
public static class ServiceMarker
{
    public sealed record Mark(string Version, DateTimeOffset StartedAt);
    static string F(string dataDir) => Path.Combine(dataDir, "service.json");

    public static void Write(string dataDir)
    {
        try
        {
            Directory.CreateDirectory(dataDir);
            File.WriteAllText(F(dataDir), JsonSerializer.Serialize(new Mark(AppInfo.Version, DateTimeOffset.Now)));
        }
        catch (Exception) { }
    }

    public static Mark? Read(string dataDir)
    {
        try { return JsonSerializer.Deserialize<Mark>(File.ReadAllText(F(dataDir))); }
        catch (Exception) { return null; }
    }
}

public sealed record UpdateRunResult(bool Ok, string FromVersion, string ToVersion, string Detail, string Source, DateTimeOffset At);

/// <summary>
/// Atualizador auxiliar. Roda a partir de uma CÓPIA do serviço fora da pasta de instalação, então pode
/// substituir os executáveis em uso. Faz backup do programa E dos dados (banco incluído), aplica, reinicia,
/// confere a versão e, se falhar, restaura programa e dados (o banco volta ao estado anterior à migração).
/// </summary>
public static class UpdateRunner
{
    public sealed record Spec(string Role, string ServiceName, string ServiceExe, string AppProcess, string Target, string DataDir);

    // Pastas de trabalho/backup que não fazem parte dos dados a preservar no backup.
    static readonly string[] DataExclude = { "logs", "update-tmp", "update-runner", "update-backup", "backups", "VeyonBackups", "Updates" };

    public static Spec For(string role) => role == "Teacher"
        ? new Spec("Teacher", AppInfo.TeacherService, "VeyonSync.Teacher.exe", "VeyonSync.Teacher.App", Path.Combine(AppInfo.ProgramRoot, "Teacher"), AppInfo.TeacherData)
        : new Spec("Student", AppInfo.StudentService, "VeyonSync.Student.exe", "VeyonSync.Student.App", Path.Combine(AppInfo.ProgramRoot, "Student"), AppInfo.StudentData);

    public static string RunnerDir(Spec s) => Path.Combine(s.DataDir, "update-runner");
    public static string ResultFile(Spec s) => Path.Combine(s.DataDir, "release-result.json");
    public static string LockFile(Spec s) => Path.Combine(s.DataDir, "update.lock");

    /// <summary>Copia o serviço instalado para fora da pasta e inicia o atualizador auxiliar.</summary>
    public static void Launch(Spec s, string payloadZip, string version, string source)
    {
        var install = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var runner = RunnerDir(s);
        if (Directory.Exists(runner)) Directory.Delete(runner, true);
        CopyDir(install, runner, skipSub: "App");
        var psi = new ProcessStartInfo(Path.Combine(runner, s.ServiceExe)) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = runner };
        foreach (var a in new[] { "--apply-release", payloadZip, version, source }) psi.ArgumentList.Add(a);
        Process.Start(psi);
    }

    public static UpdateRunResult Apply(Spec s, string payloadZip, string version, string source, Action<string> log)
    {
        var from = AppInfo.Version;
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var backup = Path.Combine(s.DataDir, "update-backup", $"{from}-{stamp}");
        bool replaced = false;
        FileStream? lockStream = null;
        try
        {
            // Impede duas atualizações ao mesmo tempo.
            try { lockStream = new FileStream(LockFile(s), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { throw new InvalidOperationException("Já existe uma atualização em andamento neste computador."); }

            var target = Path.GetFullPath(s.Target);
            if (!target.StartsWith(AppInfo.ProgramRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Destino fora de Program Files\\VeyonSync: recusado.");
            log($"Atualizando {s.Role} de {from} para {version} ({source}).");
            var staging = Path.Combine(s.DataDir, "update-tmp", "staging-" + stamp);
            var count = SafeZip.ExtractAllowed(payloadZip, staging, new[] { "Service/", "App/" });
            if (!File.Exists(Path.Combine(staging, "Service", s.ServiceExe))) throw new InvalidDataException("Pacote sem " + s.ServiceExe + ".");
            log($"{count} arquivo(s) conferidos e preparados.");

            StopService(s, log);
            KillApp(s, log);
            CopyDir(target, Path.Combine(backup, "programa"));
            BackupData(s, Path.Combine(backup, "dados"));
            log("Backup do programa e dos dados: " + backup);

            replaced = true;
            var app = Path.Combine(target, "App");
            if (Directory.Exists(app)) Directory.Delete(app, true);
            CopyDir(Path.Combine(staging, "Service"), target);
            if (Directory.Exists(Path.Combine(staging, "App"))) CopyDir(Path.Combine(staging, "App"), app);
            log("Arquivos aplicados. Iniciando o serviço (as migrações do banco rodam na inicialização)...");
            if (!StartAndConfirm(s, version, log)) throw new InvalidOperationException("O serviço não confirmou a nova versão em 90 s.");
            try { Directory.Delete(staging, true); } catch (Exception) { }
            Prune(Path.Combine(s.DataDir, "update-backup"));
            log("Sucesso.");
            return new UpdateRunResult(true, from, version, "Atualizado, serviço reiniciado e versão confirmada.", source, DateTimeOffset.Now);
        }
        catch (Exception e)
        {
            log("ERRO: " + e);
            var detail = e.Message;
            if (replaced)
            {
                try
                {
                    StopService(s, log);
                    KillApp(s, log);
                    RestoreDir(Path.Combine(backup, "programa"), s.Target);
                    RestoreData(s, Path.Combine(backup, "dados"), log);
                    var back = StartAndConfirm(s, from, log);
                    detail += back ? $" Versão {from} e dados anteriores restaurados." : " Programa e dados restaurados, mas o serviço não confirmou o reinício.";
                }
                catch (Exception re) { detail += " Falha ao restaurar: " + re.Message + " Backup em " + backup; log("ERRO na restauração: " + re); }
            }
            else
            {
                try { using var sc = new ServiceController(s.ServiceName); if (sc.Status != ServiceControllerStatus.Running) sc.Start(); } catch (Exception) { }
            }
            return new UpdateRunResult(false, from, version, detail, source, DateTimeOffset.Now);
        }
        finally
        {
            lockStream?.Dispose();
            try { File.Delete(LockFile(s)); } catch (Exception) { }
        }
    }

    static void BackupData(Spec s, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var f in Directory.GetFiles(s.DataDir)) File.Copy(f, Path.Combine(dest, Path.GetFileName(f)), true);
        foreach (var d in Directory.GetDirectories(s.DataDir))
            if (!DataExclude.Contains(Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
                CopyDir(d, Path.Combine(dest, Path.GetFileName(d)));
    }

    /// <summary>Restaura os dados ao estado anterior (inclui o banco antes de qualquer migração da nova versão).</summary>
    static void RestoreData(Spec s, string src, Action<string> log)
    {
        if (!Directory.Exists(src)) return;
        foreach (var f in Directory.GetFiles(s.DataDir))
        {
            var name = Path.GetFileName(f);
            // Arquivos do banco que a nova versão criou (WAL/SHM) não podem sobrar ao lado do banco antigo.
            if (!File.Exists(Path.Combine(src, name)) && (name.EndsWith("-wal") || name.EndsWith("-shm") || name.EndsWith(".db") || name.EndsWith(".json")))
                File.Delete(f);
        }
        CopyDir(src, s.DataDir);
        log("Dados restaurados (banco, configuração, credenciais e estado).");
    }

    static void StopService(Spec s, Action<string> log)
    {
        using var sc = new ServiceController(s.ServiceName);
        if (sc.Status != ServiceControllerStatus.Stopped)
        {
            if (sc.Status != ServiceControllerStatus.StopPending) sc.Stop();
            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60));
        }
        var name = Path.GetFileNameWithoutExtension(s.ServiceExe);
        foreach (var p in Process.GetProcessesByName(name))
            if (p.Id != Environment.ProcessId) { try { p.WaitForExit(10000); } catch (Exception) { } }
        log("Serviço parado.");
    }

    static void KillApp(Spec s, Action<string> log)
    {
        foreach (var p in Process.GetProcessesByName(s.AppProcess))
        {
            try { p.Kill(true); p.WaitForExit(5000); log("Interface fechada para atualizar."); } catch (Exception) { }
        }
    }

    static bool StartAndConfirm(Spec s, string version, Action<string> log)
    {
        var started = DateTimeOffset.Now;
        using var sc = new ServiceController(s.ServiceName);
        sc.Start();
        sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
        for (int i = 0; i < 45; i++)
        {
            Thread.Sleep(2000);
            var m = ServiceMarker.Read(s.DataDir);
            if (m != null && AppInfo.CompareVersions(m.Version, version) == 0 && m.StartedAt >= started.AddSeconds(-2))
            {
                sc.Refresh();
                if (sc.Status == ServiceControllerStatus.Running) { log($"Versão {version} em execução."); return true; }
            }
        }
        return false;
    }

    public static void CopyDir(string from, string to, string? skipSub = null)
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

    public static void RestoreDir(string backup, string target)
    {
        if (!Directory.Exists(backup)) throw new DirectoryNotFoundException("Backup ausente.");
        foreach (var f in Directory.GetFiles(target, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(target, f);
            if (!File.Exists(Path.Combine(backup, rel))) File.Delete(f);
        }
        CopyDir(backup, target);
    }

    static void Prune(string root)
    {
        if (!Directory.Exists(root)) return;
        foreach (var d in new DirectoryInfo(root).GetDirectories().OrderByDescending(d => d.CreationTimeUtc).Skip(2))
            try { d.Delete(true); } catch (Exception) { }
    }

    /// <summary>Ponto de entrada do modo atualizador (--apply-release payload versão origem).</summary>
    public static int Run(string role, string payload, string version, string source)
    {
        var s = For(role);
        var logDir = Path.Combine(s.DataDir, "logs");
        Directory.CreateDirectory(logDir);
        var logFile = Path.Combine(logDir, $"atualizacao-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        void Log(string t) { try { File.AppendAllText(logFile, $"{DateTime.Now:HH:mm:ss} {t}{Environment.NewLine}"); } catch (Exception) { } }
        var r = Apply(s, payload, version, source, Log);
        try { File.WriteAllText(ResultFile(s), JsonSerializer.Serialize(r)); } catch (Exception) { }
        try { File.Delete(payload); } catch (Exception) { }
        return r.Ok ? 0 : 1;
    }
}
