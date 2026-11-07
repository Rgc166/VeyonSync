using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace VeyonSync.Shared;

/// <summary>
/// Arquivo de ligação (.vsync). Contém apenas dados públicos: nunca senha, chave privada,
/// token de agente ou código temporário. É tratado estritamente como dados.
/// </summary>
public sealed class LinkFile
{
    public const string FormatName = "veyonsync-link";
    public const int CurrentVersion = 1;
    public const string Extension = ".vsync";
    const int MaxBytes = 64 * 1024;

    public string Format { get; set; } = FormatName;
    public int FormatVersion { get; set; } = CurrentVersion;
    public string ReceiverId { get; set; } = "";
    public string TeacherName { get; set; } = "";
    public string LabId { get; set; } = "";
    public string LabName { get; set; } = "";
    public string Url { get; set; } = "";
    public string CertificateDer { get; set; } = "";
    public string CertificateSha256 { get; set; } = "";
    public string UpdatePublicKey { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string AppVersion { get; set; } = AppInfo.Version;

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    static readonly Regex LabRx = new("^[A-Za-z0-9_-]{1,32}$");

    public static string SafeFileName(string labId) => $"VeyonSync_Lab{labId}{Extension}";

    public string Serialize() => JsonSerializer.Serialize(this, Json);

    public X509Certificate2 Certificate() => X509CertificateLoader.LoadCertificate(Convert.FromBase64String(CertificateDer));

    public Uri Uri() => new(Url);

    public static LinkFile Create(string receiverId, string teacherName, string labId, string labName, string url,
        X509Certificate2 cert, string updatePublicKey)
    {
        var der = cert.Export(X509ContentType.Cert);
        return new LinkFile
        {
            ReceiverId = receiverId, TeacherName = teacherName, LabId = labId, LabName = labName, Url = url,
            CertificateDer = Convert.ToBase64String(der), CertificateSha256 = Tls.Sha256Hex(der),
            UpdatePublicKey = updatePublicKey
        };
    }

    /// <summary>Lê e valida. Lança <see cref="InvalidDataException"/> com mensagem compreensível.</summary>
    public static LinkFile Parse(string text)
    {
        if (text.Length > MaxBytes) throw new InvalidDataException("Arquivo de ligação grande demais.");
        LinkFile? f;
        try { f = JsonSerializer.Deserialize<LinkFile>(text, Json); }
        catch (JsonException e) { throw new InvalidDataException("Arquivo de ligação inválido (JSON): " + e.Message); }
        if (f == null) throw new InvalidDataException("Arquivo de ligação vazio.");
        f.Validate();
        return f;
    }

    public static LinkFile Load(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new InvalidDataException("Arquivo não encontrado.");
        if (info.Length > MaxBytes) throw new InvalidDataException("Arquivo de ligação grande demais.");
        return Parse(File.ReadAllText(path));
    }

    public void Validate()
    {
        if (Format != FormatName) throw new InvalidDataException("Este arquivo não é um arquivo de ligação do Veyon Sync.");
        if (FormatVersion < 1 || FormatVersion > CurrentVersion)
            throw new InvalidDataException($"Versão de formato {FormatVersion} não suportada por esta versão do STD.");
        if (!Guid.TryParse(ReceiverId, out _)) throw new InvalidDataException("Identidade do professor ausente ou inválida.");
        if (!LabRx.IsMatch(LabId)) throw new InvalidDataException("Identificador de laboratório inválido.");
        if (string.IsNullOrWhiteSpace(TeacherName) || TeacherName.Length > 63) throw new InvalidDataException("Nome do professor inválido.");
        if (!System.Uri.TryCreate(Url, UriKind.Absolute, out var u) || u.Scheme != System.Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(u.Query) || u.AbsolutePath.Trim('/') != "")
            throw new InvalidDataException("Endereço do professor deve ser https://NOME:PORTA.");
        byte[] der;
        try { der = Convert.FromBase64String(CertificateDer); }
        catch (FormatException) { throw new InvalidDataException("Certificado do professor corrompido."); }
        if (!string.Equals(Tls.Sha256Hex(der), CertificateSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Impressão digital do certificado não confere: arquivo alterado ou corrompido.");
        try
        {
            using var c = X509CertificateLoader.LoadCertificate(der);
            if (c.HasPrivateKey) throw new InvalidDataException("O arquivo contém chave privada: recusado.");
        }
        catch (CryptographicException) { throw new InvalidDataException("Certificado do professor ilegível."); }
        if (!string.IsNullOrEmpty(UpdatePublicKey))
        {
            try { using var k = ECDsa.Create(); k.ImportSubjectPublicKeyInfo(Convert.FromBase64String(UpdatePublicKey), out _); }
            catch (Exception) { throw new InvalidDataException("Chave pública de atualização inválida."); }
        }
    }
}
