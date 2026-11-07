using System.IO;
using System.Collections.ObjectModel;
using System.Text;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using VeyonSync.Desktop;
using VeyonSync.Shared;

namespace VeyonSync.StudentApp;

/// <summary>Etapa do envio: pending (cinza), active (em andamento), done (concluída), failed (falhou).</summary>
public sealed class SendStep : ObservableObject
{
    string state = "pending", caption = "";
    public string Title { get; init; } = "";
    public string State { get => state; set { Set(ref state, value); Raise(nameof(Kind)); Raise(nameof(Glyph)); } }
    public string Caption { get => caption; set => Set(ref caption, value); }
    public string Kind => State switch { "done" => "ok", "failed" => "err", "active" => "accent", _ => "muted" };
    public string Glyph => State switch { "done" => "\uE73E", "failed" => "\uE711", "active" => "\uE916", _ => "\uEA3A" };
}

public sealed class StudentVm : ObservableObject
{
    readonly Ipc.Client ipc = new(AppInfo.StudentPipe);
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(5) };

    public List<NavEntry> NavEntries { get; } = new()
    {
        new("\uE80F", "Visão geral"), new("\uE898", "Atualizar e enviar"), new("\uE7BA", "Erros"), new("\uE896", "Atualizações"), new("\uE713", "Configurações")
    };
    public const int PairingPage = 5;

    StudentStatus? status;
    public StudentStatus? Status { get => status; private set { if (Set(ref status, value)) RaiseDerived(); } }

    string serviceError = "";
    public string ServiceError { get => serviceError; private set { Set(ref serviceError, value); Raise(nameof(ServiceOk)); } }
    public bool ServiceOk => serviceError == "";
    public bool IsAdmin { get; } = AdminInfo.IsElevated;
    public string VersionText => "v" + AppInfo.Version;

    int page;
    public int Page { get => page; set => Set(ref page, value); }

    bool busy;
    public bool Busy { get => busy; set => Set(ref busy, value); }

    // ---------------- Configurações (cópia editável) ----------------
    bool autoSend = true, onStart = true, onLogon = true, onIp = true;
    string presence = "60";
    public bool AutoSend { get => autoSend; set { Set(ref autoSend, value); Raise(nameof(ManualMode)); Raise(nameof(SettingsDirty)); } }
    public bool ManualMode { get => !autoSend; set => AutoSend = !value; }
    public bool SendOnStart { get => onStart; set { Set(ref onStart, value); Raise(nameof(SettingsDirty)); } }
    public bool SendOnLogon { get => onLogon; set { Set(ref onLogon, value); Raise(nameof(SettingsDirty)); } }
    public bool SendOnIpChange { get => onIp; set { Set(ref onIp, value); Raise(nameof(SettingsDirty)); } }
    public string PresenceSeconds { get => presence; set { Set(ref presence, value); Raise(nameof(SettingsDirty)); } }
    public bool SettingsDirty => Status != null && (AutoSend != Status.AutoSend || SendOnStart != Status.SendOnStart || SendOnLogon != Status.SendOnLogon
                                                    || SendOnIpChange != Status.SendOnIpChange || PresenceSeconds.Trim() != Status.PresenceSeconds.ToString());
    public bool Animations { get => ShellState.Current.Animations; set { ShellState.Current.Animations = value; Raise(); } }

    // ---------------- Credenciamento ----------------
    string linkContent = "", position = "", code = "", pairMessage = "", pairKind = "info";
    string linkLab = "", linkTeacher = "", linkUrl = "";
    bool linkLoaded, pairOk;
    public bool LinkLoaded { get => linkLoaded; set { Set(ref linkLoaded, value); Raise(nameof(HasLink)); } }
    public bool HasLink => LinkLoaded || Status?.LinkImported == true;
    public string LinkLab { get => linkLab; set => Set(ref linkLab, value); }
    public string LinkTeacher { get => linkTeacher; set => Set(ref linkTeacher, value); }
    public string LinkUrl { get => linkUrl; set => Set(ref linkUrl, value); }
    public string Position { get => position; set => Set(ref position, value); }
    public string Code { get => code; set => Set(ref code, value); }
    public string PairMessage { get => pairMessage; set => Set(ref pairMessage, value); }
    public string PairKind { get => pairKind; set => Set(ref pairKind, value); }
    public bool PairOk { get => pairOk; set => Set(ref pairOk, value); }

    // ---------------- Envio ----------------
    public SendStep Step1 { get; } = new() { Title = "Coleta local" };
    public SendStep Step2 { get; } = new() { Title = "Envio" };
    public SendStep Step3 { get; } = new() { Title = "Confirmação do professor" };
    bool sending;
    public bool Sending { get => sending; set => Set(ref sending, value); }
    string sendResult = "", sendKind = "info";
    public string SendResult { get => sendResult; set => Set(ref sendResult, value); }
    public string SendKind { get => sendKind; set { Set(ref sendKind, value); Raise(nameof(SendFailed)); } }
    public bool SendFailed => SendKind == "err";

    // ---------------- Erros ----------------
    public ObservableCollection<StudentErrorRow> ErrorList { get; } = new();
    bool showResolved;
    public bool ShowResolved { get => showResolved; set { Set(ref showResolved, value); Raise(nameof(ShowActive)); FilterErrors(); } }
    public bool ShowActive { get => !showResolved; set => ShowResolved = !value; }
    StudentErrorRow? selectedError;
    public StudentErrorRow? SelectedError { get => selectedError; set { Set(ref selectedError, value); Raise(nameof(HasSelectedError)); } }
    public bool HasSelectedError => SelectedError != null;

    public ICommand RefreshCmd { get; }
    public ICommand SendCmd { get; }
    public ICommand SaveSettingsCmd { get; }
    public ICommand DiscardSettingsCmd { get; }
    public ICommand PickLinkCmd { get; }
    public ICommand PairCmd { get; }
    public ICommand ClearErrorsCmd { get; }
    public ICommand CopyErrorCmd { get; }
    public ICommand ExportErrorsCmd { get; }
    public ICommand CopyCmd { get; }
    public ICommand CopyAllCmd { get; }
    public ICommand GoCmd { get; }
    public ReleaseVm Release { get; }

    public StudentVm()
    {
        Release = new ReleaseVm(ipc, thc: false);
        RefreshCmd = new AsyncCommand(_ => RefreshAsync(true));
        SendCmd = new AsyncCommand(_ => SendAsync(), _ => Status?.Paired == true && !Sending);
        SaveSettingsCmd = new AsyncCommand(_ => SaveSettingsAsync(), _ => SettingsDirty);
        DiscardSettingsCmd = new RelayCommand(_ => LoadSettings(), _ => SettingsDirty);
        PickLinkCmd = new AsyncCommand(_ => PickLinkAsync());
        PairCmd = new AsyncCommand(_ => PairAsync(), _ => HasLink && !Busy);
        ClearErrorsCmd = new AsyncCommand(async _ =>
        {
            if (!ThemedDialog.Confirm("Marcar como resolvidos", "Marcar todos os erros ativos deste computador como resolvidos?",
                    warning: "Use quando o problema já foi corrigido. Se acontecer de novo, um novo registro é aberto.", primary: "Marcar como resolvidos")) return;
            await ipc.CallAsync("errors.clear");
            await RefreshAsync(false);
        }, _ => OpenErrorCount > 0);
        CopyErrorCmd = new RelayCommand(_ => Clip.Copy(ErrorText(SelectedError), "Detalhes copiados"), _ => HasSelectedError);
        ExportErrorsCmd = new AsyncCommand(_ => ExportErrorsAsync());
        CopyCmd = new RelayCommand(p => Clip.Copy(p as string));
        CopyAllCmd = new RelayCommand(_ => Clip.Copy(Summary(), "Dados copiados"), _ => Status != null);
        GoCmd = new RelayCommand(p => Page = int.Parse((string)p!));
        timer.Tick += async (_, _) => await RefreshAsync(false);
        timer.Start();
        _ = RefreshAsync(true);
    }

    void RaiseDerived()
    {
        foreach (var n in new[] { nameof(OpenErrorCount), nameof(ErrorsKind), nameof(ErrorsCardText), nameof(NeedsPairing), nameof(PositionText), nameof(HeaderText),
                     nameof(TeacherText), nameof(HasLink), nameof(SendCardTitle), nameof(SendCardKind), nameof(LastConfirmedText), nameof(VeyonServiceKind),
                     nameof(ModeSummary), nameof(SettingsDirty), nameof(StatusLine), nameof(UpdateStatusText), nameof(Attempts) })
            Raise(n);
    }

    public int OpenErrorCount => Status?.Errors.Count(e => !e.Resolved) ?? 0;
    public string ErrorsKind => OpenErrorCount > 0 ? "err" : "ok";
    public string ErrorsCardText => OpenErrorCount switch { 0 => "Nenhum erro ativo", 1 => "1 erro ativo", var n => $"{n} erros ativos" };
    public bool NeedsPairing => Status != null && !Status.Paired;
    public string PositionText => string.IsNullOrEmpty(Status?.Position) ? "PC sem número" : Status!.Position;
    public string HeaderText => Status == null ? "" : $"{PositionText} · {(string.IsNullOrWhiteSpace(Status.LabName) ? "sem laboratório" : Status.LabName)}";
    public string TeacherText => Status == null ? "" : string.IsNullOrWhiteSpace(Status.TeacherName) ? Status.TeacherUrl : Status.TeacherName;
    public string SendCardTitle => Status?.LastConfirmed == null ? "Ainda sem confirmação" : $"Confirmado às {Status.LastConfirmed.Value.ToLocalTime():HH:mm}";
    public string SendCardKind => Status == null ? "muted" : Status.SendStatus == "Falhou" ? "err" : Status.LastConfirmed != null ? "ok" : "warn";
    public string LastConfirmedText => Status?.LastConfirmed == null ? "Nenhum recebimento confirmado ainda." : $"Último recebimento confirmado: {Status.LastConfirmed.Value.ToLocalTime():dd/MM/yyyy HH:mm:ss}";
    public string VeyonServiceKind => Status?.VeyonService switch { "Em execução" => "ok", "Parado" => "warn", "Não encontrado" => "err", _ => "muted" };
    public string ModeSummary => Status == null ? "" : Status.AutoSend
        ? "Automático: envia ao iniciar o Windows, no login, quando o IP muda e periodicamente."
        : "Manual: só envia quando alguém clica em \"Coletar e enviar agora\". Os dados no professor podem ficar desatualizados.";
    public string StatusLine => Status == null ? "" : Status.SendStatus + (string.IsNullOrEmpty(Status.LastError) ? "" : " — " + Status.LastError);
    public string UpdateStatusText => string.IsNullOrWhiteSpace(Status?.UpdateStatus) ? "Nenhuma atualização recebida do professor até agora." : Status!.UpdateStatus!;
    public List<SendAttempt> Attempts => Status?.Attempts ?? new();

    async Task RefreshAsync(bool loadSettings)
    {
        try
        {
            var s = await ipc.CallAsync<StudentStatus>("status", null, TimeSpan.FromSeconds(10));
            ServiceError = "";
            var first = Status == null;
            Status = s;
            if (s != null && ((loadSettings || first) || !SettingsDirtyAgainst(s))) LoadSettings();
            if (s != null && !s.Paired && first) Page = PairingPage;
            FilterErrors();
            if (!Sending) StepsFromLastAttempt();
        }
        catch (Exception e) { ServiceError = "Serviço do aluno indisponível: " + e.Message; }
    }

    // Recarrega as opções do serviço só se o usuário não tiver alterações pendentes.
    bool SettingsDirtyAgainst(StudentStatus s) => SettingsDirty;

    void LoadSettings()
    {
        var s = Status;
        if (s == null) return;
        autoSend = s.AutoSend; onStart = s.SendOnStart; onLogon = s.SendOnLogon; onIp = s.SendOnIpChange; presence = s.PresenceSeconds.ToString();
        foreach (var n in new[] { nameof(AutoSend), nameof(ManualMode), nameof(SendOnStart), nameof(SendOnLogon), nameof(SendOnIpChange), nameof(PresenceSeconds), nameof(SettingsDirty) })
            Raise(n);
    }

    void StepsFromLastAttempt()
    {
        var a = Status?.Attempts.FirstOrDefault();
        if (a == null)
        {
            Step1.State = Status?.LastCollect != null ? "done" : "pending";
            Step2.State = Step3.State = "pending";
            Step1.Caption = Status?.LastCollect == null ? "" : $"{Status.LastCollect.Value.ToLocalTime():HH:mm}";
            Step2.Caption = Step3.Caption = "";
            return;
        }
        Step1.State = "done"; Step1.Caption = $"{a.Ts.ToLocalTime():HH:mm}";
        Step2.State = a.Ok ? "done" : "failed"; Step2.Caption = a.Ok ? "Concluído" : "Não confirmado";
        Step3.State = a.Ok ? "done" : "pending"; Step3.Caption = a.Ok ? "Concluída" : "Aguardando";
    }

    async Task SendAsync()
    {
        if (Sending) return;
        Sending = true; Busy = true;
        Step1.State = "active"; Step1.Caption = "Coletando...";
        Step2.State = "pending"; Step2.Caption = "";
        Step3.State = "pending"; Step3.Caption = "";
        SendResult = "Coletando os dados deste computador e enviando ao professor...";
        SendKind = "info";
        try
        {
            var r = await ipc.CallAsync<PairOutcome>("send", null, TimeSpan.FromSeconds(60));
            SendResult = r?.Message ?? "";
            if (r?.Ok == true)
            {
                Step1.State = Step2.State = Step3.State = "done";
                Step1.Caption = "Concluída"; Step2.Caption = "Concluído"; Step3.Caption = $"Às {DateTime.Now:HH:mm}";
                SendKind = "ok";
                Notice.Ok("Recebimento confirmado pelo professor.");
            }
            else
            {
                // A coleta é local; o que falhou foi o envio ou a confirmação.
                Step1.State = "done"; Step1.Caption = "Concluída";
                Step2.State = "failed"; Step2.Caption = "Não confirmado";
                Step3.State = "pending"; Step3.Caption = "Não recebida";
                SendKind = "err";
            }
        }
        catch (Exception e)
        {
            Step1.State = "failed"; Step1.Caption = "Falhou";
            SendResult = e.Message; SendKind = "err";
        }
        finally { Sending = false; Busy = false; await RefreshAsync(false); }
    }

    async Task SaveSettingsAsync()
    {
        if (!int.TryParse(PresenceSeconds, out var p) || p < 15 || p > 3600) { Notice.Error("O intervalo de presença deve ficar entre 15 e 3600 segundos."); return; }
        if (!AutoSend && Status?.AutoSend == true &&
            !ThemedDialog.Confirm("Mudar para envio manual", "No modo manual este computador só envia quando alguém clicar em \"Coletar e enviar agora\".",
                new[] { "Sem sinal de presença periódico.", "Sem envio automático ao iniciar, no login ou quando o IP mudar.", "O professor vê este PC com o selo M (envio manual)." },
                "Os dados no professor podem ficar desatualizados.", "Usar modo manual")) return;
        await ipc.CallAsync("settings.save", new StudentSettings
        {
            AutoSend = AutoSend, SendOnStart = SendOnStart, SendOnLogon = SendOnLogon, SendOnIpChange = SendOnIpChange, PresenceSeconds = p
        });
        Notice.Ok(AutoSend ? "Configurações salvas." : "Configurações salvas. Modo manual ativo.");
        await RefreshAsync(true);
    }

    // ---------------- Credenciamento ----------------
    async Task PickLinkAsync()
    {
        var dlg = new OpenFileDialog { Filter = "Arquivo de ligação Veyon Sync (*.vsync)|*.vsync", Title = "Importar arquivo de ligação" };
        if (dlg.ShowDialog() != true) return;
        var fi = new FileInfo(dlg.FileName);
        if (fi.Length > 64 * 1024) { Notice.Error("Arquivo grande demais para ser um arquivo de ligação."); return; }
        linkContent = await File.ReadAllTextAsync(dlg.FileName);
        var r = await ipc.CallAsync("link.import", new { content = linkContent, confirmChange = false });
        if (r?["needsConfirmation"]?.GetValue<bool>() == true)
        {
            var msg = r["message"]?.GetValue<string>() ?? "";
            if (!ThemedDialog.Confirm("Trocar de professor", msg, null, "Ação administrativa: exige novo código de credenciamento do novo professor.", "Trocar o vínculo", danger: true))
            { PairMessage = "Importação cancelada. O vínculo atual foi mantido."; PairKind = "info"; return; }
            r = await ipc.CallAsync("link.import", new { content = linkContent, confirmChange = true });
        }
        LinkLoaded = true;
        LinkLab = $"{r?["labName"]?.GetValue<string>()} ({r?["lab"]?.GetValue<string>()})";
        LinkTeacher = r?["teacher"]?.GetValue<string>() ?? "";
        LinkUrl = r?["url"]?.GetValue<string>() ?? "";
        PairMessage = r?["message"]?.GetValue<string>() ?? "";
        PairKind = "ok";
        await RefreshAsync(false);
    }

    async Task PairAsync()
    {
        Busy = true;
        PairMessage = "Validando a ligação, credenciando e fazendo o primeiro envio...";
        PairKind = "info";
        try
        {
            var r = await ipc.CallAsync<PairOutcome>("pair", new { position = Position, code = Code }, TimeSpan.FromSeconds(60));
            PairOk = r?.Ok == true;
            PairMessage = r?.Message ?? "";
            PairKind = PairOk ? "ok" : "err";
            Code = "";
            if (PairOk) Notice.Ok(PairMessage);
        }
        finally { Busy = false; await RefreshAsync(false); }
    }

    // ---------------- Erros ----------------
    void FilterErrors()
    {
        var keep = SelectedError;
        var list = (Status?.Errors ?? new()).Where(e => e.Resolved == ShowResolved).ToList();
        if (list.Count == ErrorList.Count && list.Zip(ErrorList).All(x => x.First.Ts == x.Second.Ts && x.First.Message == x.Second.Message && x.First.Resolved == x.Second.Resolved)) return;
        ErrorList.Clear();
        foreach (var e in list) ErrorList.Add(e);
        SelectedError = ErrorList.FirstOrDefault(e => keep != null && e.Ts == keep.Ts && e.Message == keep.Message) ?? ErrorList.FirstOrDefault();
    }

    static string ErrorText(StudentErrorRow? e) => e == null ? "" :
        $"{AppInfo.StudentShort} — {Environment.MachineName}\nData e hora: {e.Ts.ToLocalTime():dd/MM/yyyy HH:mm:ss}\nSituação: {(e.Resolved ? "resolvido" : "ativo")}\n\n{e.Message}";

    async Task ExportErrorsAsync()
    {
        var rows = ErrorList.ToList();
        var t = new ReportTable
        {
            Title = $"Erros — {AppInfo.StudentShort} — {Environment.MachineName}",
            Subtitle = $"Gerado em {DateTime.Now:dd/MM/yyyy HH:mm} · {(ShowResolved ? "resolvidos" : "ativos")}",
            Headers = { "Data e hora", "Situação", "Mensagem" },
            Widths = { 1.2, 0.6, 4 },
            Rows = rows.Select(e => new List<string> { e.Ts.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss"), e.Resolved ? "resolvido" : "ativo", e.Message }).ToList()
        };
        var dlg = new SaveFileDialog { FileName = $"VeyonSync_STD_erros_{DateTime.Now:yyyyMMdd_HHmm}", Filter = "Texto (*.txt)|*.txt|PDF (*.pdf)|*.pdf|Word (*.docx)|*.docx", Title = "Exportar erros" };
        if (dlg.ShowDialog() != true) return;
        await Task.Run(() => Exporters.Write(t, dlg.FileName));
        Notice.Ok("Exportado: " + dlg.FileName);
    }

    string Summary()
    {
        var s = Status;
        if (s == null) return "";
        return new StringBuilder()
            .AppendLine($"Computador: {s.WindowsName} ({PositionText})")
            .AppendLine($"Laboratório: {s.LabName}")
            .AppendLine($"Usuário: {s.User ?? "sem usuário conectado"}")
            .AppendLine($"IP: {s.Ip ?? "—"}")
            .AppendLine($"MAC: {s.Mac ?? "—"}")
            .AppendLine($"Professor: {TeacherText}")
            .AppendLine($"Envio: {StatusLine}")
            .ToString();
    }
}
