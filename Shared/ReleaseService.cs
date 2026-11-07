using System.Text.Json;

namespace VeyonSync.Shared;

public sealed class ReleaseSettings
{
    public string Repo { get; set; } = "";
    public bool IncludePrerelease { get; set; }
    public bool AutoCheck { get; set; }
}

/// <summary>Estado mostrado na tela Atualizações (THC e STD).</summary>
public sealed class ReleaseStatus
{
    public string Product { get; set; } = "";
    public string Installed { get; set; } = AppInfo.Version;
    public bool PublisherKeyPresent { get; set; }
    public ReleaseSettings Settings { get; set; } = new();
    public string? AvailableVersion { get; set; }
    public string? AvailableNotes { get; set; }
    public bool AvailablePrerelease { get; set; }
    public DateTimeOffset? AvailablePublished { get; set; }
    public DateTimeOffset? LastCheck { get; set; }
    public string? LastCheckMessage { get; set; }
    public bool Busy { get; set; }
    public string Stage { get; set; } = "";
    public double Progress { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset? LastResultAt { get; set; }
    public bool? LastResultOk { get; set; }
    public string? LastResultVersion { get; set; }
    public string? LastResultDetail { get; set; }
    public string? LastResultSource { get; set; }
    public string LogFolder { get; set; } = "";
    /// <summary>Somente o THC consulta o GitHub; o STD recebe do professor ou por arquivo.</summary>
    public bool GitHubEnabled { get; set; }
}

/// <summary>Pacote STD baixado do GitHub pelo THC, já conferido, pronto para distribuir.</summary>
public sealed class StdDownload
{
    public string Path { get; set; } = "";
    public string Version { get; set; } = "";
    public string Notes { get; set; } = "";
}

/// <summary>
/// Atualização do próprio computador (THC ou STD) por GitHub Releases ou por arquivo .vsupd.
/// Roda no serviço (SYSTEM); só é acionada pelo pipe administrativo. Instala apenas por ação do administrador.
/// </summary>
public sealed class ReleaseService
{
    readonly string product;
    readonly UpdateRunner.Spec spec;
    readonly Action<string> log;
    readonly SemaphoreSlim gate = new(1, 1);
    readonly object stateLock = new();
    Persisted p;
    string stage = "";
    double progress;
    string? error;
    GitHubAsset? availableAsset;

    sealed class Persisted
    {
        public ReleaseSettings Settings { get; set; } = new();
        public string? AvailableVersion { get; set; }
        public string? AvailableNotes { get; set; }
        public bool AvailablePrerelease { get; set; }
        public DateTimeOffset? AvailablePublished { get; set; }
        public string? AvailableAssetName { get; set; }
        public string? AvailableAssetUrl { get; set; }
        public long AvailableAssetSize { get; set; }
        public DateTimeOffset? LastCheck { get; set; }
        public string? LastCheckMessage { get; set; }
        public UpdateRunResult? LastResult { get; set; }
    }

    public ReleaseService(string role, Action<string> log)
    {
        spec = UpdateRunner.For(role);
        product = role == "Teacher" ? "THC" : "STD";
        this.log = log;
        p = Load();
        if (string.IsNullOrWhiteSpace(p.Settings.Repo) && PublisherKey.DefaultRepo != null) p.Settings.Repo = PublisherKey.DefaultRepo;
        if (p.AvailableAssetUrl != null) availableAsset = new GitHubAsset(p.AvailableAssetName ?? "", p.AvailableAssetUrl, p.AvailableAssetSize);
        // Resultado gravado pelo atualizador auxiliar na última atualização.
        var rf = UpdateRunner.ResultFile(spec);
        if (File.Exists(rf))
        {
            try
            {
                var r = JsonSerializer.Deserialize<UpdateRunResult>(File.ReadAllText(rf));
                if (r != null)
                {
                    p.LastResult = r;
                    if (r.Ok && p.AvailableVersion != null && AppInfo.CompareVersions(p.AvailableVersion, AppInfo.Version) <= 0) ClearAvailable();
                }
                File.Delete(rf);
                Save();
            }
            catch (Exception e) { log("Resultado de atualização ilegível: " + e.Message); }
        }
    }

