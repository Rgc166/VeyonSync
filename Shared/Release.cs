using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VeyonSync.Shared;

/// <summary>
/// Manifesto de um pacote de atualização (.vsupd). Assinado pela chave privada do publicador
/// (que fica só no PC de desenvolvimento); conferido com a chave pública embutida no programa.
/// </summary>
public sealed class ReleaseManifest
{
    public string Format { get; set; } = ReleasePackage.FormatName;
    public int FormatVersion { get; set; } = 1;
    public string Product { get; set; } = "";       // THC | STD
    public string Version { get; set; } = "";
    public string Arch { get; set; } = "x64";
    public string MinVersion { get; set; } = "2.0.0";
    public bool Prerelease { get; set; }
    public string Notes { get; set; } = "";
    public string PayloadSha256 { get; set; } = "";
    public long PayloadSize { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public static class PublisherKey
{
    static string? Res(string name)
    {
        try
        {
            using var s = typeof(PublisherKey).Assembly.GetManifestResourceStream(name);
            if (s == null) return null;
            using var r = new StreamReader(s);
            var t = r.ReadToEnd().Trim();
            return t.Length == 0 ? null : t;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Chave pública (SPKI base64) embutida na compilação; null se esta versão foi compilada sem ela.</summary>
    public static string? Spki { get; } = Res("VeyonSync.PublisherKey");

    /// <summary>Repositório padrão (owner/repo) embutido; null se não configurado na compilação.</summary>
    public static string? DefaultRepo { get; } = Res("VeyonSync.DefaultRepo");
}

public sealed class ValidatedRelease
{
    public ReleaseManifest Manifest { get; init; } = new();
    public string PayloadPath { get; init; } = "";
}

/// <summary>
/// Pacote .vsupd = ZIP com exatamente: manifest.json, manifest.sig e payload.zip.
/// payload.zip = update.json + Service/ + App/ (o mesmo formato já usado na distribuição THC → STD).
/// </summary>
public static class ReleasePackage
{
    public const string FormatName = "veyonsync-release";
    public const string Extension = ".vsupd";
    const long MaxPackage = 1_600_000_000;

    public static string ProductLabel(string p) => p == "THC" ? "THC (professor)" : p == "STD" ? "STD (aluno)" : p;
    public static string CurrentArch => Environment.Is64BitOperatingSystem ? "x64" : "x86";
    public static string AssetName(string product, string version, string arch = "x64") => $"VeyonSync-{product}-{version}-win-{arch}{Extension}";

    public static bool IsUpdateFileName(string path) =>
        path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) || path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Aceita o .vsupd direto ou o ZIP de entrega (VeyonSync-THC-Update-x.zip / VeyonSync-STD-Update-x.zip),
    /// que contém exatamente um .vsupd e instruções. Devolve o caminho do .vsupd; a validação completa vem depois.
    /// </summary>
    public static string ResolveUpdateFile(string path, string workDir)
    {
        if (path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)) return path;
        ZipArchive z;
        try { z = ZipFile.OpenRead(path); }
        catch (InvalidDataException) { throw new InvalidDataException("O arquivo não é um ZIP válido (corrompido ou cópia incompleta)."); }
        using (z)
        {
            var names = z.Entries.Select(x => x.FullName).ToList();
            // .vsupd renomeado para .zip: já é o pacote.
            if (names.Contains("manifest.json") && names.Contains("manifest.sig") && names.Contains("payload.zip")) return path;
            var inner = z.Entries.Where(x => x.FullName.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)).ToList();
            if (inner.Count == 0)
                throw new InvalidDataException("Este ZIP não contém um pacote de atualização do Veyon Sync (.vsupd). ZIPs de código-fonte não são atualizações.");
            if (inner.Count > 1)
                throw new InvalidDataException("Este ZIP contém mais de um pacote .vsupd. Use o ZIP específico do professor (THC) ou do aluno (STD).");
            var entry = inner[0];
            if (entry.FullName.Contains("..") || entry.FullName.Contains(':') || entry.Length > MaxPackage)
                throw new InvalidDataException("Pacote dentro do ZIP com nome ou tamanho inválido.");
            if (Directory.Exists(workDir)) Directory.Delete(workDir, true);
            Directory.CreateDirectory(workDir);
            var dest = Path.Combine(workDir, "pacote" + Extension);
            entry.ExtractToFile(dest, true);
            return dest;
        }
    }

    /// <summary>Lê o manifesto SEM conferir a assinatura: somente para exibir na tela antes da validação real.</summary>
    public static ReleaseManifest? Peek(string path)
    {
        try
        {
            using var z = ZipFile.OpenRead(path);
            var e = z.GetEntry("manifest.json");
            if (e == null || e.Length > 256 * 1024) return null;
            using var s = e.Open();
            return JsonSerializer.Deserialize<ReleaseManifest>(s, IpcJson.Options);
        }
        catch (Exception) { return null; }
    }

    /// <summary>Monta o pacote assinado (usado pela ferramenta de publicação).</summary>
    public static void Create(string payloadZip, ReleaseManifest m, ECDsa privateKey, string outPath)
    {
        m.PayloadSha256 = UpdateSigning.FileSha256(payloadZip);
        m.PayloadSize = new FileInfo(payloadZip).Length;
        m.CreatedAt = DateTimeOffset.UtcNow;
        var json = JsonSerializer.SerializeToUtf8Bytes(m, IpcJson.Options);
        var sig = privateKey.SignData(json, HashAlgorithmName.SHA256);
        if (File.Exists(outPath)) File.Delete(outPath);
        using var z = ZipFile.Open(outPath, ZipArchiveMode.Create);
        using (var s = z.CreateEntry("manifest.json").Open()) s.Write(json);
        using (var s = z.CreateEntry("manifest.sig").Open()) s.Write(Encoding.ASCII.GetBytes(Convert.ToBase64String(sig)));
        z.CreateEntryFromFile(payloadZip, "payload.zip", CompressionLevel.NoCompression);
    }

    /// <summary>
    /// Valida tudo ANTES de qualquer alteração: estrutura, assinatura, produto, arquitetura, versão
    /// (sem downgrade), compatibilidade, integridade do conteúdo e caminhos seguros.
    /// Extrai payload.zip para <paramref name="stagingDir"/>.
    /// </summary>
    public static ValidatedRelease Validate(string path, string expectedProduct, string? installedVersion, string stagingDir,
        string? publicKeySpki = null, bool allowNotNewer = false)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists) throw new InvalidDataException("Arquivo de atualização não encontrado.");
        if (fi.Length > MaxPackage) throw new InvalidDataException("Arquivo grande demais para ser uma atualização do Veyon Sync.");
        var key = publicKeySpki ?? PublisherKey.Spki
            ?? throw new InvalidDataException("Esta instalação foi compilada sem a chave do publicador: atualize uma vez pelo pendrive (INICIAR.bat > Aplicar atualização).");

