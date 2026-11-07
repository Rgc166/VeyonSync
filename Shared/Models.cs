namespace VeyonSync.Shared;

// Campos novos são opcionais no fim dos records: agentes e professores da Etapa 1 continuam compatíveis.
public record PairRequest(string Code, string AgentId, string Lab, string MachineName,
    string? Position = null, string? AgentVersion = null, string? ReceiverId = null);

public record PairResponse(string Token, string Lab, string? TeacherName = null, string? ReceiverId = null,
    string? UpdatePublicKey = null);

public record ReportDto(string AgentId, string MachineName, string Lab, string Position, string? User, string? Ip,
    string? Mac, DateTimeOffset CollectedAt, long Seq, string AgentVersion, string Reason,
    bool Heartbeat = false, bool AutoMode = true, string? SessionNote = null, string? MachineGuidHash = null);

// Recebimento e integração Veyon são resultados separados.
public record ReportAck(bool Received, bool Stale, bool VeyonApplied, string VeyonAvailability, bool CollectRequested,
    string? VeyonState = null, string? Warning = null, bool UpdateAvailable = false);

public record ApiError(string Error, string? Code = null);

public static class ReportReasons
{
    public const string Start = "inicialização";
    public const string Logon = "login";
    public const string IpChange = "mudança de IP";
    public const string Manual = "manual";
    public const string Presence = "presença";
    public const string Pairing = "pareamento";
    public const string Collect = "coleta solicitada";
}

public static class VeyonStates
{
    public const string Pending = "Integração Veyon pendente";
    public const string Applied = "Aplicado no Veyon";
    public const string Error = "Erro na integração Veyon";
    public const string Conflict = "Conflito no Veyon";
    public const string NotManaged = "Ainda não integrado";
    public const string AvailabilityUnknown = "Disponibilidade não verificada";
}

// ---------- Atualizações pela rede ----------
public record UpdateManifest(string Product, string Version, string MinVersion, string Sha256, long Size,
    string FileName, DateTimeOffset CreatedAt, string ReceiverId);

// A assinatura cobre exatamente os bytes UTF-8 de ManifestJson (sem reserialização).
public record SignedManifest(string ManifestJson, string Signature, long UpdateId);

public record UpdateResult(long UpdateId, bool Ok, string Version, string Detail);

// 2.2: o STD avisa que o pacote foi conferido e a instalação começou (professores antigos ignoram).
public record UpdateStage(long UpdateId, string Stage);

// ---------- Coordenação entre professores ----------
public record PeerPairRequest(string Code, string CallerName, string CallerLab);
public record PeerPairResponse(string Token, string Lab, string TeacherName);
public record PeerInfo(string Lab, string LabName, string TeacherName, string Version, int Machines, int Pending);
public record PeerApplyRequest(bool RetryFailedOnly);
