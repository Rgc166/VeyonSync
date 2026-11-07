using System.IO;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using VeyonSync.Desktop;
using VeyonSync.Shared;

namespace VeyonSync.TeacherApp;

public sealed class SelectableMachine : ObservableObject
{
    bool selected;
    public bool Selected { get => selected; set => Set(ref selected, value); }
    public MachineRow Row { get; init; } = new();
    public bool Outdated { get; init; }
    public string VersionText => string.IsNullOrEmpty(Row.Version) ? "—" : Row.Version;
}

public sealed class SelectablePeer : ObservableObject
{
    bool selected = true;
    public bool Selected { get => selected; set => Set(ref selected, value); }
    public PeerRow Row { get; init; } = new();
}

/// <summary>Resumo do mapa na visão geral: uma coluna por fileira, um quadrado por posição.</summary>
public sealed class MiniColumn
{
    public string Name { get; init; } = "";
    public List<MiniCell> Cells { get; init; } = new();
}

public sealed class MiniCell
{
    public string Kind { get; init; } = "muted"; // ok = comunicação recente; muted = sem comunicação; empty = posição vazia
    public string Tip { get; init; } = "";
    public bool Empty => Kind == "empty";
}

/// <summary>Cópia editável das configurações, com validação e indicação de alterações não salvas.</summary>
public sealed class TeacherSettingsForm : ObservableObject
{
    TeacherSettings loaded = new();
    string labName = "", nameTemplate = Naming.WindowsName, cliPath = "", addressMode = "ip", publicHost = "", recent = "180", retention = "180";

    public string LabName { get => labName; set { Set(ref labName, value); Changed(); } }
    public string NameTemplate { get => nameTemplate; set { Set(ref nameTemplate, value ?? Naming.WindowsName); Changed(); } }
    public string VeyonCliPath { get => cliPath; set { Set(ref cliPath, value ?? ""); Changed(); } }
    public string AddressMode { get => addressMode; set { Set(ref addressMode, value ?? "ip"); Changed(); } }
    public string PublicHost { get => publicHost; set { Set(ref publicHost, value); Changed(); } }
    public string RecentSeconds { get => recent; set { Set(ref recent, value); Changed(); } }
    public string RetentionDays { get => retention; set { Set(ref retention, value); Changed(); } }
    public int Port => loaded.Port;

    public string NameExample => Naming.Friendly(NameTemplate, "B", "PC01", "DESKTOP-ALUNO01");

    public bool Dirty =>
        LabName.Trim() != (loaded.LabName ?? "") || NameTemplate != loaded.NameTemplate || VeyonCliPath.Trim() != (loaded.VeyonCliPath ?? "")
        || AddressMode != loaded.AddressMode || PublicHost.Trim() != (loaded.PublicHost ?? "")
        || RecentSeconds.Trim() != loaded.RecentSeconds.ToString(CultureInfo.InvariantCulture)
        || RetentionDays.Trim() != loaded.RetentionDays.ToString(CultureInfo.InvariantCulture);

    public string Errors
    {
        get
        {
            var e = new List<string>();
            if (string.IsNullOrWhiteSpace(LabName)) e.Add("Informe o nome do laboratório (sala no Veyon).");
            if (string.IsNullOrWhiteSpace(PublicHost)) e.Add("Informe o endereço do professor.");
            if (!int.TryParse(RecentSeconds, out var r) || r < 60 || r > 3600) e.Add("Comunicação recente: use de 60 a 3600 segundos.");
            if (!int.TryParse(RetentionDays, out var d) || d < 7 || d > 3650) e.Add("Histórico: use de 7 a 3650 dias.");
            return string.Join("\n", e);
        }
    }

    void Changed() { Raise(nameof(Dirty)); Raise(nameof(Errors)); Raise(nameof(NameExample)); }

    public void Load(TeacherSettings s)
    {
        loaded = s;
        labName = s.LabName ?? ""; nameTemplate = s.NameTemplate; cliPath = s.VeyonCliPath ?? ""; addressMode = s.AddressMode;
        publicHost = s.PublicHost ?? ""; recent = s.RecentSeconds.ToString(CultureInfo.InvariantCulture); retention = s.RetentionDays.ToString(CultureInfo.InvariantCulture);
        foreach (var n in new[] { nameof(LabName), nameof(NameTemplate), nameof(VeyonCliPath), nameof(AddressMode), nameof(PublicHost), nameof(RecentSeconds), nameof(RetentionDays), nameof(Port) })
            Raise(n);
        Changed();
    }

    public TeacherSettings ToSettings() => new()
    {
        LabName = LabName.Trim(), NameTemplate = NameTemplate, VeyonCliPath = string.IsNullOrWhiteSpace(VeyonCliPath) ? null : VeyonCliPath.Trim(),
        AddressMode = AddressMode, PublicHost = PublicHost.Trim(), Port = loaded.Port,
        RecentSeconds = int.Parse(RecentSeconds, CultureInfo.InvariantCulture), RetentionDays = int.Parse(RetentionDays, CultureInfo.InvariantCulture)
    };
}