    string StateFile => Path.Combine(spec.DataDir, "release.json");
    string Work => Path.Combine(spec.DataDir, "update-tmp");
    string LogDir => Path.Combine(spec.DataDir, "logs");

    Persisted Load()
    {
        try { return File.Exists(StateFile) ? JsonSerializer.Deserialize<Persisted>(File.ReadAllText(StateFile)) ?? new() : new(); }
        catch (Exception) { return new(); }
    }

    void Save()
    {
        lock (stateLock)
        {
            Directory.CreateDirectory(spec.DataDir);
            var t = StateFile + ".tmp";
            File.WriteAllText(t, JsonSerializer.Serialize(p));
            File.Move(t, StateFile, true);
        }
    }

    void ClearAvailable()
    {
        p.AvailableVersion = null; p.AvailableNotes = null; p.AvailableAssetName = null; p.AvailableAssetUrl = null; p.AvailableAssetSize = 0;
        availableAsset = null;
    }

    void Log(string op, string text)
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            File.AppendAllText(Path.Combine(LogDir, $"atualizacao-{DateTime.Now:yyyyMMdd}.txt"), $"{DateTime.Now:HH:mm:ss} [{op}] {text}{Environment.NewLine}");
        }
        catch (Exception) { }
        log($"Atualização [{op}]: {text}");
    }

    public ReleaseStatus Status() => new()
    {
        Product = product, Installed = AppInfo.Version, PublisherKeyPresent = PublisherKey.Spki != null, Settings = p.Settings,
        AvailableVersion = p.AvailableVersion, AvailableNotes = p.AvailableNotes, AvailablePrerelease = p.AvailablePrerelease,
        AvailablePublished = p.AvailablePublished, LastCheck = p.LastCheck, LastCheckMessage = p.LastCheckMessage,
        Busy = gate.CurrentCount == 0 || File.Exists(UpdateRunner.LockFile(spec)), Stage = stage, Progress = progress, Error = error,
        LastResultAt = p.LastResult?.At, LastResultOk = p.LastResult?.Ok, LastResultVersion = p.LastResult?.ToVersion,
        LastResultDetail = p.LastResult?.Detail, LastResultSource = p.LastResult?.Source, LogFolder = LogDir,
        GitHubEnabled = GitHubAllowed
    };

    bool GitHubAllowed => product == "THC";

    void RequireGitHub()
    {
        if (!GitHubAllowed)
            throw new InvalidOperationException("No aluno (STD) as atualizações chegam pelo professor (THC) ou por arquivo. O STD não acessa o GitHub.");
    }

    public ReleaseStatus SaveSettings(ReleaseSettings s)
    {
        s.Repo = (s.Repo ?? "").Trim().Replace("https://github.com/", "", StringComparison.OrdinalIgnoreCase).TrimEnd('/');
        if (s.Repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) s.Repo = s.Repo[..^4];
        if (s.Repo != "" && !GitHubReleases.ValidRepo(s.Repo)) throw new InvalidDataException("Repositório inválido. Use o formato dono/repositorio (ex.: renan/veyonsync).");
        p.Settings = s;
        Save();
        Log("config", $"Repositório '{s.Repo}', testes={s.IncludePrerelease}, busca automática={s.AutoCheck}");
        return Status();
    }

    public async Task<ReleaseStatus> CheckAsync(CancellationToken ct)
    {
        RequireGitHub();
        if (string.IsNullOrWhiteSpace(p.Settings.Repo)) throw new InvalidDataException("Configure o repositório do GitHub primeiro.");
        error = null;
        try
        {
            var rel = await GitHubReleases.LatestAsync(p.Settings.Repo, p.Settings.IncludePrerelease, ct);
            p.LastCheck = DateTimeOffset.Now;
            var expected = ReleasePackage.AssetName(product, rel?.Version ?? "", ReleasePackage.CurrentArch);
            var asset = rel?.Assets.FirstOrDefault(a => string.Equals(a.Name, expected, StringComparison.OrdinalIgnoreCase));
            if (rel == null) { ClearAvailable(); p.LastCheckMessage = "Nenhuma versão publicada."; }
            else if (AppInfo.CompareVersions(rel.Version, AppInfo.Version) <= 0) { ClearAvailable(); p.LastCheckMessage = $"Você já está na versão mais recente ({AppInfo.Version})."; }
            else if (asset == null) { ClearAvailable(); p.LastCheckMessage = $"A versão {rel.Version} existe, mas não tem pacote {expected}."; }
            else
            {
                p.AvailableVersion = rel.Version; p.AvailableNotes = rel.Notes; p.AvailablePrerelease = rel.Prerelease; p.AvailablePublished = rel.Published;
                p.AvailableAssetName = asset.Name; p.AvailableAssetUrl = asset.Url; p.AvailableAssetSize = asset.Size;
                availableAsset = asset;
                p.LastCheckMessage = $"Versão {rel.Version} disponível.";
            }
            Save();
            Log("busca", p.LastCheckMessage ?? "");
        }
        catch (GitHubException e)
        {
            p.LastCheck = DateTimeOffset.Now; p.LastCheckMessage = e.Message; Save();
            Log("busca", "Falha: " + e.Message);
            throw new InvalidOperationException(e.Message);
        }
        return Status();
    }

    /// <summary>Baixa (com progresso) e instala a versão encontrada. Somente por ação do administrador.</summary>
    public async Task<ReleaseStatus> InstallFromGitHubAsync(CancellationToken ct)
    {
        RequireGitHub();
        if (!await gate.WaitAsync(0, ct)) throw new InvalidOperationException("Já existe uma atualização em andamento.");
        try
        {
            error = null;
            if (availableAsset == null || p.AvailableVersion == null) throw new InvalidOperationException("Nenhuma versão disponível. Clique em Buscar atualização primeiro.");
            Directory.CreateDirectory(Work);
            var file = Path.Combine(Work, Path.GetFileName(availableAsset.Name));
            stage = "Baixando " + availableAsset.Name; progress = 0;
            Log("github", $"Baixando {availableAsset.Name} ({availableAsset.Size / 1048576} MB)");
            try { await GitHubReleases.DownloadAsync(availableAsset, file, x => progress = x, ct); }
            catch (GitHubException e) { throw new InvalidOperationException(e.Message); }
            Prepare(file, "GitHub");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            error = e.Message; stage = ""; Log("github", "Falha: " + e.Message);
            throw new InvalidOperationException(e.Message);
        }
        finally { gate.Release(); }
        return Status();
    }

    /// <summary>Instala a partir de um .vsupd local (pendrive, rede), sem internet.</summary>
    public ReleaseStatus InstallFromFile(string path)
    {
        if (!gate.Wait(0)) throw new InvalidOperationException("Já existe uma atualização em andamento.");
        try
        {
            error = null;
            if (!ReleasePackage.IsUpdateFileName(path))
                throw new InvalidDataException("Escolha o arquivo de atualização (.vsupd) ou o ZIP de atualização (VeyonSync-THC-Update/VeyonSync-STD-Update).");
            if (!File.Exists(path))
                throw new FileNotFoundException("O serviço não encontrou o arquivo. Se ele está numa unidade de rede mapeada, copie para este computador ou para o pendrive.");
            Directory.CreateDirectory(Work);
            // Copia primeiro: o pendrive pode ser retirado e o arquivo não muda durante a validação.
            var local = Path.Combine(Work, "arquivo" + Path.GetExtension(path).ToLowerInvariant());
            stage = "Copiando o arquivo"; progress = 0;
            File.Copy(path, local, true);
            Log("arquivo", "Pacote: " + Path.GetFileName(path));
            // ZIP de atualização: contém exatamente um .vsupd, que passa pelas mesmas validações.
            var pkg = ReleasePackage.ResolveUpdateFile(local, Path.Combine(Work, "zip"));
            if (!string.Equals(pkg, local, StringComparison.OrdinalIgnoreCase)) try { File.Delete(local); } catch (Exception) { }
            Prepare(pkg, "Arquivo");
        }
        catch (Exception e)
        {
            error = e.Message; stage = ""; Log("arquivo", "Falha: " + e.Message);
            throw new InvalidOperationException(e.Message);
        }
        finally { gate.Release(); }
        return Status();
    }

    void Prepare(string package, string source)
    {
        stage = "Validando assinatura e conteúdo"; progress = 0;
        var v = ReleasePackage.Validate(package, product, AppInfo.Version, Path.Combine(Work, "validado"));
        Log(source, $"Pacote válido: {product} {v.Manifest.Version} ({v.Manifest.Arch}).");
        var payload = Path.Combine(Work, $"payload-{v.Manifest.Version}.zip");
        File.Move(v.PayloadPath, payload, true);
        try { File.Delete(package); } catch (Exception) { }
        stage = $"Instalando {v.Manifest.Version}: o serviço e a janela vão reiniciar"; progress = 1;
        Log(source, "Iniciando o atualizador auxiliar.");
        UpdateRunner.Launch(spec, payload, v.Manifest.Version, source);
    }

    /// <summary>
    /// THC: baixa da Release mais recente o pacote do ALUNO (STD), confere a assinatura do publicador e
    /// devolve o caminho para a distribuição pela rede (que é assinada de novo por este professor).
    /// </summary>
    public async Task<StdDownload> DownloadStdFromGitHubAsync(CancellationToken ct)
    {
        RequireGitHub();
        if (string.IsNullOrWhiteSpace(p.Settings.Repo)) throw new InvalidDataException("Configure o repositório do GitHub em Configurações.");
        if (!await gate.WaitAsync(0, ct)) throw new InvalidOperationException("Já existe um download ou atualização em andamento.");
        try
        {
            error = null;
            GitHubRelease? rel;
            try { rel = await GitHubReleases.LatestAsync(p.Settings.Repo, p.Settings.IncludePrerelease, ct); }
            catch (GitHubException e) { throw new InvalidOperationException(e.Message); }
            if (rel == null) throw new InvalidOperationException("Nenhuma versão publicada no repositório.");
            var expected = ReleasePackage.AssetName("STD", rel.Version, ReleasePackage.CurrentArch);
            var asset = rel.Assets.FirstOrDefault(a => string.Equals(a.Name, expected, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"A versão {rel.Version} não tem o pacote do aluno ({expected}).");
            var dir = Path.Combine(Work, "std");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, Path.GetFileName(asset.Name));
            stage = "Baixando " + asset.Name; progress = 0;
            Log("github-std", $"Baixando {asset.Name} ({asset.Size / 1048576} MB) para distribuir aos alunos");
            try { await GitHubReleases.DownloadAsync(asset, file, x => progress = x, ct); }
            catch (GitHubException e) { throw new InvalidOperationException(e.Message); }
            stage = "Conferindo assinatura do pacote do aluno"; progress = 1;
            var v = ReleasePackage.Validate(file, "STD", null, Path.Combine(dir, "conferido"));
            try { Directory.Delete(Path.Combine(dir, "conferido"), true); } catch (Exception) { }
            stage = "";
            Log("github-std", $"Pacote do aluno {v.Manifest.Version} conferido: {file}");
            return new StdDownload { Path = file, Version = v.Manifest.Version, Notes = v.Manifest.Notes };
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            error = e.Message; stage = ""; Log("github-std", "Falha: " + e.Message);
            throw new InvalidOperationException(e.Message);
        }
        finally { gate.Release(); }
    }

    /// <summary>Busca automática (só consulta e avisa; nunca instala sozinha). Somente no THC.</summary>
    public async Task AutoCheckLoopAsync(CancellationToken ct)
    {
        if (!GitHubAllowed) return;
        await Task.Delay(TimeSpan.FromMinutes(2), ct);
        while (!ct.IsCancellationRequested)
        {
            if (p.Settings.AutoCheck && !string.IsNullOrWhiteSpace(p.Settings.Repo) && gate.CurrentCount > 0)
            {
                try { await CheckAsync(ct); } catch (Exception e) when (e is not OperationCanceledException) { }
            }
            await Task.Delay(TimeSpan.FromHours(6), ct);
        }
    }
}
