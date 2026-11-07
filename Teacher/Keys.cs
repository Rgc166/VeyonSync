using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using VeyonSync.Shared;

namespace VeyonSync.Teacher;

/// <summary>Chave de assinatura das atualizações (ECDSA P-256), guardada com DPAPI LocalMachine na pasta protegida do professor.</summary>
public static class Keys
{
    static string KeyFile => Path.Combine(AppInfo.TeacherData, "update-signing.key");
    static readonly object L = new();

    public static ECDsa UpdateKey()
    {
        lock (L)
        {
            var k = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            if (File.Exists(KeyFile))
            {
                var raw = ProtectedData.Unprotect(File.ReadAllBytes(KeyFile), null, DataProtectionScope.LocalMachine);
                k.ImportPkcs8PrivateKey(raw, out _);
                CryptographicOperations.ZeroMemory(raw);
                return k;
            }
            var pkcs8 = k.ExportPkcs8PrivateKey();
            Directory.CreateDirectory(AppInfo.TeacherData);
            File.WriteAllBytes(KeyFile, ProtectedData.Protect(pkcs8, null, DataProtectionScope.LocalMachine));
            CryptographicOperations.ZeroMemory(pkcs8);
            return k;
        }
    }

    public static string UpdatePublicKey()
    {
        using var k = UpdateKey();
        return Convert.ToBase64String(k.ExportSubjectPublicKeyInfo());
    }

    public static X509Certificate2 TlsCertificate(string thumbprint)
    {
        using var st = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        st.Open(OpenFlags.ReadOnly);
        var found = st.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, false);
        if (found.Count == 0) throw new InvalidOperationException($"Certificado TLS {thumbprint} não encontrado em LocalMachine\\My. Reinstale o professor.");
        return found[0];
    }

    public static string Protect(string s) =>
        Convert.ToBase64String(ProtectedData.Protect(System.Text.Encoding.UTF8.GetBytes(s), null, DataProtectionScope.LocalMachine));

    public static string Unprotect(string s) =>
        System.Text.Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(s), null, DataProtectionScope.LocalMachine));
}
