using System.Diagnostics;
using System.Text;

namespace VeyonSync.Teacher.Veyon;

public sealed record CliResult(int ExitCode, string StdOut, string StdErr, bool TimedOut)
{
    public bool Ok => !TimedOut && ExitCode == 0;
    public string Summary => TimedOut ? "tempo esgotado" : $"código {ExitCode}: {Trim(StdErr.Length > 0 ? StdErr : StdOut)}";
    static string Trim(string s) { s = s.Trim(); return s.Length > 400 ? s[..400] + "…" : s; }
}

public sealed class VeyonObject
{
    public bool IsComputer { get; set; }
    public string Location { get; set; } = "";
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public string Mac { get; set; } = "";
}

/// <summary>
/// Adaptador do veyon-cli (docs.veyon.io/en/latest/admin/cli.html). Usa somente comandos documentados:
/// help, networkobjects export/add/remove, config export/import. Argumentos via ArgumentList (sem shell).
/// Nunca usa "networkobjects clear".
/// </summary>
public sealed class VeyonCli(string exePath)
{
    public string ExePath { get; } = exePath;
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);
    const string ExportFormat = "%type%;%location%;%name%;%host%;%mac%";

    public static string? Locate(string? configured)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configured)) candidates.Add(configured);
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Veyon", "veyon-cli.exe"));
        var x86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrEmpty(x86)) candidates.Add(Path.Combine(x86, "Veyon", "veyon-cli.exe"));
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>Versão a partir dos metadados do executável instalado (não há comando de versão documentado).</summary>
    public string Version()
    {
        try
        {
            var v = FileVersionInfo.GetVersionInfo(ExePath);
            return v.ProductVersion ?? v.FileVersion ?? "desconhecida";
        }
        catch (Exception) { return "desconhecida"; }
    }

    public async Task<CliResult> RunAsync(IEnumerable<string> args, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(ExePath)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(ExePath)!
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = new Process { StartInfo = psi };
        p.Start();
        var outTask = p.StandardOutput.ReadToEndAsync(ct);
        var errTask = p.StandardError.ReadToEndAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? DefaultTimeout);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (Exception) { }
            return new CliResult(-1, "", "", true);
        }
        return new CliResult(p.ExitCode, await outTask, await errTask, false);
    }

    /// <summary>Confere, na ajuda da versão instalada, se os módulos necessários existem.</summary>
    public async Task<(bool ok, string detail)> CheckAsync(CancellationToken ct = default)
    {
        var h = await RunAsync(new[] { "help" }, TimeSpan.FromSeconds(20), ct);
        if (h.TimedOut) return (false, "veyon-cli não respondeu à ajuda (tempo esgotado).");
        var text = (h.StdOut + "\n" + h.StdErr).ToLowerInvariant();
        var missing = new[] { "networkobjects", "config" }.Where(m => !text.Contains(m)).ToList();
        if (missing.Count > 0) return (false, "Módulos ausentes na ajuda do veyon-cli: " + string.Join(", ", missing));
        // Algumas versões não têm "networkobjects help" (respondem "comando não existe"). Por isso a conferência
        // dos subcomandos é feita com uma leitura real e inofensiva ("networkobjects list").
        var n = await RunAsync(new[] { "networkobjects", "list" }, TimeSpan.FromSeconds(30), ct);
        if (n.TimedOut) return (false, "veyon-cli não respondeu a 'networkobjects list' (tempo esgotado).");
        if (n.ExitCode != 0) return (false, "veyon-cli não conseguiu ler o diretório (" + n.Summary + ").");
        return (true, "veyon-cli " + Version());
    }

    public async Task<List<VeyonObject>> ExportAsync(string tempDir, CancellationToken ct = default)
    {
        Directory.CreateDirectory(tempDir);
        var file = Path.Combine(tempDir, $"export-{Guid.NewGuid():N}.txt");
        try
        {
            var r = await RunAsync(new[] { "networkobjects", "export", file, "format", ExportFormat }, null, ct);
            if (!r.Ok) throw new InvalidOperationException("Falha ao ler o diretório do Veyon (" + r.Summary + ")");
            if (!File.Exists(file)) return new List<VeyonObject>(); // diretório vazio
            return Parse(ReadText(file));
        }
        finally { try { File.Delete(file); } catch (Exception) { } }
    }

    public static string ReadText(string file)
    {
        var bytes = File.ReadAllBytes(file);
        if (bytes.Length >= 2 && (bytes[0] == 0xFF && bytes[1] == 0xFE)) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(bytes); }
    }

    public static List<VeyonObject> Parse(string text)
    {
        var list = new List<VeyonObject>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Trim().Length == 0) continue;
            var f = line.Split(';');
            if (f.Length < 5) continue;
            // Campos extras (nome com ';') são juntados ao nome para não perder o registro.
            string type = f[0].Trim(), loc = f[1].Trim();
            string mac = f[^1].Trim(), host = f[^2].Trim();
            string name = string.Join(";", f[2..^2]).Trim();
            var t = type.ToLowerInvariant();
            bool computer = t.Contains("comp") || (!t.Contains("loc") && host.Length > 0);
            list.Add(new VeyonObject { IsComputer = computer, Location = computer ? loc : (loc.Length > 0 ? loc : name), Name = name, Host = host, Mac = mac });
        }
        return list;
    }

    /// <summary>
    /// Confere pelo "networkobjects list" se uma sala existe (a exportação pode omitir salas vazias).
    /// Evita criar sala duplicada.
    /// </summary>
    public async Task<bool> LocationListedAsync(string name, CancellationToken ct = default)
    {
        var r = await RunAsync(new[] { "networkobjects", "list" }, TimeSpan.FromSeconds(30), ct);
        if (!r.Ok) return false;
        foreach (var raw in r.StdOut.Split('\n'))
        {
            var line = raw.Trim().Trim('"', '\'', '*', '-', '+', '|', ' ', '\t', '\r');
            if (line.Equals(name, StringComparison.OrdinalIgnoreCase) || line.Contains("\"" + name + "\"", StringComparison.OrdinalIgnoreCase)
                || line.EndsWith(" " + name, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public Task<CliResult> AddLocationAsync(string name, CancellationToken ct = default) =>
        RunAsync(new[] { "networkobjects", "add", "location", name }, null, ct);

    public Task<CliResult> AddComputerAsync(string name, string host, string mac, string parent, CancellationToken ct = default) =>
        RunAsync(new[] { "networkobjects", "add", "computer", name, host, mac, parent }, null, ct);

    public Task<CliResult> RemoveAsync(string objectName, CancellationToken ct = default) =>
        RunAsync(new[] { "networkobjects", "remove", objectName }, null, ct);

    public Task<CliResult> ConfigExportAsync(string file, CancellationToken ct = default) =>
        RunAsync(new[] { "config", "export", file }, null, ct);

    public Task<CliResult> ConfigImportAsync(string file, CancellationToken ct = default) =>
        RunAsync(new[] { "config", "import", file }, null, ct);

    public Task<CliResult> ExportCsvAsync(string file, CancellationToken ct = default) =>
        RunAsync(new[] { "networkobjects", "export", file, "format", ExportFormat }, null, ct);

    public static string NormMac(string? mac)
    {
        if (string.IsNullOrEmpty(mac)) return "";
        var sb = new StringBuilder();
        foreach (var ch in mac) if (Uri.IsHexDigit(ch)) sb.Append(char.ToUpperInvariant(ch));
        return sb.Length == 12 ? sb.ToString() : "";
    }

    /// <summary>Formato AA:BB:CC:DD:EE:FF usado pelo Veyon.</summary>
    public static string VeyonMac(string? mac)
    {
        var n = NormMac(mac);
        return n.Length == 12 ? string.Join(":", Enumerable.Range(0, 6).Select(i => n.Substring(i * 2, 2))) : "";
    }
}