        ZipArchive z;
        try { z = ZipFile.OpenRead(path); }
        catch (InvalidDataException) { throw new InvalidDataException("O arquivo não é um pacote válido (ZIP corrompido ou download incompleto)."); }
        using (z)
        {
            var names = z.Entries.Select(e => e.FullName).OrderBy(n => n).ToList();
            if (!names.SequenceEqual(new[] { "manifest.json", "manifest.sig", "payload.zip" }))
                throw new InvalidDataException("Estrutura de pacote desconhecida. Use os arquivos .vsupd gerados pelo PUBLICAR_UPDATE.bat.");
            byte[] json, sig;
            using (var ms = new MemoryStream()) { using (var s = z.GetEntry("manifest.json")!.Open()) s.CopyTo(ms); json = ms.ToArray(); }
            if (json.Length > 256 * 1024) throw new InvalidDataException("Manifesto grande demais.");
            using (var r = new StreamReader(z.GetEntry("manifest.sig")!.Open()))
            {
                try { sig = Convert.FromBase64String(r.ReadToEnd().Trim()); }
                catch (FormatException) { throw new InvalidDataException("Assinatura do pacote ilegível."); }
            }
            using (var k = ECDsa.Create())
            {
                try { k.ImportSubjectPublicKeyInfo(Convert.FromBase64String(key), out _); }
                catch (Exception) { throw new InvalidDataException("Chave do publicador embutida está inválida."); }
                if (!k.VerifyData(json, sig, HashAlgorithmName.SHA256))
                    throw new InvalidDataException("Assinatura inválida: o pacote foi alterado ou não foi publicado por você. Nada foi instalado.");
            }
            var m = JsonSerializer.Deserialize<ReleaseManifest>(json, IpcJson.Options) ?? throw new InvalidDataException("Manifesto vazio.");
            if (m.Format != FormatName || m.FormatVersion != 1) throw new InvalidDataException("Formato de pacote não suportado por esta versão.");
            if (m.Product != expectedProduct)
                throw new InvalidDataException($"Este pacote é do {ProductLabel(m.Product)}, mas este computador tem o {ProductLabel(expectedProduct)}.");
            if (m.Arch != CurrentArch) throw new InvalidDataException($"Pacote para {m.Arch}; este Windows é {CurrentArch}.");
            if (installedVersion != null)
            {
                var c = AppInfo.CompareVersions(m.Version, installedVersion);
                if (c <= 0 && !allowNotNewer)
                    throw new InvalidDataException(c == 0 ? $"A versão {m.Version} já está instalada." : $"O pacote é da versão {m.Version}, mais antiga que a instalada ({installedVersion}). Downgrade bloqueado.");
                if (AppInfo.CompareVersions(installedVersion, m.MinVersion) < 0)
                    throw new InvalidDataException($"Esta atualização exige a versão {m.MinVersion} ou superior instalada (atual: {installedVersion}).");
            }

            if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true);
            Directory.CreateDirectory(stagingDir);
            var payload = Path.Combine(stagingDir, "payload.zip");
            var entry = z.GetEntry("payload.zip")!;
            if (entry.Length != m.PayloadSize) throw new InvalidDataException("Tamanho do conteúdo não confere com o manifesto.");
            entry.ExtractToFile(payload, true);
            if (!string.Equals(UpdateSigning.FileSha256(payload), m.PayloadSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Integridade do conteúdo não confere (SHA-256). Pacote corrompido.");
            var info = PackageInfo.Read(payload);
            if (info.Product != m.Product || AppInfo.CompareVersions(info.Version, m.Version) != 0)
                throw new InvalidDataException("O conteúdo do pacote não corresponde ao manifesto.");
            SafeZip.Check(payload, new[] { "Service/", "App/" });
            return new ValidatedRelease { Manifest = m, PayloadPath = payload };
        }
    }
}

