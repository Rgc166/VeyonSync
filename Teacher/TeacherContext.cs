using System.Text.Json;
using VeyonSync.Shared;

namespace VeyonSync.Teacher;

/// <summary>Configuração do professor: appsettings.json da Etapa 1 (Lab, Port, Tls.Thumbprint) + ajustes no banco.</summary>
public sealed class TeacherContext(Db db, IConfiguration cfg)
{
    public Db Db { get; } = db;
    public string Lab { get; } = (cfg["Lab"] ?? "A").Trim();
    public int Port { get; } = cfg.GetValue("Port", 5443);
    public string Thumbprint { get; } = cfg["Tls:Thumbprint"] ?? "";
    public string TeacherName => Environment.MachineName;

    public TeacherSettings Settings()
    {
        var json = Db.GetSetting("teacher_settings");
        TeacherSettings s;
        try { s = json == null ? new TeacherSettings() : JsonSerializer.Deserialize<TeacherSettings>(json, IpcJson.Options) ?? new TeacherSettings(); }
        catch (JsonException) { s = new TeacherSettings(); }
        if (string.IsNullOrWhiteSpace(s.LabName)) s.LabName = "Laboratório " + Lab;
        if (string.IsNullOrWhiteSpace(s.PublicHost)) s.PublicHost = Environment.MachineName;
        s.Port = Port;
        if (s.RecentSeconds < 60) s.RecentSeconds = 180;
        if (s.RetentionDays < 7) s.RetentionDays = 180;
        if (!Naming.Options.Any(o => o.Key == s.NameTemplate)) s.NameTemplate = Naming.WindowsName;
        if (s.AddressMode is not ("ip" or "hostname")) s.AddressMode = "ip";
        return s;
    }

    public void SaveSettings(TeacherSettings s)
    {
        var old = Settings();
        s.Port = Port;
        s.LabName = Naming.Sanitize(s.LabName ?? "");
        if (string.IsNullOrWhiteSpace(s.LabName)) s.LabName = "Laboratório " + Lab;
        if (!Naming.Options.Any(o => o.Key == s.NameTemplate)) throw new InvalidDataException("Padrão de nome inválido.");
        if (s.AddressMode is not ("ip" or "hostname")) throw new InvalidDataException("Modo de endereço inválido.");
        if (!string.IsNullOrWhiteSpace(s.VeyonCliPath) && !s.VeyonCliPath.EndsWith("veyon-cli.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("O caminho deve apontar para veyon-cli.exe.");
        s.RecentSeconds = Math.Clamp(s.RecentSeconds, 60, 3600);
        s.RetentionDays = Math.Clamp(s.RetentionDays, 7, 3650);
        s.PublicHost = (s.PublicHost ?? "").Trim();
        if (s.PublicHost.Length == 0 || Uri.CheckHostName(s.PublicHost) == UriHostNameType.Unknown)
            throw new InvalidDataException("Endereço público do professor inválido.");
        Db.SetSetting("teacher_settings", JsonSerializer.Serialize(s, IpcJson.Options));
        Db.Hist("", Lab, TeacherName, "Ação administrativa", "Configurações do THC alteradas");
        // Mudou o que vai para o Veyon: todos os registros do laboratório precisam ser reconferidos.
        if (old.NameTemplate != s.NameTemplate || old.LabName != s.LabName || old.AddressMode != s.AddressMode)
            Db.QueueAll(Lab);
    }

    public string Url(TeacherSettings? s = null) => $"https://{(s ?? Settings()).PublicHost}:{Port}";
}
