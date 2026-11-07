using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using VeyonSync.Shared;

namespace VeyonSync.Student;

/// <summary>
/// Configuração do agente (config.json da Etapa 1, com campos novos opcionais).
/// Campos ausentes em instalações antigas recebem padrão e a instalação continua funcionando.
/// </summary>
public class AgentConfig
{
    public string AgentId { get; set; } = Guid.NewGuid().ToString();
    public string MachineGuid { get; set; } = "";
    public string TeacherUrl { get; set; } = "https://professor-lab-a:5443";
    public string Lab { get; set; } = "A";
    public string Position { get; set; } = "";
    public bool AutoSend { get; set; } = true;
    public bool SendOnStart { get; set; } = true;
    public bool SendOnLogon { get; set; } = true;
    public bool SendOnIpChange { get; set; } = true;
    public int PresenceSeconds { get; set; } = 60;
    public string? TokenProtected { get; set; }

    // Novos (arquivo de ligação)
    public string? TeacherName { get; set; }
    public string? LabName { get; set; }
    public string? ReceiverId { get; set; }
    public string? TeacherCertSha256 { get; set; }
    public string? UpdatePublicKey { get; set; }
    public DateTimeOffset? LinkImportedAt { get; set; }

    public static readonly string Dir = AppInfo.StudentData;
    static string F => Path.Combine(Dir, "config.json");
    static readonly JsonSerializerOptions O = new() { WriteIndented = true };
    static readonly object L = new();

    public static AgentConfig Load()
    {
        lock (L)
        {
            Directory.CreateDirectory(Dir);
            AgentConfig c;
            if (File.Exists(F))
            {
                // JSON inválido gera erro explícito: nunca substituir silenciosamente por configuração vazia.
                var raw = File.ReadAllText(F);
                c = JsonSerializer.Deserialize<AgentConfig>(raw, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? throw new InvalidDataException("config.json vazio.");
                if (raw.Contains("\"Token\"", StringComparison.Ordinal)) c.SaveUnlocked(); // remove token em texto claro da Etapa 1
            }
            else c = new AgentConfig();
            var mg = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography", "MachineGuid", "")?.ToString() ?? "";
            if (c.MachineGuid != mg)
            { // novo PC ou clone: nova identidade, exige novo pareamento
                if (c.MachineGuid != "") { c.AgentId = Guid.NewGuid().ToString(); c.TokenProtected = null; }
                c.MachineGuid = mg;
                c.SaveUnlocked();
            }
            if (c.PresenceSeconds < 15) c.PresenceSeconds = 60;
            return c;
        }
    }

    public void Save() { lock (L) SaveUnlocked(); }

    void SaveUnlocked()
    {
        Directory.CreateDirectory(Dir);
        var t = F + ".tmp";
        File.WriteAllText(t, JsonSerializer.Serialize(this, O));
        File.Move(t, F, true);
    }

    // Propriedades calculadas não vão para o config.json (a Etapa 1 gravava o token em texto claro por engano).
    [JsonIgnore] public bool Paired => TokenProtected != null;
    [JsonIgnore] public string? Token => TokenProtected == null ? null : Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(TokenProtected), null, DataProtectionScope.LocalMachine));
    public void SetToken(string t) => TokenProtected = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(t), null, DataProtectionScope.LocalMachine));
    [JsonIgnore] public string MachineGuidHash => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("veyonsync:" + MachineGuid)))[..16];
}
