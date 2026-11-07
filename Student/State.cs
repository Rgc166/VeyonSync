using System.Text.Json;
using VeyonSync.Shared;

namespace VeyonSync.Student;

/// <summary>state.json (Etapa 1 + campos novos). Gravação atômica.</summary>
public class State
{
    public ReportDto? Pending { get; set; }
    public string Status { get; set; } = "Aguardando";
    public DateTimeOffset? LastConfirmed { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? LastCollect { get; set; }
    public string? LastIp { get; set; }
    public string? LastUser { get; set; }
    public string? LastMac { get; set; }
    public string? SessionNote { get; set; }
    public long Seq { get; set; }
    public string VeyonState { get; set; } = VeyonStates.NotManaged;
    public string VeyonAvailability { get; set; } = VeyonStates.AvailabilityUnknown;
    public string? Warning { get; set; }
    public string AgentVersion { get; set; } = AppInfo.Version;
    public string? UpdateStatus { get; set; }
    public DateTimeOffset? ServiceStarted { get; set; }
    public List<SendAttempt> Attempts { get; set; } = new();

    static string F => Path.Combine(AgentConfig.Dir, "state.json");
    static readonly object L = new();

    public static State Load()
    {
        lock (L)
        {
            try { return JsonSerializer.Deserialize<State>(File.ReadAllText(F)) ?? new(); }
            catch { return new(); }
        }
    }

    public void Save()
    {
        lock (L)
        {
            Directory.CreateDirectory(AgentConfig.Dir);
            var t = F + ".tmp";
            File.WriteAllText(t, JsonSerializer.Serialize(this));
            File.Move(t, F, true);
        }
    }
}

/// <summary>Erros locais do STD (máximo 300, sem tokens ou códigos).</summary>
public static class ErrorLog
{
    static string F => Path.Combine(AgentConfig.Dir, "errors.json");
    static readonly object L = new();
    const int Max = 300;

    public static List<StudentErrorRow> Load()
    {
        lock (L)
        {
            try { return JsonSerializer.Deserialize<List<StudentErrorRow>>(File.ReadAllText(F)) ?? new(); }
            catch { return new(); }
        }
    }

    public static void Add(string message)
    {
        lock (L)
        {
            var list = Load();
            var open = list.FirstOrDefault(e => !e.Resolved && e.Message == message);
            if (open != null) open.Ts = DateTimeOffset.Now; // não repete o mesmo erro em sequência
            else list.Add(new StudentErrorRow { Ts = DateTimeOffset.Now, Message = message });
            if (list.Count > Max) list.RemoveRange(0, list.Count - Max);
            Save(list);
        }
    }

    public static void ResolveAll()
    {
        lock (L)
        {
            var list = Load();
            if (!list.Any(e => !e.Resolved)) return;
            foreach (var e in list) e.Resolved = true;
            Save(list);
        }
    }

    static void Save(List<StudentErrorRow> list)
    {
        Directory.CreateDirectory(AgentConfig.Dir);
        var t = F + ".tmp";
        File.WriteAllText(t, JsonSerializer.Serialize(list));
        File.Move(t, F, true);
    }
}
