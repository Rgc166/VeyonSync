using System.IO;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using VeyonSync.Shared;

namespace VeyonSync.Desktop;

/// <summary>
/// Atualização do PRÓPRIO computador, compartilhada por THC e STD.
/// THC: GitHub Releases e arquivo. STD: somente arquivo (o STD não acessa o GitHub; recebe do professor).
/// </summary>
public sealed class ReleaseVm : ObservableObject
{
    readonly Ipc.Client api;
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(10) };
    bool installing;

    public bool IsThc { get; }
    public bool IsStd => !IsThc;

    ReleaseStatus? status;
    public ReleaseStatus? Status { get => status; private set { Set(ref status, value); RaiseAll(); } }

    string repo = "", message = "";
    bool includePre, autoCheck;
    string loadedRepo = "";
    bool loadedPre, loadedAuto;
    public string Repo { get => repo; set { Set(ref repo, value); Raise(nameof(RepoDirty)); } }
    public bool IncludePrerelease { get => includePre; set { Set(ref includePre, value); Raise(nameof(RepoDirty)); } }
    public bool AutoCheck { get => autoCheck; set { Set(ref autoCheck, value); Raise(nameof(RepoDirty)); } }
    public bool RepoDirty => (Repo ?? "").Trim() != loadedRepo || IncludePrerelease != loadedPre || AutoCheck != loadedAuto;
    public string Message { get => message; set { Set(ref message, value); Raise(nameof(MessageKind)); } }
    public string MessageKind => Message.StartsWith("Erro") || Message.StartsWith("Falha") ? "err" : StateGlyphConverter.Classify(Message.ToLowerInvariant()) switch { "ok" => "ok", "warn" => "warn", "err" => "err", _ => "info" };

    public string ProductText => IsThc ? "Veyon Sync THC (este professor)" : "Veyon Sync STD (este computador)";
    public string InstalledText => Status?.Installed ?? AppInfo.Version;
    public bool HasAvailable => !string.IsNullOrEmpty(Status?.AvailableVersion);
    public string AvailableText => HasAvailable ? Status!.AvailableVersion! : "—";
    public string AvailableTag => HasAvailable ? (Status!.AvailablePrerelease ? "Versão de teste" : "Nova versão") : "";
    public string NotesTitle => HasAvailable ? $"Notas da versão {Status!.AvailableVersion}" : "Notas da versão";
    public string NotesText => string.IsNullOrWhiteSpace(Status?.AvailableNotes) ? "Sem notas para esta versão." : Status!.AvailableNotes!;
    public string LastCheckText => Status?.LastCheck == null ? "Ainda não consultado." : $"Última consulta: {Status.LastCheck.Value.ToLocalTime():dd/MM/yyyy HH:mm} — {Status.LastCheckMessage}";
    public bool HasLastResult => Status?.LastResultAt != null;
    public string LastResultKind => Status?.LastResultOk == true ? "ok" : "err";
    public string LastResultTitle => Status?.LastResultAt == null ? "Nenhuma atualização registrada neste computador."
        : Status.LastResultOk == true ? $"Atualizado para {Status.LastResultVersion}" : $"Atualização para {Status.LastResultVersion} não concluída";
    public string LastResultText => Status?.LastResultAt == null ? ""
        : $"{Status.LastResultAt.Value.ToLocalTime():dd/MM/yyyy HH:mm} · origem: {Status.LastResultSource}. {Status.LastResultDetail}";
    public bool Busy => installing || Status?.Busy == true;
    public double ProgressPercent => (Status?.Progress ?? 0) * 100;
    public string StageText => Status?.Error != null ? "Erro: " + Status.Error : Status?.Stage ?? "";
    public bool KeyMissing => Status != null && !Status.PublisherKeyPresent;
    public string RepoText => string.IsNullOrWhiteSpace(Status?.Settings.Repo) ? "Repositório não configurado (Configurações → Atualizações)." : "Repositório: " + Status!.Settings.Repo;

    public ICommand RefreshCmd { get; }
    public ICommand CheckCmd { get; }
    public ICommand InstallCmd { get; }
    public ICommand FileCmd { get; }
    public ICommand SaveCmd { get; }

    public ReleaseVm(Ipc.Client api, bool thc)
    {
        this.api = api;
        IsThc = thc;
        RefreshCmd = new AsyncCommand(_ => RefreshAsync(true));
        CheckCmd = new AsyncCommand(async _ =>
        {
            Message = "Consultando o GitHub...";
            try
            {
                Status = await api.CallAsync<ReleaseStatus>("release.check", null, TimeSpan.FromSeconds(60));
                Message = Status?.LastCheckMessage ?? "";
            }
            catch (Exception e) { Message = "Falha: " + e.Message; throw; }
        }, _ => IsThc && !Busy);
        InstallCmd = new AsyncCommand(_ => InstallAsync(false), _ => IsThc && HasAvailable && !Busy);
        FileCmd = new AsyncCommand(_ => InstallAsync(true), _ => !Busy);
        SaveCmd = new AsyncCommand(async _ => { await SaveSettingsAsync(); Notice.Ok("Configurações de atualização salvas."); });
        timer.Tick += async (_, _) => await RefreshAsync(false);
        timer.Start();
        _ = RefreshAsync(true);
    }

    void RaiseAll()
    {
        foreach (var n in new[] { nameof(InstalledText), nameof(HasAvailable), nameof(AvailableText), nameof(AvailableTag), nameof(NotesTitle), nameof(NotesText),
                     nameof(LastCheckText), nameof(HasLastResult), nameof(LastResultKind), nameof(LastResultTitle), nameof(LastResultText), nameof(Busy),
                     nameof(ProgressPercent), nameof(StageText), nameof(KeyMissing), nameof(RepoText) })
            Raise(n);
    }

    void LoadSettings()
    {
        if (Status == null) return;
        loadedRepo = Status.Settings.Repo ?? ""; loadedPre = Status.Settings.IncludePrerelease; loadedAuto = Status.Settings.AutoCheck;
        Repo = loadedRepo; IncludePrerelease = loadedPre; AutoCheck = loadedAuto;
    }

    /// <summary>Grava repositório e opções (usado pela tela Configurações do THC).</summary>
    public async Task SaveSettingsAsync()
    {
        Status = await api.CallAsync<ReleaseStatus>("release.settings", new ReleaseSettings { Repo = Repo ?? "", IncludePrerelease = IncludePrerelease, AutoCheck = AutoCheck });
        LoadSettings();
    }

    public void DiscardSettings() => LoadSettings();

    async Task RefreshAsync(bool loadSettings)
    {
        try
        {
            Status = await api.CallAsync<ReleaseStatus>("release.status", null, TimeSpan.FromSeconds(10));
            if (loadSettings) LoadSettings();
            timer.Interval = TimeSpan.FromSeconds(Busy ? 1 : 10);
        }
        catch (Exception e)
        {
            // Durante a instalação o serviço é reiniciado: a falha de conexão é esperada.
            Message = installing ? "Atualizando: o serviço está reiniciando. Aguarde..." : e.Message;
        }
    }

    async Task InstallAsync(bool fromFile)
    {
        string? path = null;
        string what;
        if (fromFile)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Atualização do Veyon Sync (*.vsupd;*.zip)|*.vsupd;*.zip",
                Title = IsThc ? "Atualizar o professor (THC) por arquivo" : "Atualizar este aluno (STD) por arquivo"
            };
            if (dlg.ShowDialog() != true) return;
            path = dlg.FileName;
            what = Path.GetFileName(path);
            // Aviso antecipado (a validação real, com assinatura, acontece no serviço antes de qualquer alteração).
            var expected = IsThc ? "THC" : "STD";
            var other = IsThc ? "STD" : "THC";
            if (what.Contains("-" + other + "-", StringComparison.OrdinalIgnoreCase))
            {
                Notice.Error($"Este arquivo é do {ReleasePackage.ProductLabel(other)}. Neste computador use o pacote do {ReleasePackage.ProductLabel(expected)}.");
                return;
            }
        }
        else what = "a versão " + Status?.AvailableVersion;

        if (!ThemedDialog.Confirm("Atualizar o Veyon Sync", $"Instalar {what} neste computador?",
                new[]
                {
                    "O pacote é conferido (edição, versão, assinatura e integridade) antes de qualquer alteração.",
                    "Backup do programa e dos dados (configurações, credenciamento, certificados, banco e histórico).",
                    "O serviço para por alguns instantes e esta janela fecha; abra-a novamente depois.",
                    "Se algo falhar, a versão anterior e os dados são restaurados automaticamente."
                },
                "Evite fazer isso durante uma aula. O Windows não será reiniciado.", "Instalar agora")) return;
        installing = true;
        timer.Interval = TimeSpan.FromSeconds(1);
        Raise(nameof(Busy));
        try
        {
            Message = fromFile ? "Copiando e validando o arquivo..." : "Baixando a atualização...";
            Status = fromFile
                ? await api.CallAsync<ReleaseStatus>("release.installFile", new { path }, TimeSpan.FromMinutes(5))
                : await api.CallAsync<ReleaseStatus>("release.install", null, TimeSpan.FromMinutes(30));
            Message = "Pacote válido. Instalando: o serviço e esta janela vão reiniciar.";
        }
        catch (Exception e)
        {
            installing = false;
            Message = "Erro: " + e.Message;
            Notice.Error(e.Message);
            await RefreshAsync(false);
        }
    }
}
