namespace VeyonSync.Shared;

// DTOs trocados entre serviços e interfaces (IPC) e entre professores (HTTPS).

public sealed class MachineRow
{
    public string AgentId { get; set; } = "";
    public string Lab { get; set; } = "";
    public string Position { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string WindowsName { get; set; } = "";
    public string? User { get; set; }
    public string? Ip { get; set; }
    public string? Mac { get; set; }
    public DateTimeOffset? LastSeen { get; set; }
    public DateTimeOffset? FirstSeen { get; set; }
    public bool Recent { get; set; }
    public bool AutoMode { get; set; } = true;
    public string AgentState { get; set; } = "";
    public string VeyonState { get; set; } = "";
    public string Version { get; set; } = "";
    public bool CollectPending { get; set; }
    public bool Duplicate { get; set; }
    public int OpenErrors { get; set; }
    public string? SessionNote { get; set; }
    public string? FriendlyOverride { get; set; }
    /// <summary>Situação na última coleta solicitada ("" = não participou).</summary>
    public string CollectStatus { get; set; } = "";
}

/// <summary>Acompanhamento de uma coleta solicitada pelo professor.</summary>
public sealed class CollectRound
{
    public long Id { get; set; }
    public DateTimeOffset Ts { get; set; }
    public int DeadlineSeconds { get; set; }
    public int Total { get; set; }
    public int Answered { get; set; }
    public int Waiting { get; set; }
    public int Manual { get; set; }
    public int NoResponse { get; set; }
    public bool Finished => Waiting == 0 && Manual == 0;
    public bool AllAnswered => Total > 0 && Answered == Total;
}

public static class CollectStates
{
    public const string Waiting = "Aguardando resposta";
    public const string Answered = "Respondeu à coleta";
    public const string Manual = "Modo manual: aguardando envio";
    public const string NoResponse = "Sem resposta no prazo";
}

public sealed class TeacherOverview
{
    public string Lab { get; set; } = "";
    public string LabName { get; set; } = "";
    public string TeacherName { get; set; } = "";
    public string Url { get; set; } = "";
    public string Version { get; set; } = AppInfo.Version;
    public int Recent { get; set; }
    public int NotRecent { get; set; }
    public int VeyonPending { get; set; }
    public int ErrorsPending { get; set; }
    public int RecentSeconds { get; set; }
    public string NameTemplate { get; set; } = Naming.WindowsName;
    public bool VeyonCliFound { get; set; }
    public string? VeyonCliPath { get; set; }
    public List<MachineRow> Machines { get; set; } = new();
    public CollectRound? LastCollect { get; set; }
    public List<HistoryRow> RecentEvents { get; set; } = new();
    public LabMap? Map { get; set; }
}

public sealed class HistoryRow
{
    public long Id { get; set; }
    public DateTimeOffset Ts { get; set; }
    public string Lab { get; set; } = "";
    public string AgentId { get; set; } = "";
    public string Machine { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Result { get; set; } = "";
    public string Detail { get; set; } = "";
}

public sealed class HistoryQuery
{
    public DateTimeOffset? From { get; set; }
    public DateTimeOffset? To { get; set; }
    public string? Lab { get; set; }
    public string? Machine { get; set; }
    public string? Kind { get; set; }
    public string? Result { get; set; }
    public int Limit { get; set; } = 2000;
}

public sealed class ErrorRow
{
    public long Id { get; set; }
    public string AgentId { get; set; } = "";
    public DateTimeOffset Ts { get; set; }
    public DateTimeOffset LastTs { get; set; }
    public string Lab { get; set; } = "";
    public string Machine { get; set; } = "";
    public string Source { get; set; } = "";
    public string Message { get; set; } = "";
    public int Count { get; set; }
    public bool Resolved { get; set; }
}

public sealed class LabMap
{
    public string Lab { get; set; } = "";
    public List<MapRow> Rows { get; set; } = new();
}

public sealed class MapRow
{
    public string Name { get; set; } = "";
    /// <summary>AgentId de cada posição; null = posição vazia (corredor ou sem máquina).</summary>
    public List<string?> Slots { get; set; } = new();
}

public sealed class VeyonChange
{
    public string AgentId { get; set; } = "";
    public string Machine { get; set; } = "";
    public string Lab { get; set; } = "";
    public string Action { get; set; } = ""; // Adicionar, Atualizar, Já atualizado, Conflito
    public string? OldName { get; set; }
    public string? OldHost { get; set; }
    public string? OldMac { get; set; }
    public string? OldLocation { get; set; }
    public string NewName { get; set; } = "";
    public string NewHost { get; set; } = "";
    public string NewMac { get; set; } = "";
    public string NewLocation { get; set; } = "";
    public string Detail { get; set; } = "";
}

public sealed class VeyonPreview
{
    public string Lab { get; set; } = "";
    public string Destination { get; set; } = "";
    public bool CliFound { get; set; }
    public string? CliPath { get; set; }
    public string? CliVersion { get; set; }
    public string? Error { get; set; }
    public List<VeyonChange> Changes { get; set; } = new();
}

public sealed class VeyonItemResult
{
    public string AgentId { get; set; } = "";
    public string Machine { get; set; } = "";
    public string Outcome { get; set; } = ""; // Integrado, Já atualizado, Conflito, Erro
    public string Detail { get; set; } = "";
}

public sealed class BatchSummary
{
    public string Lab { get; set; } = "";
    public string Destination { get; set; } = "";
    public bool Unavailable { get; set; }
    public int Integrated { get; set; }
    public int AlreadyUpdated { get; set; }
    public int Conflicts { get; set; }
    public int Errors { get; set; }
    public string Detail { get; set; } = "";
    public string? BackupFile { get; set; }
    public List<VeyonItemResult> Items { get; set; } = new();
}

public sealed class PeerRow
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Lab { get; set; } = "";
    public string Url { get; set; } = "";
    public bool Enabled { get; set; }
    public string LastStatus { get; set; } = "";
    public DateTimeOffset? LastContact { get; set; }
}

public sealed class BackupRow
{
    public long Id { get; set; }
    public DateTimeOffset Ts { get; set; }
    public string File { get; set; } = "";
    public string Note { get; set; } = "";
}

public sealed class UpdateRow
{
    public long Id { get; set; }
    public string Product { get; set; } = "";
    public string Version { get; set; } = "";
    public DateTimeOffset Created { get; set; }
    public int Pending { get; set; }
    public int Downloading { get; set; }
    public int Done { get; set; }
    public int Failed { get; set; }
    public List<UpdateTargetRow> Targets { get; set; } = new();
}

public sealed class UpdateTargetRow
{
    public string AgentId { get; set; } = "";
    public string Machine { get; set; } = "";
    public string Status { get; set; } = "";
    public string Detail { get; set; } = "";
    public DateTimeOffset? Updated { get; set; }
    public string StatusLabel => UpdateStates.Label(Status);
}

/// <summary>Estados de uma distribuição THC → STD, separados como a tela exige.</summary>
public static class UpdateStates
{
    public static string Label(string s) => s switch
    {
        "pendente" => "Pendente (aguardando o computador)",
        "falhou-repetir" => "Pendente (nova tentativa)",
        "baixando" => "Transferindo",
        "instalando" or "aplicando" => "Instalando",
        "concluída" => "Confirmada",
        "falhou" => "Falhou",
        "substituída" => "Substituída por distribuição mais nova",
        _ => s
    };
}

public sealed class TeacherSettings
{
    public string LabName { get; set; } = "";
    public string NameTemplate { get; set; } = Naming.WindowsName;
    public string? VeyonCliPath { get; set; }
    public int RecentSeconds { get; set; } = 180;
    public int RetentionDays { get; set; } = 180;
    public string AddressMode { get; set; } = "ip"; // ip | hostname
    public string PublicHost { get; set; } = "";
    public int Port { get; set; } = 5443;
}

public sealed class LinkPreview
{
    public string Lab { get; set; } = "";
    public string LabName { get; set; } = "";
    public string TeacherName { get; set; } = "";
    public string Url { get; set; } = "";
    public List<string> CertNames { get; set; } = new();
    public string CertSha256 { get; set; } = "";
    public DateTimeOffset CertNotAfter { get; set; }
    public List<string> Warnings { get; set; } = new();
}

// ----------- STD -----------
public sealed class StudentStatus
{
    public bool Paired { get; set; }
    public bool LinkImported { get; set; }
    public bool CertificatePinned { get; set; }
    public string Lab { get; set; } = "";
    public string LabName { get; set; } = "";
    public string TeacherName { get; set; } = "";
    public string TeacherUrl { get; set; } = "";
    public string Position { get; set; } = "";
    public string WindowsName { get; set; } = "";
    public string? User { get; set; }
    public string? UserNote { get; set; }
    public string? Ip { get; set; }
    public string? Mac { get; set; }
    public DateTimeOffset? LastCollect { get; set; }
    public DateTimeOffset? LastConfirmed { get; set; }
    public string SendStatus { get; set; } = "Aguardando";
    public string? LastError { get; set; }
    public string VeyonState { get; set; } = VeyonStates.NotManaged;
    public string VeyonAvailability { get; set; } = VeyonStates.AvailabilityUnknown;
    public bool AutoSend { get; set; } = true;
    public bool SendOnStart { get; set; } = true;
    public bool SendOnLogon { get; set; } = true;
    public bool SendOnIpChange { get; set; } = true;
    public int PresenceSeconds { get; set; } = 60;
    public string AgentIdShort { get; set; } = "";
    public string Version { get; set; } = AppInfo.Version;
    public bool PendingReport { get; set; }
    public string? UpdateStatus { get; set; }
    public List<StudentErrorRow> Errors { get; set; } = new();
    /// <summary>Serviço local do Veyon (medido neste PC): Em execução, Parado, Não encontrado.</summary>
    public string VeyonService { get; set; } = "Não verificado";
    public List<SendAttempt> Attempts { get; set; } = new();
}

public sealed class SendAttempt
{
    public DateTimeOffset Ts { get; set; }
    public bool Ok { get; set; }
    public string Reason { get; set; } = "";
    public string Message { get; set; } = "";
}

public sealed class StudentErrorRow
{
    public DateTimeOffset Ts { get; set; }
    public string Message { get; set; } = "";
    public bool Resolved { get; set; }
}

public sealed class StudentSettings
{
    public bool AutoSend { get; set; } = true;
    public bool SendOnStart { get; set; } = true;
    public bool SendOnLogon { get; set; } = true;
    public bool SendOnIpChange { get; set; } = true;
    public int PresenceSeconds { get; set; } = 60;
}

public sealed class PairOutcome
{
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
    public string SendStatus { get; set; } = "";
    public string? LastError { get; set; }
}