// ============================================================ GitHub Releases

public sealed record GitHubAsset(string Name, string Url, long Size);
public sealed record GitHubRelease(string Tag, string Version, string Name, string Notes, bool Prerelease, DateTimeOffset? Published, List<GitHubAsset> Assets);

public sealed class GitHubException(string message) : Exception(message);

/// <summary>Consulta Releases públicas (sem token). Não usa git pull nem o ZIP de código-fonte.</summary>
public static class GitHubReleases
{
    static readonly Regex RepoRx = new("^[A-Za-z0-9_.-]{1,100}/[A-Za-z0-9_.-]{1,100}$");
    public static bool ValidRepo(string? r) => r != null && RepoRx.IsMatch(r.Trim()) && !r.Contains("..");

    static HttpClient Client(TimeSpan timeout)
    {
        var h = new HttpClient { Timeout = timeout };
        h.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("VeyonSync-Updater", AppInfo.Version.Split('-')[0]));
        h.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return h;
    }

    static string Explain(Exception e) => e switch
    {
        TaskCanceledException => "Tempo esgotado ao falar com o GitHub. Verifique a internet.",
        HttpRequestException { InnerException: System.Net.Sockets.SocketException } => "Sem conexão com a internet (não foi possível alcançar o GitHub).",
        HttpRequestException h => "Falha de rede ao falar com o GitHub: " + h.Message,
        _ => e.Message
    };

    static async Task<string> CheckResponse(HttpResponseMessage r, string repo, CancellationToken ct)
    {
        if (r.IsSuccessStatusCode) return await r.Content.ReadAsStringAsync(ct);
        if ((r.StatusCode == HttpStatusCode.Forbidden || (int)r.StatusCode == 429)
            && r.Headers.TryGetValues("X-RateLimit-Remaining", out var rem) && rem.FirstOrDefault() == "0")
        {
            var when = "";
            if (r.Headers.TryGetValues("X-RateLimit-Reset", out var reset) && long.TryParse(reset.FirstOrDefault(), out var sec))
                when = " Tente depois de " + DateTimeOffset.FromUnixTimeSeconds(sec).ToLocalTime().ToString("HH:mm") + ".";
            throw new GitHubException("Limite de consultas da API do GitHub atingido para esta rede." + when);
        }
        if (r.StatusCode == HttpStatusCode.NotFound)
            throw new GitHubException($"Nenhuma Release publicada em '{repo}', ou o repositório não existe/é privado.");
        throw new GitHubException($"O GitHub respondeu HTTP {(int)r.StatusCode}.");
    }

    public static async Task<GitHubRelease?> LatestAsync(string repo, bool includePrerelease, CancellationToken ct)
    {
        if (!ValidRepo(repo)) throw new GitHubException("Repositório inválido. Use o formato dono/repositorio.");
        using var h = Client(TimeSpan.FromSeconds(30));
        try
        {
            var url = includePrerelease ? $"https://api.github.com/repos/{repo}/releases?per_page=30" : $"https://api.github.com/repos/{repo}/releases/latest";
            using var r = await h.GetAsync(url, ct);
            var body = await CheckResponse(r, repo, ct);
            using var doc = JsonDocument.Parse(body);
            var list = new List<GitHubRelease>();
            if (doc.RootElement.ValueKind == JsonValueKind.Array) foreach (var x in doc.RootElement.EnumerateArray()) list.Add(Parse(x));
            else list.Add(Parse(doc.RootElement));
            return list.Where(x => !x.Name.StartsWith("\0") && (includePrerelease || !x.Prerelease))
                       .OrderByDescending(x => x.Version, Comparer<string>.Create(AppInfo.CompareVersions)).FirstOrDefault();
        }
        catch (JsonException) { throw new GitHubException("Resposta inesperada do GitHub."); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new GitHubException(Explain(e));
        }
    }

    static GitHubRelease Parse(JsonElement x)
    {
        string S(string p) => x.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        bool B(string p) => x.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.True;
        var assets = new List<GitHubAsset>();
        if (x.TryGetProperty("assets", out var a) && a.ValueKind == JsonValueKind.Array)
            foreach (var y in a.EnumerateArray())
                assets.Add(new GitHubAsset(y.GetProperty("name").GetString() ?? "", y.GetProperty("browser_download_url").GetString() ?? "",
                    y.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0));
        var tag = S("tag_name");
        var draft = B("draft");
        return new GitHubRelease(tag, tag.TrimStart('v', 'V'), draft ? "\0draft" : S("name"), S("body"), B("prerelease"),
            DateTimeOffset.TryParse(S("published_at"), out var d) ? d : null, assets);
    }

    /// <summary>Baixa para .part e só renomeia quando o tamanho confere (download interrompido não vira pacote).</summary>
    public static async Task DownloadAsync(GitHubAsset asset, string dest, Action<double> progress, CancellationToken ct)
    {
        if (!asset.Url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
            throw new GitHubException("Endereço de download inesperado.");
        var part = dest + ".part";
        using var h = Client(TimeSpan.FromMinutes(30));
        try
        {
            using var r = await h.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!r.IsSuccessStatusCode) throw new GitHubException($"Download recusado pelo GitHub (HTTP {(int)r.StatusCode}).");
            var total = r.Content.Headers.ContentLength ?? asset.Size;
            await using (var src = await r.Content.ReadAsStreamAsync(ct))
            await using (var dst = File.Create(part))
            {
                var buf = new byte[81920];
                long got = 0;
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    got += n;
                    if (total > 0) progress(Math.Min(1.0, (double)got / total));
                }
                if (asset.Size > 0 && got != asset.Size)
                    throw new GitHubException($"Download interrompido ({got / 1048576} de {asset.Size / 1048576} MB). Tente de novo.");
            }
            File.Move(part, dest, true);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            try { File.Delete(part); } catch (Exception) { }
            throw new GitHubException(e is IOException ? "Download interrompido (conexão caiu ou disco cheio). Tente de novo." : Explain(e));
        }
    }
}