public sealed class TeacherVm : ObservableObject
{
    public readonly Ipc.Client Api = new(AppInfo.TeacherPipe);
    public event Action? MachinesRebuilt;
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(5) };

    public List<NavEntry> NavEntries { get; } = new()
    {
        new("\uE80F", "Visão geral"), new("\uE7F8", "Computadores"), new("\uE81E", "Mapa da sala"), new("\uE8D7", "Credenciamento"),
        new("\uE81C", "Histórico"), new("\uE7BA", "Erros"), new("\uE895", "Integração"), new("\uE896", "Atualizações"), new("\uE713", "Configurações")
    };

    int page;
    public int Page { get => page; set { if (Set(ref page, value)) _ = OnPageAsync(); } }

    bool busy;
    public bool Busy { get => busy; set => Set(ref busy, value); }

    string serviceError = "";
    public string ServiceError { get => serviceError; set { Set(ref serviceError, value); Raise(nameof(ServiceOk)); } }
    public bool ServiceOk => serviceError == "";

    public bool IsAdmin { get; } = AdminInfo.IsElevated;
    public string VersionText => "v" + AppInfo.Version;

    TeacherOverview? overview;
    public TeacherOverview? Overview
    {
        get => overview;
        set
        {
            Set(ref overview, value);
            ApplyFilter();
            BuildMini();
            foreach (var n in new[] { nameof(LabText), nameof(TotalCount), nameof(ErrorsKind), nameof(ErrorsText), nameof(PendingKind), nameof(Collect), nameof(HasCollect),
                         nameof(CollectTitle), nameof(CollectKind), nameof(CollectPercent), nameof(CollectDetail), nameof(RecentEvents), nameof(HasRecentEvents), nameof(CliText), nameof(CliKind) })
                Raise(n);
        }
    }

    // ---------------- Visão geral ----------------
    public string LabText => Overview == null ? "" : $"{(string.IsNullOrWhiteSpace(Overview.LabName) ? "Laboratório " + Overview.Lab : Overview.LabName)}";
    public int TotalCount => Overview?.Machines.Count ?? 0;
    public string ErrorsKind => (Overview?.ErrorsPending ?? 0) > 0 ? "err" : "ok";
    public string ErrorsText => (Overview?.ErrorsPending ?? 0) switch { 0 => "Nenhum erro ativo", 1 => "1 erro ativo", var n => $"{n} erros ativos" };
    public string PendingKind => (Overview?.VeyonPending ?? 0) > 0 ? "warn" : "ok";
    public CollectRound? Collect => Overview?.LastCollect;
    public bool HasCollect => Collect != null && Collect.Total > 0 && DateTimeOffset.Now - Collect.Ts < TimeSpan.FromHours(12);
    public string CollectTitle => Collect == null ? "" :
        Collect.AllAnswered ? "Coleta concluída: todos responderam"
        : Collect.Waiting > 0 ? "Coleta em andamento"
        : "Coleta encerrada com pendências";
    public string CollectKind => Collect == null ? "info" : Collect.AllAnswered ? "ok" : Collect.Waiting > 0 ? "info" : "warn";
    public double CollectPercent => Collect == null || Collect.Total == 0 ? 0 : 100.0 * Collect.Answered / Collect.Total;
    public string CollectDetail => Collect == null ? "" :
        $"Solicitada às {Collect.Ts.ToLocalTime():HH:mm} para {Collect.Total} computador(es). Prazo: {Collect.DeadlineSeconds / 60} min.";
    public List<HistoryRow> RecentEvents => Overview?.RecentEvents ?? new();
    public bool HasRecentEvents => RecentEvents.Count > 0;
    public ObservableCollection<MiniColumn> MiniMap { get; } = new();
    bool hasMap;
    public bool HasMap { get => hasMap; set => Set(ref hasMap, value); }
    public string CliText => Overview == null ? "" : Overview.VeyonCliFound ? "Veyon CLI encontrado" : "Veyon CLI não encontrado";
    public string CliKind => Overview?.VeyonCliFound == true ? "ok" : "warn";

    void BuildMini()
    {
        MiniMap.Clear();
        var map = Overview?.Map;
        var ms = (Overview?.Machines ?? new()).ToDictionary(m => m.AgentId);
        if (map != null)
            for (int i = 0; i < map.Rows.Count; i++)
            {
                var row = map.Rows[i];
                MiniMap.Add(new MiniColumn
                {
                    Name = string.IsNullOrWhiteSpace(row.Name) ? $"Fileira {i + 1}" : row.Name,
                    Cells = row.Slots.Select(id => id != null && ms.TryGetValue(id, out var m)
                        ? new MiniCell { Kind = m.Recent ? "ok" : "muted", Tip = $"{(string.IsNullOrEmpty(m.Position) ? m.DisplayName : m.Position)} · {m.AgentState}" }
                        : new MiniCell { Kind = "empty", Tip = "Posição vazia" }).ToList()
                });
            }
        HasMap = MiniMap.Count > 0;
    }

    // ---------------- Computadores ----------------
    public ObservableCollection<MachineRow> Machines { get; } = new();
    string search = "", agentFilter = "Todos", veyonFilter = "Todos";
    public string Search { get => search; set { Set(ref search, value); ApplyFilter(); } }
    public string AgentFilter { get => agentFilter; set { Set(ref agentFilter, value ?? "Todos"); ApplyFilter(); } }
    public string VeyonFilter { get => veyonFilter; set { Set(ref veyonFilter, value ?? "Todos"); ApplyFilter(); } }
    public string[] AgentFilters { get; } = { "Todos", "Comunicação recente", "Sem comunicação recente", "Modo manual", "Com erro", "Coleta: sem resposta", "Coleta solicitada", "Identidade duplicada" };
    public string[] VeyonFilters { get; } = { "Todos", VeyonStates.Applied, VeyonStates.Pending, VeyonStates.Conflict, VeyonStates.Error, VeyonStates.NotManaged };
    public string MachinesCountText => Machines.Count == TotalCount ? $"{TotalCount} computador(es)" : $"{Machines.Count} de {TotalCount} computador(es)";

    MachineRow? selectedMachine;
    public MachineRow? SelectedMachine
    {
        get => selectedMachine;
        set
        {
            var changed = selectedMachine?.AgentId != value?.AgentId;
            Set(ref selectedMachine, value);
            Raise(nameof(HasSelectedMachine));
            Raise(nameof(SelectedTitle));
            if (changed) _ = LoadMachineErrorsAsync();
        }
    }
    public bool HasSelectedMachine => SelectedMachine != null;
    public string SelectedTitle => SelectedMachine == null ? "" : string.IsNullOrEmpty(SelectedMachine.Position) ? SelectedMachine.DisplayName : SelectedMachine.Position;
    public ObservableCollection<ErrorRow> MachineErrors { get; } = new();

    void ApplyFilter()
    {
        var list = Overview?.Machines ?? new List<MachineRow>();
        IEnumerable<MachineRow> q = list;
        if (!string.IsNullOrWhiteSpace(Search))
        {
            var s = Search.Trim();
            q = q.Where(m => new[] { m.DisplayName, m.WindowsName, m.Position, m.User, m.Ip, m.Mac, m.Lab }.Any(f => f != null && f.Contains(s, StringComparison.OrdinalIgnoreCase)));
        }
        q = AgentFilter switch
        {
            "Comunicação recente" => q.Where(m => m.Recent),
            "Sem comunicação recente" => q.Where(m => !m.Recent && m.AutoMode),
            "Modo manual" => q.Where(m => !m.AutoMode),
            "Com erro" => q.Where(m => m.OpenErrors > 0),
            "Coleta: sem resposta" => q.Where(m => m.CollectStatus is CollectStates.NoResponse or CollectStates.Manual),
            "Coleta solicitada" => q.Where(m => m.CollectPending || m.CollectStatus == CollectStates.Waiting),
            "Identidade duplicada" => q.Where(m => m.Duplicate),
            _ => q
        };
        if (VeyonFilter != "Todos") q = q.Where(m => m.VeyonState == VeyonFilter);
        Machines.Clear();
        foreach (var m in q) Machines.Add(m);
        if (SelectedMachine != null)
        {
            var again = list.FirstOrDefault(m => m.AgentId == SelectedMachine.AgentId);
            selectedMachine = again;
            Raise(nameof(SelectedMachine)); Raise(nameof(HasSelectedMachine)); Raise(nameof(SelectedTitle));
        }
        Raise(nameof(MachinesCountText));
        MachinesRebuilt?.Invoke(); // a tela restaura a seleção após a atualização automática
    }

    async Task LoadMachineErrorsAsync()
    {
        MachineErrors.Clear();
        var m = SelectedMachine;
        if (m == null) return;
        try
        {
            var rows = await Api.CallAsync<List<ErrorRow>>("errors.list", new { includeResolved = false }) ?? new();
            if (SelectedMachine?.AgentId != m.AgentId) return;
            foreach (var r in rows.Where(r => r.AgentId == m.AgentId)) MachineErrors.Add(r);
        }
        catch (Exception) { }
    }

    /// <summary>Atalhos da visão geral: abre Computadores já filtrado.</summary>
    public void OpenMachines(string agent, string veyon)
    {
        search = ""; Raise(nameof(Search));
        agentFilter = agent; Raise(nameof(AgentFilter));
        veyonFilter = veyon; Raise(nameof(VeyonFilter));
        ApplyFilter();
        Page = 1;
    }

    // ---------------- Credenciamento ----------------
    LinkPreview? link;
    public LinkPreview? Link { get => link; set { Set(ref link, value); Raise(nameof(LinkWarnings)); Raise(nameof(CertNamesText)); Raise(nameof(HasLinkWarnings)); } }
    public bool HasLinkWarnings => LinkWarnings != "";
    public string LinkWarnings => Link == null || Link.Warnings.Count == 0 ? "" : "• " + string.Join("\n• ", Link.Warnings);
    public string CertNamesText => Link == null ? "" : string.Join(", ", Link.CertNames);
    string code = "", codeInfo = "";
    public string Code { get => code; set { Set(ref code, value); Raise(nameof(HasCode)); Raise(nameof(CodeDisplay)); } }
    public bool HasCode => Code != "";
    public string CodeDisplay => Code.Length == 8 ? Code[..4] + " " + Code[4..] : Code;
    public string CodeInfo { get => codeInfo; set => Set(ref codeInfo, value); }
    DateTimeOffset? codeExpires;

    // ---------------- Histórico ----------------
    public ObservableCollection<HistoryRow> History { get; } = new();
    public string[] Periods { get; } = { "Hoje", "Últimos 7 dias", "Últimos 30 dias", "Tudo", "Personalizado" };
    string hPeriod = "Últimos 7 dias", hFrom = "", hTo = "";
    Choice? hMachine, hKind;
    public string HPeriod { get => hPeriod; set { Set(ref hPeriod, value ?? "Últimos 7 dias"); Raise(nameof(CustomPeriod)); if (hPeriod != "Personalizado") _ = SafeLoadHistoryAsync(); } }
    public bool CustomPeriod => HPeriod == "Personalizado";
    public string HFrom { get => hFrom; set => Set(ref hFrom, value); }
    public string HTo { get => hTo; set => Set(ref hTo, value); }
    public ObservableCollection<Choice> MachineChoices { get; } = new();
    public ObservableCollection<Choice> KindChoices { get; } = new();
    public Choice? HMachine { get => hMachine; set { Set(ref hMachine, value); _ = SafeLoadHistoryAsync(); } }
    public Choice? HKind { get => hKind; set { Set(ref hKind, value); _ = SafeLoadHistoryAsync(); } }
    public string HistoryCountText => $"{History.Count} evento(s)";
    bool loadingChoices;

    // ---------------- Erros ----------------
    public ObservableCollection<ErrorRow> Errors { get; } = new();
    List<ErrorRow> allErrors = new();
    bool showResolved;
    public bool ShowResolved { get => showResolved; set { Set(ref showResolved, value); Raise(nameof(ShowActive)); FilterErrors(); } }
    public bool ShowActive { get => !showResolved; set => ShowResolved = !value; }
    Choice? errMachine;
    public Choice? ErrMachine { get => errMachine; set { Set(ref errMachine, value); FilterErrors(); } }
    ErrorRow? selectedError;
    public ErrorRow? SelectedError
    {
        get => selectedError;
        set { Set(ref selectedError, value); foreach (var n in new[] { nameof(HasSelectedError), nameof(ErrorTitle), nameof(ErrorExplain), nameof(CanRetryError), nameof(ErrorWhen) }) Raise(n); }
    }
    public bool HasSelectedError => SelectedError != null;
    public int ActiveErrorCount => allErrors.Count(e => !e.Resolved);
    public string ActiveErrorsText => ActiveErrorCount switch { 0 => "Nenhum erro ativo", 1 => "1 erro ativo", var n => $"{n} erros ativos" };
    public string ActiveErrorsKind => ActiveErrorCount > 0 ? "err" : "ok";
    public string ErrorTitle => SelectedError == null ? "" : ErrorSourceTitle(SelectedError.Source);
    public string ErrorWhen => SelectedError == null ? "" :
        $"Primeira ocorrência: {SelectedError.Ts.ToLocalTime():dd/MM/yyyy HH:mm:ss} · última: {SelectedError.LastTs.ToLocalTime():dd/MM/yyyy HH:mm:ss} · {SelectedError.Count} ocorrência(s)";
    public string ErrorExplain => SelectedError == null ? "" : SelectedError.Source switch
    {
        "Veyon" => "A integração deste computador com o Veyon não foi concluída. Confira a prévia em Integração; dá para repetir só as falhas.",
        "Veyon-geral" => "A integração com o Veyon foi interrompida antes de terminar. Nada é apagado; repita depois de conferir o Veyon CLI.",
        "Identificação" => "Os dados de identificação deste computador estão em conflito com outro (número repetido ou imagem clonada).",
        "Professor parceiro" => "Não foi possível falar com o professor de outro laboratório autorizado.",
        _ => "Registro técnico do Veyon Sync."
    };
    public bool CanRetryError => SelectedError != null && !SelectedError.Resolved && SelectedError.Source.StartsWith("Veyon", StringComparison.Ordinal);
    static string ErrorSourceTitle(string s) => s switch
    {
        "Veyon" => "Falha na integração com o Veyon",
        "Veyon-geral" => "Integração com o Veyon interrompida",
        "Identificação" => "Conflito de identificação",
        "Professor parceiro" => "Professor parceiro indisponível",
        _ => s
    };

    // ---------------- Integração Veyon ----------------
    string veyonStatus = "";
    public string VeyonStatus { get => veyonStatus; set => Set(ref veyonStatus, value); }
    bool cliOk, cliFound;
    public bool CliOk { get => cliOk; set { Set(ref cliOk, value); Raise(nameof(CliStateKind)); } }
    public bool CliFound { get => cliFound; set { Set(ref cliFound, value); Raise(nameof(CliStateKind)); } }
    public string CliStateKind => CliOk ? "ok" : CliFound ? "warn" : "err";
    string cliTitle = "Verificando o Veyon CLI...";
    public string CliTitle { get => cliTitle; set => Set(ref cliTitle, value); }
    BackupRow? lastBackup;
    public BackupRow? LastBackup { get => lastBackup; set { Set(ref lastBackup, value); Raise(nameof(LastBackupText)); } }
    public string LastBackupText => LastBackup == null ? "Nenhum backup ainda" : LastBackup.Ts.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
    public ObservableCollection<VeyonChange> Changes { get; } = new();
    public int AddCount => Changes.Count(c => c.Action == "Adicionar");
    public int UpdateCount => Changes.Count(c => c.Action == "Atualizar");
    public int SameCount => Changes.Count(c => c.Action == "Já atualizado");
    public int ConflictCount => Changes.Count(c => c.Action == "Conflito");
    public bool HasConflicts => ConflictCount > 0;
    bool previewLoaded;
    public bool PreviewLoaded { get => previewLoaded; set => Set(ref previewLoaded, value); }
    public ObservableCollection<BatchSummary> BatchResults { get; } = new();
    public ObservableCollection<VeyonItemResult> BatchItems { get; } = new();
    public bool HasBatch => BatchResults.Count > 0;
    public bool BatchHasFailures => BatchItems.Any(i => i.Outcome is "Erro" or "Conflito");
    string batchSummary = "";
    public string BatchSummaryText { get => batchSummary; set => Set(ref batchSummary, value); }
    string batchKind = "info";
    public string BatchKind { get => batchKind; set => Set(ref batchKind, value); }
    public ObservableCollection<BackupRow> Backups { get; } = new();
    public ObservableCollection<SelectablePeer> Peers { get; } = new();
    bool includeLocal = true;
    public bool IncludeLocal { get => includeLocal; set => Set(ref includeLocal, value); }
    string previewSummary = "";
    public string PreviewSummary { get => previewSummary; set => Set(ref previewSummary, value); }
    bool integrating;
    public bool Integrating { get => integrating; set => Set(ref integrating, value); }

    // ---------------- Atualizações (distribuição aos alunos) ----------------
    public ObservableCollection<UpdateRow> UpdatesList { get; } = new();
    public ObservableCollection<SelectableMachine> UpdateTargets { get; } = new();
    string packagePath = "", packageInfo = "", packageVersion = "";
    public string PackagePath { get => packagePath; set { Set(ref packagePath, value); Raise(nameof(HasPackage)); } }
    public bool HasPackage => PackagePath != "";
    public string PackageInfoText { get => packageInfo; set => Set(ref packageInfo, value); }
    UpdateRow? selectedUpdate;
    public UpdateRow? SelectedUpdate { get => selectedUpdate; set { Set(ref selectedUpdate, value); Raise(nameof(UpdateCounts)); } }
    public string UpdateCounts => SelectedUpdate == null ? "" :
        $"Pendentes: {SelectedUpdate.Pending} · Transferindo/instalando: {SelectedUpdate.Downloading} · Confirmadas: {SelectedUpdate.Done} · Falhas: {SelectedUpdate.Failed}";
    SelectableMachine? testTarget;
    public SelectableMachine? TestTarget { get => testTarget; set => Set(ref testTarget, value); }
    string downloadStage = "";
    public string DownloadStage { get => downloadStage; set => Set(ref downloadStage, value); }

    // ---------------- Configurações ----------------
    public TeacherSettingsForm Form { get; } = new();
    public List<Choice> NameChoices { get; } = Naming.Options.Select(o => new Choice(o.Key, o.Label)).ToList();
    public List<Choice> AddressChoices { get; } = new() { new("ip", "IP atual do aluno"), new("hostname", "Nome do Windows (hostname)") };
    public Choice? NameChoice
    {
        get => NameChoices.FirstOrDefault(c => c.Key == Form.NameTemplate);
        set { if (value != null) { Form.NameTemplate = value.Key; Raise(); } }
    }
    public Choice? AddressChoice
    {
        get => AddressChoices.FirstOrDefault(c => c.Key == Form.AddressMode);
        set { if (value != null) { Form.AddressMode = value.Key; Raise(); } }
    }
    public bool Animations { get => ShellState.Current.Animations; set { ShellState.Current.Animations = value; Raise(); } }
    public bool SettingsDirty => Form.Dirty || Release.RepoDirty;

    // ---------------- Comandos ----------------
    public ICommand GoCmd { get; }
    public ICommand RefreshCmd { get; }
    public ICommand CollectAllCmd { get; }
    public ICommand NewCodeCmd { get; }
    public ICommand CopyCodeCmd { get; }
    public ICommand CopyUrlCmd { get; }
    public ICommand ExportLinkCmd { get; }
    public ICommand LoadHistoryCmd { get; }
    public ICommand ExportHistoryCmd { get; }
    public ICommand ExportErrorsCmd { get; }
    public ICommand ExportErrorCmd { get; }
    public ICommand CopyErrorCmd { get; }
    public ICommand ExportInventoryCmd { get; }
    public ICommand ResolveErrorCmd { get; }
    public ICommand RetryErrorCmd { get; }
    public ICommand PreviewCmd { get; }
    public ICommand ApplyLocalCmd { get; }
    public ICommand RetryFailedCmd { get; }
    public ICommand BackupCmd { get; }
    public ICommand RestoreCmd { get; }
    public ICommand BatchPreviewCmd { get; }
    public ICommand BatchApplyCmd { get; }
    public ICommand BatchRetryCmd { get; }
    public ICommand AddPeerCmd { get; }
    public ICommand RemovePeerCmd { get; }
    public ICommand PickPackageCmd { get; }
    public ICommand DownloadStdCmd { get; }
    public ICommand PublishCmd { get; }
    public ICommand PublishTestCmd { get; }
    public ICommand RetryUpdateCmd { get; }
    public ICommand SaveSettingsCmd { get; }
    public ICommand DiscardSettingsCmd { get; }
    public ICommand DiagnosticsCmd { get; }
    public ICommand SelectAllTargetsCmd { get; }
    public ICommand SelectOutdatedCmd { get; }
    public ICommand OpenMachinesCmd { get; }
    public ICommand CloseMachineCmd { get; }
    public ICommand MachineCollectCmd { get; }
    public ICommand MachineRenameCmd { get; }
    public ICommand MachineIntegrateCmd { get; }
    public ICommand MachineRemoveCmd { get; }
    public ICommand MachineHistoryCmd { get; }
    public ICommand HistoryDetailsCmd { get; }

    /// <summary>Atualização deste professor (GitHub/arquivo).</summary>
    public ReleaseVm Release { get; }

    public TeacherVm()
    {
        Release = new ReleaseVm(Api, thc: true);
        Release.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ReleaseVm.RepoDirty)) Raise(nameof(SettingsDirty)); };
        Form.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(TeacherSettingsForm.Dirty)) Raise(nameof(SettingsDirty)); };
        Changes.CollectionChanged += (_, _) => { foreach (var n in new[] { nameof(AddCount), nameof(UpdateCount), nameof(SameCount), nameof(ConflictCount), nameof(HasConflicts) }) Raise(n); };
        BatchItems.CollectionChanged += (_, _) => { Raise(nameof(HasBatch)); Raise(nameof(BatchHasFailures)); };
        History.CollectionChanged += (_, _) => Raise(nameof(HistoryCountText));

        GoCmd = new RelayCommand(p => Page = int.Parse((string)p!, CultureInfo.InvariantCulture));
        RefreshCmd = new AsyncCommand(_ => RefreshAsync());
        CollectAllCmd = new AsyncCommand(_ => RequestCollectAsync(null));
        NewCodeCmd = new AsyncCommand(_ => NewCodeAsync());
        CopyCodeCmd = new RelayCommand(_ => Clip.Copy(Code, "Código copiado"), _ => HasCode);
        CopyUrlCmd = new RelayCommand(_ => Clip.Copy(Link?.Url, "Endereço copiado"));
        ExportLinkCmd = new AsyncCommand(_ => ExportLinkAsync());
        LoadHistoryCmd = new AsyncCommand(_ => LoadHistoryAsync());
        ExportHistoryCmd = new AsyncCommand(_ => ExportAsync(HistoryTable(), "Historico", "CSV (*.csv)|*.csv|PDF (*.pdf)|*.pdf"));
        ExportErrorsCmd = new AsyncCommand(_ => ExportAsync(ErrorsTable(Errors), "Erros", TxtPdfDocx));
        ExportErrorCmd = new AsyncCommand(_ => SelectedError == null ? Task.CompletedTask : ExportAsync(ErrorsTable(new[] { SelectedError }), "Erro", TxtPdfDocx), _ => HasSelectedError);
        CopyErrorCmd = new RelayCommand(_ => Clip.Copy(ErrorText(SelectedError), "Detalhes copiados"), _ => HasSelectedError);
        ExportInventoryCmd = new AsyncCommand(_ => ExportAsync(InventoryTable(), "Inventario", "CSV (*.csv)|*.csv"));
        ResolveErrorCmd = new AsyncCommand(async p =>
        {
            if (p is not ErrorRow e) return;
            if (!ThemedDialog.Confirm("Marcar como resolvido", $"Marcar o erro \"{ErrorSourceTitle(e.Source)}\" de {Show(e.Machine)} como resolvido?",
                    warning: "Use quando o problema já foi corrigido. Se ele acontecer de novo, um novo registro é aberto.", primary: "Marcar como resolvido")) return;
            await Api.CallAsync("errors.resolve", new { id = e.Id });
            await LoadErrorsAsync();
            await RefreshQuietAsync();
        });
        RetryErrorCmd = new AsyncCommand(async _ =>
        {
            var e = SelectedError;
            if (e == null) return;
            await ApplyLocalAsync(true, string.IsNullOrEmpty(e.AgentId) ? null : new List<string> { e.AgentId });
            await LoadErrorsAsync();
        }, _ => CanRetryError && !Integrating);
        PreviewCmd = new AsyncCommand(_ => PreviewAsync());
        ApplyLocalCmd = new AsyncCommand(_ => ApplyLocalAsync(false, null), _ => !Integrating);
        RetryFailedCmd = new AsyncCommand(_ => ApplyLocalAsync(true, null), _ => !Integrating);
        BackupCmd = new AsyncCommand(async _ =>
        {
            var f = await Api.CallAsync("veyon.backup", null, TimeSpan.FromMinutes(2));
            Notice.Ok("Backup criado: " + Path.GetFileName(f?.GetValue<string>() ?? ""));
            await LoadBackupsAsync();
        });
        RestoreCmd = new AsyncCommand(p => RestoreAsync(p as BackupRow));
        BatchPreviewCmd = new AsyncCommand(_ => BatchPreviewAsync());
        BatchApplyCmd = new AsyncCommand(_ => BatchApplyAsync(false), _ => !Integrating);
        BatchRetryCmd = new AsyncCommand(_ => BatchApplyAsync(true), _ => !Integrating);
        AddPeerCmd = new AsyncCommand(_ => AddPeerAsync());
        RemovePeerCmd = new AsyncCommand(async p =>
        {
            if (p is SelectablePeer sp && Notice.Confirm($"Remover a autorização do professor do laboratório {sp.Row.Lab}?", "Remover professor parceiro"))
            { await Api.CallAsync("peers.remove", new { id = sp.Row.Id }); await LoadPeersAsync(); }
        });
        PickPackageCmd = new AsyncCommand(_ => PickPackageAsync());
        DownloadStdCmd = new AsyncCommand(_ => DownloadStdAsync());
        PublishCmd = new AsyncCommand(_ => PublishAsync(UpdateTargets.Where(t => t.Selected).ToList(), false), _ => HasPackage);
        PublishTestCmd = new AsyncCommand(_ => TestTarget == null ? Task.CompletedTask : PublishAsync(new List<SelectableMachine> { TestTarget }, true), _ => HasPackage && TestTarget != null);
        RetryUpdateCmd = new AsyncCommand(async p =>
        {
            if (p is UpdateRow u) { await Api.CallAsync("updates.retry", new { id = u.Id }); Notice.Ok("Somente as falhas foram marcadas para nova tentativa."); await LoadUpdatesAsync(); }
        }, p => p is UpdateRow u && u.Failed > 0);
        SaveSettingsCmd = new AsyncCommand(_ => SaveSettingsAsync(), _ => SettingsDirty);
        DiscardSettingsCmd = new AsyncCommand(async _ => { Settings = await Api.CallAsync<TeacherSettings>("settings.get") ?? new TeacherSettings(); Release.DiscardSettings(); }, _ => SettingsDirty);
        DiagnosticsCmd = new AsyncCommand(_ => DiagnosticsAsync());
        SelectAllTargetsCmd = new RelayCommand(_ => { var all = UpdateTargets.All(t => t.Selected); foreach (var t in UpdateTargets) t.Selected = !all; });
        SelectOutdatedCmd = new RelayCommand(_ => { foreach (var t in UpdateTargets) t.Selected = t.Outdated; });
        OpenMachinesCmd = new RelayCommand(p =>
        {
            switch (p as string)
            {
                case "recent": OpenMachines("Comunicação recente", "Todos"); break;
                case "pending": OpenMachines("Todos", VeyonStates.Pending); break;
                case "noanswer": OpenMachines("Coleta: sem resposta", "Todos"); break;
                default: OpenMachines("Todos", "Todos"); break;
            }
        });
        CloseMachineCmd = new RelayCommand(_ => SelectedMachine = null);
        MachineCollectCmd = new AsyncCommand(_ => SelectedMachine == null ? Task.CompletedTask : RequestCollectAsync(new[] { SelectedMachine }), _ => HasSelectedMachine);
        MachineRenameCmd = new AsyncCommand(_ => SelectedMachine == null ? Task.CompletedTask : RenameAsync(SelectedMachine), _ => HasSelectedMachine);
        MachineIntegrateCmd = new AsyncCommand(_ => SelectedMachine == null ? Task.CompletedTask : IntegrateAsync(new[] { SelectedMachine }), _ => HasSelectedMachine && !Integrating);
        MachineRemoveCmd = new AsyncCommand(_ => SelectedMachine == null ? Task.CompletedTask : RemoveAsync(SelectedMachine), _ => HasSelectedMachine);
        MachineHistoryCmd = new RelayCommand(_ => { if (SelectedMachine != null) MachineDetails.Show(this, SelectedMachine); }, _ => HasSelectedMachine);
        HistoryDetailsCmd = new RelayCommand(p => { if (p is HistoryRow h) ShowHistoryDetails(h); });

        timer.Tick += async (_, _) => { if (!Busy) await RefreshQuietAsync(); };
        timer.Start();
        _ = RefreshAsync();
    }

    const string TxtPdfDocx = "Texto (*.txt)|*.txt|PDF (*.pdf)|*.pdf|Word (*.docx)|*.docx";
    static string Show(string? s) => string.IsNullOrWhiteSpace(s) ? "este professor" : s;

    TeacherSettings settings = new();
    public TeacherSettings Settings { get => settings; set { Set(ref settings, value); Form.Load(value); Raise(nameof(NameChoice)); Raise(nameof(AddressChoice)); } }

    async Task OnPageAsync()
    {
        try
        {
            switch (Page)
            {
                case 3: Link = await Api.CallAsync<LinkPreview>("link.preview"); break;
                case 4: await LoadChoicesAsync(); await LoadHistoryAsync(); break;
                case 5: await LoadChoicesAsync(); await LoadErrorsAsync(); break;
                case 6:
                    await VeyonStatusAsync(); await LoadBackupsAsync(); await LoadPeersAsync();
                    if (!PreviewLoaded && CliOk) await PreviewAsync();
                    break;
                case 7: await LoadUpdatesAsync(); BuildTargets(); break;
                case 8: if (!SettingsDirty) Settings = await Api.CallAsync<TeacherSettings>("settings.get") ?? new TeacherSettings(); break;
            }
        }
        catch (Exception e) { Notice.Error(e.Message); }
    }

    public async Task RefreshAsync()
    {
        await RefreshQuietAsync();
        if (Page >= 3 && Page != 8 && Page != 6) await OnPageAsync();
    }

    async Task RefreshQuietAsync()
    {
        try
        {
            Overview = await Api.CallAsync<TeacherOverview>("overview", null, TimeSpan.FromSeconds(15));
            ServiceError = "";
            if (codeExpires != null)
            {
                var left = codeExpires.Value - DateTimeOffset.Now;
                CodeInfo = left > TimeSpan.Zero ? $"Expira em {left.Minutes} min {left.Seconds:00} s · uso único" : "Código expirado. Gere outro.";
                if (left <= TimeSpan.Zero) { Code = ""; codeExpires = null; }
            }
            if (Page == 7) RefreshTargets();
        }
        catch (Exception e) { ServiceError = "Serviço do professor indisponível: " + e.Message; }
    }

    // ---------------- Coleta ----------------
    public async Task RequestCollectAsync(IEnumerable<MachineRow>? rows)
    {
        var list = rows?.ToList();
        if (list != null && list.Count == 0) { Notice.Error("Selecione computadores."); return; }
        var r = await Api.CallAsync<CollectRound>("collect.request", new { agentIds = list?.Select(x => x.AgentId).ToList() ?? new List<string>() });
        var manual = (list ?? Overview?.Machines ?? new()).Count(x => !x.AutoMode);
        Notice.Info($"Coleta solicitada para {r?.Total ?? list?.Count ?? 0} computador(es). O andamento aparece na Visão geral." +
                    (manual > 0 ? $" {manual} em modo manual só respondem quando alguém enviar no próprio computador." : ""));
        await RefreshQuietAsync();
    }

    // ---------------- Credenciamento ----------------
    async Task NewCodeAsync()
    {
        if (HasCode && !Notice.Confirm("Gerar um código novo invalida o código atual. Continuar?", "Novo código")) return;
        var r = await Api.CallAsync("code.new");
        Code = r?["code"]?.GetValue<string>() ?? "";
        codeExpires = r?["expires"] is JsonNode n ? n.GetValue<DateTimeOffset>() : DateTimeOffset.Now.AddMinutes(15);
        CodeInfo = "Expira em 15 min · uso único";
    }

    async Task ExportLinkAsync()
    {
        Link = await Api.CallAsync<LinkPreview>("link.preview");
        if (Link != null && Link.Warnings.Count > 0 &&
            !ThemedDialog.Confirm("Arquivo de ligação", "Há avisos sobre o endereço ou o certificado deste professor:", Link.Warnings, null, "Gerar mesmo assim")) return;
        var r = await Api.CallAsync("link.export");
        var name = r?["fileName"]?.GetValue<string>() ?? "VeyonSync.vsync";
        var dlg = new SaveFileDialog { FileName = name, Filter = "Arquivo de ligação (*.vsync)|*.vsync", Title = "Salvar arquivo de ligação" };
        if (dlg.ShowDialog() != true) return;
        await File.WriteAllTextAsync(dlg.FileName, r?["content"]?.GetValue<string>() ?? "");
        Notice.Ok($"Arquivo salvo em {dlg.FileName}. Copie para a pasta Ligacoes do pendrive e use nos alunos deste laboratório.");
    }

    // ---------------- Histórico ----------------
    async Task LoadChoicesAsync()
    {
        loadingChoices = true;
        try
        {
            var keepM = hMachine?.Key; var keepK = hKind?.Key; var keepE = errMachine?.Key;
            MachineChoices.Clear();
            MachineChoices.Add(new Choice("", "Todos os computadores"));
            foreach (var m in (Overview?.Machines ?? new()).OrderBy(m => Naming.PositionNumber(m.Position) ?? int.MaxValue).ThenBy(m => m.DisplayName))
                MachineChoices.Add(new Choice(m.AgentId, string.IsNullOrEmpty(m.Position) ? m.DisplayName : $"{m.Position} · {m.WindowsName}"));
            var kinds = await Api.CallAsync<List<string>>("history.kinds") ?? new();
            KindChoices.Clear();
            KindChoices.Add(new Choice("", "Todos os tipos"));
            foreach (var k in kinds) KindChoices.Add(new Choice(k, k));
            hMachine = MachineChoices.FirstOrDefault(c => c.Key == keepM) ?? MachineChoices[0];
            hKind = KindChoices.FirstOrDefault(c => c.Key == keepK) ?? KindChoices[0];
            errMachine = MachineChoices.FirstOrDefault(c => c.Key == keepE) ?? MachineChoices[0];
            Raise(nameof(HMachine)); Raise(nameof(HKind)); Raise(nameof(ErrMachine));
        }
        finally { loadingChoices = false; }
    }

    static DateTimeOffset? ParseDate(string s, bool end)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (!DateTime.TryParseExact(s.Trim(), new[] { "dd/MM/yyyy", "d/M/yyyy", "dd/MM/yy" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            throw new FormatException($"Data inválida: {s}. Use dd/mm/aaaa.");
        return new DateTimeOffset(end ? d.AddDays(1).AddTicks(-1) : d);
    }

    async Task SafeLoadHistoryAsync()
    {
        if (loadingChoices || Page != 4) return;
        try { await LoadHistoryAsync(); } catch (Exception e) { Notice.Error(e.Message); }
    }

    async Task LoadHistoryAsync()
    {
        DateTimeOffset? from = null, to = null;
        var today = new DateTimeOffset(DateTime.Today);
        switch (HPeriod)
        {
            case "Hoje": from = today; break;
            case "Últimos 7 dias": from = today.AddDays(-6); break;
            case "Últimos 30 dias": from = today.AddDays(-29); break;
            case "Personalizado": from = ParseDate(HFrom, false); to = ParseDate(HTo, true); break;
        }
        var q = new HistoryQuery { From = from, To = to, Machine = HMachine?.Key, Kind = HKind?.Key, Limit = 5000 };
        var rows = await Api.CallAsync<List<HistoryRow>>("history.query", q) ?? new();
        History.Clear();
        foreach (var r in rows) History.Add(r);
    }

    void ShowHistoryDetails(HistoryRow h) => ThemedDialog.Details(h.Kind, new (string, string?)[]
    {
        ("Data e hora", h.Ts.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss")), ("Computador", h.Machine), ("Laboratório", h.Lab),
        ("Tipo", h.Kind), ("Resultado", h.Result), ("Descrição", h.Detail)
    });

    ReportTable HistoryTable() => new()
    {
        Title = $"Histórico — {AppInfo.TeacherShort} — {LabText}",
        Subtitle = $"Gerado em {DateTime.Now:dd/MM/yyyy HH:mm} · {HPeriod} · {History.Count} registro(s)",
        Headers = { "Data/hora", "Laboratório", "Computador", "Tipo", "Resultado", "Descrição" },
        Widths = { 1.3, 0.6, 1.3, 1.3, 0.7, 3.5 },
        Rows = History.Select(h => new List<string> { h.Ts.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss"), h.Lab, h.Machine, h.Kind, h.Result, h.Detail }).ToList()
    };

    // ---------------- Erros ----------------
    async Task LoadErrorsAsync()
    {
        var keep = SelectedError?.Id;
        allErrors = await Api.CallAsync<List<ErrorRow>>("errors.list", new { includeResolved = true }) ?? new();
        FilterErrors();
        SelectedError = Errors.FirstOrDefault(e => e.Id == keep) ?? Errors.FirstOrDefault();
        Raise(nameof(ActiveErrorCount)); Raise(nameof(ActiveErrorsText)); Raise(nameof(ActiveErrorsKind));
    }

    void FilterErrors()
    {
        var keep = SelectedError?.Id;
        Errors.Clear();
        foreach (var e in allErrors.Where(e => e.Resolved == ShowResolved && (string.IsNullOrEmpty(ErrMachine?.Key) || e.AgentId == ErrMachine.Key)))
            Errors.Add(e);
        SelectedError = Errors.FirstOrDefault(e => e.Id == keep) ?? Errors.FirstOrDefault();
    }

    static string ErrorText(ErrorRow? e) => e == null ? "" : new StringBuilder()
        .AppendLine(ErrorSourceTitle(e.Source))
        .AppendLine($"Computador: {Show(e.Machine)} · Laboratório: {e.Lab}")
        .AppendLine($"Primeira ocorrência: {e.Ts.ToLocalTime():dd/MM/yyyy HH:mm:ss} · Última: {e.LastTs.ToLocalTime():dd/MM/yyyy HH:mm:ss} · {e.Count} vez(es)")
        .AppendLine($"Situação: {(e.Resolved ? "resolvido" : "ativo")} · Origem: {e.Source}")
        .AppendLine().AppendLine(e.Message).ToString();

    ReportTable ErrorsTable(IEnumerable<ErrorRow> rows) => new()
    {
        Title = $"Erros — {AppInfo.TeacherShort} — {LabText}",
        Subtitle = $"Gerado em {DateTime.Now:dd/MM/yyyy HH:mm}",
        Headers = { "Primeira ocorrência", "Última", "Laboratório", "Computador", "Origem", "Ocorrências", "Situação", "Mensagem" },
        Widths = { 1.2, 1.2, 0.6, 1.2, 0.9, 0.6, 0.6, 3.5 },
        Rows = rows.Select(e => new List<string> { e.Ts.ToLocalTime().ToString("dd/MM/yyyy HH:mm"), e.LastTs.ToLocalTime().ToString("dd/MM/yyyy HH:mm"), e.Lab, e.Machine, e.Source, e.Count.ToString(), e.Resolved ? "resolvido" : "ativo", e.Message }).ToList()
    };

    ReportTable InventoryTable() => new()
    {
        Title = "Inventário",
        Headers = { "Laboratório", "Posição", "Nome exibido", "Nome do Windows", "Usuário", "IP", "MAC", "Última comunicação", "Estado do agente", "Estado da integração", "Versão do STD", "ID do agente" },
        Rows = (Overview?.Machines ?? new()).Select(m => new List<string> { m.Lab, m.Position, m.DisplayName, m.WindowsName, m.User ?? "", m.Ip ?? "", m.Mac ?? "",
            m.LastSeen?.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss") ?? "", m.AgentState, m.VeyonState, m.Version, m.AgentId }).ToList()
    };

    static async Task ExportAsync(ReportTable t, string baseName, string filter)
    {
        var dlg = new SaveFileDialog { FileName = $"VeyonSync_{baseName}_{DateTime.Now:yyyyMMdd_HHmm}", Filter = filter, Title = "Exportar" };
        if (dlg.ShowDialog() != true) return;
        await Task.Run(() => Exporters.Write(t, dlg.FileName));
        Notice.Ok("Exportado: " + dlg.FileName);
    }

    // ---------------- Veyon ----------------
    async Task VeyonStatusAsync()
    {
        CliTitle = "Verificando o Veyon CLI...";
        var s = await Api.CallAsync("veyon.status", null, TimeSpan.FromSeconds(45));
        CliFound = s?["found"]?.GetValue<bool>() == true;
        CliOk = CliFound && s?["ok"]?.GetValue<bool>() == true;
        CliTitle = !CliFound ? "Veyon CLI não encontrado" : CliOk ? "Veyon CLI detectado" : "Veyon CLI com problema";
        VeyonStatus = CliFound
            ? $"{s?["path"]?.GetValue<string>()} · versão {s?["version"]?.GetValue<string>()} · {s?["check"]?.GetValue<string>()}"
            : "Informe o caminho do veyon-cli.exe em Configurações.";
        LastBackup = IpcJson.From<BackupRow>(s?["lastBackup"]);
    }

    async Task LoadBackupsAsync()
    {
        var b = await Api.CallAsync<List<BackupRow>>("veyon.backups") ?? new();
        Backups.Clear();
        foreach (var x in b) Backups.Add(x);
        LastBackup = Backups.FirstOrDefault();
    }

    async Task LoadPeersAsync()
    {
        var p = await Api.CallAsync<List<PeerRow>>("peers.list") ?? new();
        Peers.Clear();
        foreach (var x in p) Peers.Add(new SelectablePeer { Row = x, Selected = x.Enabled });
    }

    async Task PreviewAsync()
    {
        Busy = true;
        try
        {
            var p = await Api.CallAsync<VeyonPreview>("veyon.preview", new { onlyPending = false }, TimeSpan.FromMinutes(3));
            Changes.Clear();
            PreviewLoaded = true;
            if (p?.Error != null) { PreviewSummary = p.Error; Notice.Error(p.Error); return; }
            foreach (var c in p?.Changes ?? new()) Changes.Add(c);
            PreviewSummary = Summarize(Changes);
        }
        finally { Busy = false; }
    }

    static string Summarize(IEnumerable<VeyonChange> c) =>
        $"{c.Count(x => x.Action == "Adicionar")} a adicionar · {c.Count(x => x.Action == "Atualizar")} a atualizar · {c.Count(x => x.Action == "Já atualizado")} sem alterações · {c.Count(x => x.Action == "Conflito")} em conflito";

    /// <summary>Integra os itens válidos (com backup antes). Conflitos nunca são aplicados.</summary>
    async Task ApplyLocalAsync(bool retryFailedOnly, List<string>? agentIds)
    {
        if (Integrating) return;
        Busy = true;
        try
        {
            var p = await Api.CallAsync<VeyonPreview>("veyon.preview", new { onlyPending = true, agentIds }, TimeSpan.FromMinutes(3));
            if (p?.Error != null) { Notice.Error(p.Error); return; }
            var work = p!.Changes.Where(c => c.Action is "Adicionar" or "Atualizar").ToList();
            var conflicts = p.Changes.Count(c => c.Action == "Conflito");
            if (agentIds == null)
            {
                Changes.Clear();
                foreach (var c in p.Changes) Changes.Add(c);
                PreviewLoaded = true;
                PreviewSummary = Summarize(p.Changes);
            }
            if (work.Count == 0 && !retryFailedOnly)
            {
                ThemedDialog.Details("Nada para integrar", new (string, string?)[]
                {
                    ("Laboratório", p.Lab), ("Resultado", conflicts > 0 ? $"{conflicts} conflito(s) precisam ser resolvidos antes; o restante já está no Veyon." : "Todos os computadores já estão iguais no Veyon.")
                });
                return;
            }
            var bullets = new List<string>();
            var add = work.Count(c => c.Action == "Adicionar");
            var upd = work.Count(c => c.Action == "Atualizar");
            if (add > 0) bullets.Add($"{add} computador(es) serão adicionados");
            if (upd > 0) bullets.Add($"{upd} computador(es) serão atualizados (sem duplicar: o registro antigo é substituído)");
            if (conflicts > 0) bullets.Add($"{conflicts} em conflito ficam de fora até serem resolvidos");
            if (retryFailedOnly) bullets.Add("Somente os que falharam na última integração");
            if (!ThemedDialog.Confirm("Integrar computadores ao Veyon", $"Laboratório {p.Lab}: confira o resumo antes de continuar.", bullets,
                    "Um backup da configuração do Veyon é feito antes. Se o backup falhar, nada é alterado. Salas e computadores não gerenciados pelo Veyon Sync são preservados.",
                    "Integrar agora")) return;
            Integrating = true;
            var s = await Api.CallAsync<BatchSummary>("veyon.apply", new { retryFailedOnly, agentIds }, TimeSpan.FromMinutes(15));
            ShowBatch(new List<BatchSummary> { s! });
        }
        finally
        {
            Integrating = false;
            Busy = false;
            await RefreshQuietAsync();
            try { await LoadBackupsAsync(); } catch (Exception) { }
        }
    }

    async Task IntegrateAsync(IEnumerable<MachineRow> rows)
    {
        var ids = rows.Select(r => r.AgentId).ToList();
        if (ids.Count == 0) { Notice.Error("Selecione computadores."); return; }
        await ApplyLocalAsync(false, ids);
    }

    public Task IntegrateSelectedAsync(IEnumerable<MachineRow> rows) => IntegrateAsync(rows);

    void ShowBatch(List<BatchSummary> list)
    {
        BatchResults.Clear(); BatchItems.Clear();
        foreach (var b in list)
        {
            BatchResults.Add(b);
            foreach (var i in b.Items) BatchItems.Add(new VeyonItemResult { AgentId = i.AgentId, Machine = list.Count > 1 ? $"[{b.Lab}] {i.Machine}" : i.Machine, Outcome = i.Outcome, Detail = i.Detail });
        }
        var msg = string.Join(" | ", list.Select(b => b.Unavailable ? $"Laboratório {b.Lab}: não executado — {b.Detail}"
            : $"Laboratório {b.Lab}: {b.Integrated} integrado(s), {b.AlreadyUpdated} já atualizado(s), {b.Conflicts} conflito(s), {b.Errors} erro(s)"));
        BatchSummaryText = msg;
        var failed = list.Any(b => b.Unavailable || b.Errors > 0);
        BatchKind = failed ? "err" : list.Any(b => b.Conflicts > 0) ? "warn" : "ok";
        Raise(nameof(HasBatch)); Raise(nameof(BatchHasFailures));
        if (failed) Notice.Error(msg); else Notice.Ok(msg);
    }

    async Task RestoreAsync(BackupRow? b)
    {
        if (b == null) { Notice.Error("Selecione um backup."); return; }
        if (!ThemedDialog.Confirm("Restaurar backup do Veyon", $"Restaurar a configuração do Veyon deste professor (laboratório {Overview?.Lab}) a partir de {Path.GetFileName(b.File)}?",
                new[] { "Um backup do estado atual é criado antes.", "Outros laboratórios não são alterados.", "Os computadores ficam marcados para reconferência na próxima integração." },
                null, "Restaurar", danger: true)) return;
        Busy = true;
        try
        {
            await Api.CallAsync("veyon.restore", new { id = b.Id }, TimeSpan.FromMinutes(5));
            Notice.Ok("Backup restaurado.");
            await LoadBackupsAsync();
        }
        finally { Busy = false; }
    }

    List<long> SelectedPeerIds() => Peers.Where(p => p.Selected && p.Row.Enabled).Select(p => p.Row.Id).ToList();

    async Task BatchPreviewAsync()
    {
        Busy = true;
        try
        {
            var list = await Api.CallAsync<List<VeyonPreview>>("batch.preview", new { includeLocal = IncludeLocal, peerIds = SelectedPeerIds() }, TimeSpan.FromMinutes(5)) ?? new();
            Changes.Clear();
            foreach (var p in list) foreach (var c in p.Changes) Changes.Add(c);
            PreviewLoaded = true;
            PreviewSummary = string.Join("\n", list.Select(p => p.Error != null ? $"Laboratório {p.Lab}: {p.Error}" : $"Laboratório {p.Lab}: {Summarize(p.Changes)}"));
        }
        finally { Busy = false; }
    }

    async Task BatchApplyAsync(bool retryFailedOnly)
    {
        var ids = SelectedPeerIds();
        if (!IncludeLocal && ids.Count == 0) { Notice.Error("Selecione ao menos um laboratório."); return; }
        await BatchPreviewAsync();
        var labs = (IncludeLocal ? new[] { Overview?.Lab ?? "" } : Array.Empty<string>()).Concat(Peers.Where(p => ids.Contains(p.Row.Id)).Select(p => p.Row.Lab));
        if (!ThemedDialog.Confirm("Integrar laboratórios ao Veyon", $"Integrar {(retryFailedOnly ? "somente as falhas" : "os pendentes")} dos laboratórios: {string.Join(", ", labs)}.",
                PreviewSummary.Split('\n'), "Cada professor aplica no próprio Veyon, com backup antes. Um professor indisponível não impede os demais.", "Integrar agora")) return;
        Busy = true; Integrating = true;
        try
        {
            var r = await Api.CallAsync<List<BatchSummary>>("batch.apply", new { includeLocal = IncludeLocal, peerIds = ids, retryFailedOnly }, TimeSpan.FromMinutes(30)) ?? new();
            ShowBatch(r);
        }
        finally { Busy = false; Integrating = false; await RefreshQuietAsync(); await LoadPeersAsync(); }
    }

    async Task AddPeerAsync()
    {
        var dlg = new OpenFileDialog { Filter = "Arquivo de ligação (*.vsync)|*.vsync", Title = "Arquivo de ligação do OUTRO laboratório" };
        if (dlg.ShowDialog() != true) return;
        var content = await File.ReadAllTextAsync(dlg.FileName);
        var c = InputBox.Ask("Código do outro professor", "No THC do outro laboratório, gere um código em Credenciamento e digite aqui (8 dígitos):");
        if (string.IsNullOrWhiteSpace(c)) return;
        var p = await Api.CallAsync<PeerRow>("peers.add", new { linkContent = content, code = c }, TimeSpan.FromSeconds(40));
        Notice.Ok($"Professor do laboratório {p?.Lab} ({p?.Name}) autorizado para integração em lote.");
        await LoadPeersAsync();
    }

    // ---------------- Atualizações ----------------
    async Task LoadUpdatesAsync()
    {
        var keep = SelectedUpdate?.Id;
        var u = await Api.CallAsync<List<UpdateRow>>("updates.list") ?? new();
        UpdatesList.Clear();
        foreach (var x in u.Where(x => x.Product == "STD" || x.Product == "")) UpdatesList.Add(x);
        SelectedUpdate = UpdatesList.FirstOrDefault(x => x.Id == keep) ?? UpdatesList.FirstOrDefault();
    }

    void BuildTargets()
    {
        var keep = UpdateTargets.Where(t => t.Selected).Select(t => t.Row.AgentId).ToHashSet();
        var test = TestTarget?.Row.AgentId;
        UpdateTargets.Clear();
        foreach (var m in (Overview?.Machines ?? new()).OrderBy(m => Naming.PositionNumber(m.Position) ?? int.MaxValue).ThenBy(m => m.DisplayName))
            UpdateTargets.Add(new SelectableMachine
            {
                Row = m, Selected = keep.Contains(m.AgentId),
                Outdated = packageVersion != "" && AppInfo.CompareVersions(m.Version, packageVersion) < 0
            });
        TestTarget = UpdateTargets.FirstOrDefault(t => t.Row.AgentId == test);
    }

    int refreshTick;
    void RefreshTargets()
    {
        // Atualiza a lista de destinos e o progresso sem perder a seleção (a cada ~15 s nesta tela).
        if (++refreshTick % 3 != 0) return;
        BuildTargets();
        _ = LoadUpdatesSafeAsync();
    }

    async Task LoadUpdatesSafeAsync() { try { await LoadUpdatesAsync(); } catch (Exception) { } }

    void UsePackage(string path, string version, string min, string origin)
    {
        PackagePath = path;
        packageVersion = version;
        PackageInfoText = $"Veyon Sync STD {version} · {origin} · exige {min} ou superior no aluno";
        BuildTargets();
        foreach (var t in UpdateTargets) t.Selected = t.Outdated;
    }

    async Task PickPackageAsync()
    {
        var dlg = new OpenFileDialog { Filter = "Pacote do aluno (*.vsupd;*.zip)|*.vsupd;*.zip", Title = "Selecionar pacote do aluno (STD)" };
        if (dlg.ShowDialog() != true) return;
        var file = dlg.FileName;
        string product, version, min;
        var work = Path.Combine(Path.GetTempPath(), "VeyonSync-pacote-" + Guid.NewGuid().ToString("N")[..8]);
        if (file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && !await Task.Run(() => IsLegacyZip(file)))
            file = await Task.Run(() => ReleasePackage.ResolveUpdateFile(dlg.FileName, work));
        if (file.EndsWith(ReleasePackage.Extension, StringComparison.OrdinalIgnoreCase) || !file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            // Exibição apenas; assinatura e integridade são conferidas pelo serviço ao distribuir.
            var m = await Task.Run(() => ReleasePackage.Peek(file)) ?? throw new InvalidDataException("Arquivo de atualização ilegível.");
            (product, version, min) = (m.Product, m.Version, m.MinVersion);
        }
        else
        {
            var info = await Task.Run(() => PackageInfo.Read(file));
            (product, version, min) = (info.Product, info.Version, info.MinVersion);
        }
        if (product != "STD")
        {
            Notice.Error($"Este pacote é do {ReleasePackage.ProductLabel(product)}. Para os alunos, use o pacote do STD (VeyonSync-STD-Update). O do professor se instala na aba \"Este professor\".");
            return;
        }
        UsePackage(dlg.FileName, version, min, "arquivo " + Path.GetFileName(dlg.FileName));
    }

    static bool IsLegacyZip(string zip)
    {
        using var z = System.IO.Compression.ZipFile.OpenRead(zip);
        return z.GetEntry("update.json") != null;
    }

    async Task DownloadStdAsync()
    {
        DownloadStage = "Baixando do GitHub o pacote do aluno e conferindo a assinatura...";
        try
        {
            var d = await Api.CallAsync<StdDownload>("release.stdLatest", null, TimeSpan.FromMinutes(30));
            if (d == null) throw new InvalidOperationException("Resposta vazia do serviço.");
            UsePackage(d.Path, d.Version, "2.0.0", "baixado do GitHub e conferido");
            DownloadStage = $"Pacote do aluno {d.Version} pronto para distribuir.";
        }
        catch (Exception e) { DownloadStage = "Falha: " + e.Message; throw; }
    }

    async Task PublishAsync(List<SelectableMachine> targets, bool test)
    {
        if (PackagePath == "") { Notice.Error("Selecione o pacote."); return; }
        if (targets.Count == 0) { Notice.Error("Selecione os computadores de destino."); return; }
        var offline = targets.Count(t => !t.Row.Recent);
        var bullets = new List<string>
        {
            PackageInfoText,
            test ? $"Somente o PC de teste: {Name(targets[0].Row)}" : $"{targets.Count} computador(es) deste laboratório",
            "O serviço confere edição, versão e assinatura do pacote e assina a distribuição com a chave deste professor."
        };
        if (offline > 0) bullets.Add($"{offline} sem comunicação recente: recebem quando voltarem a se comunicar");
        if (!ThemedDialog.Confirm(test ? "Enviar para o PC de teste" : "Distribuir atualização", "Confira antes de distribuir:", bullets,
                "Cada aluno faz backup e volta à versão anterior se a instalação falhar. Credenciamento e configurações são preservados.", test ? "Enviar para o teste" : "Distribuir")) return;
        await Api.CallAsync<UpdateRow>("updates.publish", new { packagePath = PackagePath, agentIds = targets.Select(t => t.Row.AgentId).ToList() }, TimeSpan.FromMinutes(3));
        Notice.Ok(test ? "Enviado para o PC de teste. Acompanhe abaixo; quando confirmar, distribua aos demais." : "Distribuição criada. O progresso aparece abaixo conforme os computadores se comunicam.");
        await LoadUpdatesAsync();
    }

    static string Name(MachineRow m) => string.IsNullOrEmpty(m.Position) ? m.DisplayName : $"{m.Position} ({m.WindowsName})";

    // ---------------- Configurações ----------------
    async Task SaveSettingsAsync()
    {
        if (Form.Errors != "") { Notice.Error(Form.Errors.Replace("\n", " ")); return; }
        var changedLab = Form.Dirty;
        if (Form.Dirty) Settings = await Api.CallAsync<TeacherSettings>("settings.save", Form.ToSettings()) ?? Settings;
        if (Release.RepoDirty) await Release.SaveSettingsAsync();
        Raise(nameof(SettingsDirty));
        Notice.Ok(changedLab ? "Configurações salvas. Se o nome exibido, a sala ou o endereço mudaram, os computadores ficam pendentes de integração." : "Configurações salvas.");
        await RefreshQuietAsync();
    }

    async Task DiagnosticsAsync()
    {
        var text = (await Api.CallAsync("diagnostics"))?.GetValue<string>() ?? "";
        var dlg = new SaveFileDialog { FileName = $"VeyonSync_THC_diagnostico_{DateTime.Now:yyyyMMdd_HHmm}.txt", Filter = "Texto (*.txt)|*.txt" };
        if (dlg.ShowDialog() != true) return;
        await File.WriteAllTextAsync(dlg.FileName, text);
        Notice.Ok("Diagnóstico salvo: " + dlg.FileName);
    }

    // ---------------- ações por máquina ----------------
    public async Task RenameAsync(MachineRow m)
    {
        var name = InputBox.Ask("Nome exibido no Veyon", $"Nome exibido para {m.WindowsName} (deixe vazio para usar o padrão configurado). Não altera o nome do Windows.", m.FriendlyOverride ?? "");
        if (name == null) return;
        await Api.CallAsync("machine.friendly", new { agentId = m.AgentId, name });
        await RefreshQuietAsync();
    }

    public async Task RemoveAsync(MachineRow m)
    {
        if (!ThemedDialog.Confirm("Remover registro", $"Remover o registro de {Name(m)} e a credencial dele?",
                new[] { "O histórico é preservado.", "O registro no Veyon NÃO é apagado.", "O computador precisará de novo credenciamento." },
                null, "Remover", danger: true)) return;
        await Api.CallAsync("machine.remove", new { agentId = m.AgentId });
        SelectedMachine = null;
        await RefreshQuietAsync();
    }
}
