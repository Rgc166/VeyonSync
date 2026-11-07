using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VeyonSync.Shared;

/// <summary>
/// Autenticidade das atualizações: o professor assina o manifesto (ECDSA P-256/SHA-256) com uma chave
/// privada protegida por DPAPI. O aluno confere com a chave pública recebida no arquivo de ligação/pareamento
/// e confere o SHA-256 do pacote descrito no manifesto. Hash sozinho não prova origem; a assinatura sim.
/// </summary>
public static class UpdateSigning
{
    public static string ManifestJson(UpdateManifest m) => JsonSerializer.Serialize(m, IpcJson.Options);

    public static string Sign(string manifestJson, ECDsa key) =>
        Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(manifestJson), HashAlgorithmName.SHA256));

    /// <summary>Confere a assinatura e só então interpreta o manifesto.</summary>
    public static UpdateManifest? Verify(string manifestJson, string signature, string publicKeySpki)
    {
        try
        {
            using var k = ECDsa.Create();
            k.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeySpki), out _);
            if (!k.VerifyData(Encoding.UTF8.GetBytes(manifestJson), Convert.FromBase64String(signature), HashAlgorithmName.SHA256))
                return null;
            return JsonSerializer.Deserialize<UpdateManifest>(manifestJson, IpcJson.Options);
        }
        catch (Exception) { return null; }
    }

    public static string FileSha256(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s));
    }
}

/// <summary>Pacote de atualização do STD: ZIP com update.json e a pasta Student/ (e opcionalmente App/).</summary>
public sealed class PackageInfo
{
    public string Product { get; set; } = "";
    public string Version { get; set; } = "";
    public string MinVersion { get; set; } = "0.0.0";

    public static PackageInfo Read(string zipPath)
    {
        using var z = ZipFile.OpenRead(zipPath);
        var e = z.GetEntry("update.json") ?? throw new InvalidDataException("Pacote sem update.json.");
        using var s = e.Open();
        var info = JsonSerializer.Deserialize<PackageInfo>(s, IpcJson.Options) ?? throw new InvalidDataException("update.json vazio.");
        if (info.Product is not ("STD" or "THC")) throw new InvalidDataException("Produto do pacote desconhecido.");
        if (!System.Version.TryParse(info.Version, out _)) throw new InvalidDataException("Versão do pacote inválida.");
        return info;
    }
}

public static class SafeZip
{
    /// <summary>Confere os caminhos sem extrair nada (mesmas regras de <see cref="ExtractAllowed"/>).</summary>
    public static void Check(string zipPath, IReadOnlyList<string> allowedPrefixes, long maxTotalBytes = 1_500_000_000)
    {
        long total = 0;
        using var z = ZipFile.OpenRead(zipPath);
        foreach (var e in z.Entries)
        {
            var name = e.FullName.Replace('\\', '/');
            if (name.EndsWith('/') || name == "update.json") continue;
            if (name.StartsWith('/') || name.Contains("..") || name.Contains(':'))
                throw new InvalidDataException("Pacote recusado: caminho inválido " + name);
            if (!allowedPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Pacote recusado: arquivo fora das pastas permitidas: " + name);
            total += e.Length;
            if (total > maxTotalBytes) throw new InvalidDataException("Pacote recusado: tamanho excessivo.");
        }
    }

    /// <summary>
    /// Extrai somente entradas sob os prefixos permitidos, recusando caminhos absolutos, ".." e destinos fora da pasta.
    /// Nada do pacote é executado na extração.
    /// </summary>
    public static int ExtractAllowed(string zipPath, string destination, IReadOnlyList<string> allowedPrefixes, long maxTotalBytes = 1_500_000_000)
    {
        var root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);
        long total = 0;
        int count = 0;
        using var z = ZipFile.OpenRead(zipPath);
        foreach (var e in z.Entries)
        {
            var name = e.FullName.Replace('\\', '/');
            if (name.EndsWith('/')) continue;
            if (name == "update.json") continue;
            if (name.StartsWith('/') || name.Contains("..") || name.Contains(':'))
                throw new InvalidDataException("Pacote recusado: caminho inválido " + name);
            if (!allowedPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Pacote recusado: arquivo fora das pastas permitidas: " + name);
            var target = Path.GetFullPath(Path.Combine(root, name));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Pacote recusado: destino fora da pasta.");
            total += e.Length;
            if (total > maxTotalBytes) throw new InvalidDataException("Pacote recusado: tamanho excessivo.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            e.ExtractToFile(target, overwrite: true);
            count++;
        }
        return count;
    }
}
