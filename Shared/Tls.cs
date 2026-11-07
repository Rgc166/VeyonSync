using System.Net.Http;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace VeyonSync.Shared;

/// <summary>
/// Validação TLS restrita ao Veyon Sync. Com certificado fixado (vindo do arquivo de ligação),
/// aceita somente aquele certificado exato, dentro da validade, com uso de servidor e com o
/// nome do endereço presente nos nomes válidos (SAN DNS/IP ou CN). Nunca desativa a validação
/// globalmente e não instala raiz no Windows.
/// </summary>
public static class Tls
{
    public static string Sha256Hex(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    public sealed class Probe
    {
        public string? Failure { get; set; }
    }

    public static HttpClient CreateClient(string? pinnedSha256, Probe? probe = null, TimeSpan? timeout = null)
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(8),
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (sender, cert, chain, errors) =>
                {
                    var host = (sender as SslStream)?.TargetHostName ?? "";
                    var reason = Evaluate(cert, errors, pinnedSha256, host);
                    if (probe != null) probe.Failure = reason;
                    return reason == null;
                }
            }
        };
        return new HttpClient(handler) { Timeout = timeout ?? TimeSpan.FromSeconds(20) };
    }

    /// <summary>Retorna null quando aceito; caso contrário, o motivo em português.</summary>
    public static string? Evaluate(X509Certificate? cert, SslPolicyErrors errors, string? pinnedSha256, string host)
    {
        if (cert == null) return "O professor não apresentou certificado.";
        if (string.IsNullOrEmpty(pinnedSha256))
        {
            // Instalações da Etapa 1 confiam no professor.cer importado no Windows: validação padrão.
            if (errors == SslPolicyErrors.None) return null;
            if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
                return $"O certificado do professor não inclui o nome '{host}'. Nomes válidos: {string.Join(", ", Names(cert))}.";
            return "Certificado do professor não confiável neste PC. Importe o arquivo de ligação (.vsync) atual.";
        }
        using var c2 = new X509Certificate2(cert);
        if (!string.Equals(Sha256Hex(c2.RawData), pinnedSha256, StringComparison.OrdinalIgnoreCase))
            return "O certificado apresentado não é o do professor cadastrado. Se o professor foi reinstalado ou renomeado, gere um novo arquivo de ligação no THC.";
        var now = DateTime.Now;
        if (now < c2.NotBefore || now > c2.NotAfter)
            return $"Certificado do professor fora da validade ({c2.NotBefore:d} a {c2.NotAfter:d}). Confira data e hora deste PC.";
        if (!HasServerAuth(c2)) return "O certificado do professor não permite uso como servidor TLS.";
        if (!MatchesHost(c2, host))
            return $"O endereço usa '{host}', mas o certificado vale para: {string.Join(", ", Names(c2))}. Use um desses nomes ou gere um novo arquivo de ligação.";
        return null;
    }

    public static bool MatchesHost(X509Certificate2 cert, string host)
    {
        if (string.IsNullOrEmpty(host)) return false;
        try { return cert.MatchesHostname(host, allowWildcards: true, allowCommonName: true); }
        catch (ArgumentException) { return false; }
    }

    static bool HasServerAuth(X509Certificate2 c)
    {
        var eku = c.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
        if (eku == null) return true; // sem EKU = qualquer uso
        foreach (var o in eku.EnhancedKeyUsages) if (o.Value == "1.3.6.1.5.5.7.3.1") return true;
        return false;
    }

    public static IReadOnlyList<string> Names(X509Certificate cert)
    {
        using var c = new X509Certificate2(cert);
        var list = new List<string>();
        var san = c.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
        if (san != null)
        {
            list.AddRange(san.EnumerateDnsNames());
            list.AddRange(san.EnumerateIPAddresses().Select(a => a.ToString()));
        }
        var cn = c.GetNameInfo(X509NameType.SimpleName, false);
        if (!string.IsNullOrEmpty(cn) && !list.Contains(cn, StringComparer.OrdinalIgnoreCase)) list.Add(cn + " (CN)");
        return list;
    }

    /// <summary>Converte falhas de rede/TLS em mensagem compreensível.</summary>
    public static string Explain(Exception e, Probe? probe)
    {
        if (probe?.Failure != null) return probe.Failure;
        for (var x = e; x != null; x = x.InnerException)
        {
            if (x is AuthenticationException) return "Falha TLS: " + x.Message;
            if (x is System.Net.Sockets.SocketException se)
                return se.SocketErrorCode switch
                {
                    System.Net.Sockets.SocketError.HostNotFound or System.Net.Sockets.SocketError.NoData
                        => "O nome do professor não foi encontrado na rede (DNS).",
                    System.Net.Sockets.SocketError.ConnectionRefused
                        => "O professor recusou a conexão: serviço parado ou porta errada.",
                    System.Net.Sockets.SocketError.TimedOut or System.Net.Sockets.SocketError.HostUnreachable
                        => "Professor inacessível: desligado, rede ou firewall.",
                    _ => "Erro de rede: " + se.Message
                };
            if (x is TaskCanceledException) return "Tempo esgotado ao falar com o professor.";
        }
        return e.Message;
    }
}
