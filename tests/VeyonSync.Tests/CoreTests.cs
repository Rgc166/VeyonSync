using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Data.Sqlite;
using VeyonSync.Shared;
using VeyonSync.Teacher;
using VeyonSync.Teacher.Veyon;
using Xunit;

namespace VeyonSync.Tests;

public class LinkFileTests
{
    static X509Certificate2 Cert(string name)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN={name}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder(); san.AddDnsName(name);
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
        return req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(1));
    }

    static string UpdKey() { using var k = ECDsa.Create(ECCurve.NamedCurves.nistP256); return Convert.ToBase64String(k.ExportSubjectPublicKeyInfo()); }

    [Fact]
    public void RoundTrip_PreservesLabTeacherAndCertificate()
    {
        using var c = Cert("PROFESSOR-LAB-B");
        var l = LinkFile.Create(Guid.NewGuid().ToString(), "PROFESSOR-LAB-B", "B", "Laboratório B", "https://PROFESSOR-LAB-B:5443", c, UpdKey());
        var p = LinkFile.Parse(l.Serialize());
        Assert.Equal("B", p.LabId);
        Assert.Equal("https://PROFESSOR-LAB-B:5443", p.Url);
        Assert.Equal(Tls.Sha256Hex(c.RawData), p.CertificateSha256);
        Assert.DoesNotContain("PRIVATE", l.Serialize(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TamperedCertificate_IsRejected()
    {
        using var a = Cert("PROFESSOR-LAB-B");
        using var b = Cert("OUTRO");
        var l = LinkFile.Create(Guid.NewGuid().ToString(), "PROFESSOR-LAB-B", "B", "Lab B", "https://PROFESSOR-LAB-B:5443", a, "");
        l.CertificateDer = Convert.ToBase64String(b.RawData);
        Assert.Throws<InvalidDataException>(() => LinkFile.Parse(l.Serialize()));
    }

    [Theory]
    [InlineData("http://x:5443")]
    [InlineData("https://x:5443/api?x=1")]
    [InlineData("nada")]
    public void InvalidUrl_IsRejected(string url)
    {
        using var a = Cert("x");
        var l = LinkFile.Create(Guid.NewGuid().ToString(), "x", "B", "Lab B", "https://x:5443", a, "");
        l.Url = url;
        Assert.Throws<InvalidDataException>(() => LinkFile.Parse(l.Serialize()));
    }

    [Fact]
    public void HostValidation_UsesSanNotSubjectText()
    {
        using var c = Cert("PROFESSOR-LAB-A");
        Assert.True(Tls.MatchesHost(c, "PROFESSOR-LAB-A"));
        Assert.False(Tls.MatchesHost(c, "PROFESSOR-LAB-B")); // PC renomeado: certificado antigo não vale
        Assert.NotNull(Tls.Evaluate(c, System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors, Tls.Sha256Hex(c.RawData), "PROFESSOR-LAB-B"));
        Assert.Null(Tls.Evaluate(c, System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors, Tls.Sha256Hex(c.RawData), "PROFESSOR-LAB-A"));
        using var other = Cert("PROFESSOR-LAB-A");
        Assert.NotNull(Tls.Evaluate(other, System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors, Tls.Sha256Hex(c.RawData), "PROFESSOR-LAB-A"));
    }
}

public class NamingTests
{
    [Theory]
    [InlineData("3", "PC03")]
    [InlineData("pc3", "PC03")]
    [InlineData("PC 12", "PC12")]
    public void NormalizePosition(string input, string expected) => Assert.Equal(expected, Naming.NormalizePosition(input));

    [Fact]
    public void FriendlyPatterns()
    {
        Assert.Equal("PC 01", Naming.Friendly(Naming.Pc, "B", "PC01", "DESKTOP-X1"));
        Assert.Equal("LAB-B-PC01", Naming.Friendly(Naming.LabPc, "b", "1", "DESKTOP-X1"));
        Assert.Equal("DESKTOP-X1", Naming.Friendly(Naming.WindowsName, "B", "PC01", "DESKTOP-X1"));
        Assert.Equal("DESKTOP-X1", Naming.Friendly(Naming.Pc, "B", "", "DESKTOP-X1"));
        Assert.Equal("ALUNO 07", Naming.Friendly(Naming.Aluno, "B", "PC07", "DESKTOP-X1"));
    }
}

public class SigningTests
{
    [Fact]
    public void Signature_DetectsTampering()
    {
        using var k = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pub = Convert.ToBase64String(k.ExportSubjectPublicKeyInfo());
        var json = UpdateSigning.ManifestJson(new UpdateManifest("STD", "2.0.1", "2.0.0", "AB", 10, "x.zip", DateTimeOffset.UtcNow, Guid.NewGuid().ToString()));
        var sig = UpdateSigning.Sign(json, k);
        Assert.NotNull(UpdateSigning.Verify(json, sig, pub));
        Assert.Null(UpdateSigning.Verify(json.Replace("2.0.1", "9.9.9"), sig, pub));
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Null(UpdateSigning.Verify(json, sig, Convert.ToBase64String(other.ExportSubjectPublicKeyInfo())));
    }

    [Fact]
    public void SafeZip_RejectsTraversalAndUnknownFolders()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var zip = Path.Combine(dir, "p.zip");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create)) { z.CreateEntry("Service/../../evil.txt"); }
        Assert.Throws<InvalidDataException>(() => SafeZip.ExtractAllowed(zip, Path.Combine(dir, "out"), new[] { "Service/", "App/" }));
        File.Delete(zip);
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create)) { z.CreateEntry("Windows/x.dll"); }
        Assert.Throws<InvalidDataException>(() => SafeZip.ExtractAllowed(zip, Path.Combine(dir, "out"), new[] { "Service/", "App/" }));
    }
}

public class ExporterTests
{
    static ReportTable T() => new() { Title = "Histórico — ação", Headers = { "A", "B" }, Rows = { new() { "ção (1)", "x\\y" } } };

    [Fact]
    public void Pdf_HasHeaderAndEof()
    {
        var f = Path.GetTempFileName() + ".pdf";
        Exporters.Pdf(T(), f);
        var b = File.ReadAllBytes(f);
        Assert.StartsWith("%PDF-1.4", System.Text.Encoding.ASCII.GetString(b, 0, 8));
        Assert.Contains("%%EOF", System.Text.Encoding.ASCII.GetString(b));
    }

    [Fact]
    public void Docx_IsZipWithDocument()
    {
        var f = Path.GetTempFileName() + ".docx";
        Exporters.Docx(T(), f);
        using var z = ZipFile.OpenRead(f);
        Assert.NotNull(z.GetEntry("word/document.xml"));
        Assert.NotNull(z.GetEntry("[Content_Types].xml"));
    }
}

public class VeyonCompareTests
{
    static MachineRow M(string id, string pos, string win, string ip, string mac) =>
        new() { AgentId = id, Lab = "B", Position = pos, WindowsName = win, Ip = ip, Mac = mac, DisplayName = Naming.Friendly(Naming.LabPc, "B", pos, win) };

    static readonly TeacherSettings S = new() { LabName = "Laboratório B", AddressMode = "ip", NameTemplate = Naming.LabPc };

    [Fact]
    public void ParsesExport()
    {
        var o = VeyonCli.Parse("Location;;Laboratório B;;\r\nComputer;Laboratório B;LAB-B-PC01;10.0.0.5;AA:BB:CC:DD:EE:01\r\n");
        Assert.Equal(2, o.Count);
        Assert.False(o[0].IsComputer);
        Assert.True(o[1].IsComputer);
        Assert.Equal("10.0.0.5", o[1].Host);
    }

    [Fact]
    public void NewMachine_IsAdded_AndIdentical_IsAlreadyUpdated()
    {
        var w = VeyonEngine.DesiredRecords(new[] { M("a", "PC01", "D1", "10.0.0.5", "AA-BB-CC-DD-EE-01") }, S);
        var p = VeyonEngine.Compare(new(), w, new());
        Assert.Equal("Adicionar", p.Changes[0].Action);
        var cur = new List<VeyonObject> { new() { IsComputer = true, Location = "Laboratório B", Name = "LAB-B-PC01", Host = "10.0.0.5", Mac = "AA:BB:CC:DD:EE:01" } };
        Assert.Equal("Já atualizado", VeyonEngine.Compare(cur, w, new()).Changes[0].Action);
    }

    [Fact]
    public void IpChange_UpdatesManagedRecord_WithoutDuplicating()
    {
        var w = VeyonEngine.DesiredRecords(new[] { M("a", "PC01", "D1", "10.0.0.9", "AA-BB-CC-DD-EE-01") }, S);
        var cur = new List<VeyonObject> { new() { IsComputer = true, Location = "Laboratório B", Name = "LAB-B-PC01", Host = "10.0.0.5", Mac = "AA:BB:CC:DD:EE:01" } };
        var managed = new Dictionary<string, Db.ManagedRecord> { ["a"] = new("a", "B", "LAB-B-PC01", "10.0.0.5", "AA:BB:CC:DD:EE:01", "Laboratório B") };
        var c = VeyonEngine.Compare(cur, w, managed).Changes[0];
        Assert.Equal("Atualizar", c.Action);
        Assert.Equal("10.0.0.5", c.OldHost);
        Assert.Equal("10.0.0.9", c.NewHost);
    }

    [Fact]
    public void StrangeNameRecord_IsAssociatedByMac_AndRenamed()
    {
        var w = VeyonEngine.DesiredRecords(new[] { M("a", "PC01", "D1", "10.0.0.5", "AA-BB-CC-DD-EE-01") }, S);
        var cur = new List<VeyonObject> { new() { IsComputer = true, Location = "Laboratório B", Name = "3f2a9c1e-uuid", Host = "10.0.0.5", Mac = "aa:bb:cc:dd:ee:01" } };
        var c = VeyonEngine.Compare(cur, w, new()).Changes[0];
        Assert.Equal("Atualizar", c.Action);
        Assert.Equal("3f2a9c1e-uuid", c.OldName);
        Assert.Equal("LAB-B-PC01", c.NewName);
    }

    [Fact]
    public void AmbiguousRecords_AreConflicts()
    {
        var w = VeyonEngine.DesiredRecords(new[] { M("a", "PC01", "D1", "10.0.0.5", "AA-BB-CC-DD-EE-01") }, S);
        var two = new List<VeyonObject>
        {
            new() { IsComputer = true, Location = "X", Name = "A", Host = "1", Mac = "AA:BB:CC:DD:EE:01" },
            new() { IsComputer = true, Location = "Y", Name = "B", Host = "2", Mac = "AA:BB:CC:DD:EE:01" }
        };
        Assert.Equal("Conflito", VeyonEngine.Compare(two, w, new()).Changes[0].Action);
        var sameIpOtherMac = new List<VeyonObject> { new() { IsComputer = true, Location = "Laboratório B", Name = "PC velho", Host = "10.0.0.5", Mac = "11:22:33:44:55:66" } };
        Assert.Equal("Conflito", VeyonEngine.Compare(sameIpOtherMac, w, new()).Changes[0].Action);
    }

    [Fact]
    public void DuplicatePosition_IsConflict()
    {
        var w = VeyonEngine.DesiredRecords(new[] { M("a", "PC01", "D1", "10.0.0.5", "AA-BB-CC-DD-EE-01"), M("b", "PC01", "D2", "10.0.0.6", "AA-BB-CC-DD-EE-02") }, S);
        Assert.All(VeyonEngine.Compare(new(), w, new()).Changes, c => Assert.Equal("Conflito", c.Action));
    }
}

public class DbTests
{
    static string Etapa1Db()
    {
        var f = Path.Combine(Directory.CreateTempSubdirectory().FullName, "teacher.db");
        using var c = new SqliteConnection($"Data Source={f}");
        c.Open();
        foreach (var s in new[]
        {
            "CREATE TABLE machines(agent_id TEXT PRIMARY KEY, lab TEXT, name TEXT, position TEXT, user TEXT, ip TEXT, mac TEXT, last_seen TEXT, last_seq INTEGER, version TEXT)",
            "CREATE TABLE tokens(agent_id TEXT PRIMARY KEY, hash TEXT, lab TEXT)",
            "CREATE TABLE codes(hash TEXT PRIMARY KEY, expires TEXT)",
            "CREATE TABLE history(id INTEGER PRIMARY KEY AUTOINCREMENT, agent_id TEXT, ts TEXT, kind TEXT, detail TEXT)",
            "CREATE TABLE veyon_queue(agent_id TEXT PRIMARY KEY, status TEXT, created TEXT)",
            "CREATE TABLE collect_requests(agent_id TEXT PRIMARY KEY)",
            $"INSERT INTO machines VALUES('{Guid.Empty}','B','DESKTOP-1','PC01',NULL,'10.0.0.5','AA-BB-CC-DD-EE-01','{DateTimeOffset.UtcNow:o}',100,'0.1.0')",
            $"INSERT INTO history(agent_id,ts,kind,detail) VALUES('{Guid.Empty}','{DateTimeOffset.UtcNow:o}','Primeiro cadastro','x')"
        })
        { using var m = c.CreateCommand(); m.CommandText = s; m.ExecuteNonQuery(); }
        c.Close();
        SqliteConnection.ClearAllPools();
        return f;
    }

    static ReportDto R(string id, long seq, string ip, string pos = "PC01") =>
        new(id, "DESKTOP-1", "B", pos, null, ip, "AA-BB-CC-DD-EE-01", DateTimeOffset.UtcNow, seq, "2.0.0", "teste");

    [Fact]
    public void Etapa1Database_IsMigrated_WithoutDataLoss()
    {
        var f = Etapa1Db();
        var db = new Db(f);
        var m = Assert.Single(db.Machines(Naming.WindowsName, 180));
        Assert.Equal("10.0.0.5", m.Ip);
        Assert.Contains(db.History(new HistoryQuery()), h => h.Kind == "Primeiro cadastro");
        Assert.True(Directory.GetFiles(Path.GetDirectoryName(f)!, "teacher.db.antes-v2-*.bak").Length == 1);
        new Db(f); // segunda abertura não migra de novo
        Assert.True(Directory.GetFiles(Path.GetDirectoryName(f)!, "teacher.db.antes-v2-*.bak").Length == 1);
    }

    [Fact]
    public void IpChange_DoesNotDuplicate_AndStaleIsIgnored()
    {
        var db = new Db(Etapa1Db());
        var id = Guid.Empty.ToString();
        db.Upsert(R(id, 200, "10.0.0.9"));
        var stale = db.Upsert(R(id, 150, "10.0.0.1"));
        Assert.True(stale.Stale);
        var m = Assert.Single(db.Machines(Naming.WindowsName, 180));
        Assert.Equal("10.0.0.9", m.Ip);
        Assert.Contains(db.History(new HistoryQuery()), h => h.Kind == "Mudança de IP" && h.Detail.Contains("10.0.0.9"));
    }

    [Fact]
    public void Pairing_CodeSingleUse_AndPositionCollision()
    {
        var db = new Db(Etapa1Db());
        var code = db.NewPairingCode();
        Assert.Equal(401, db.Pair("00000000", Guid.NewGuid().ToString(), "B", "PC05", "X").Status);
        var other = Guid.NewGuid().ToString();
        var col = db.Pair(code, other, "B", "1", "OUTRO"); // PC01 já é de DESKTOP-1
        Assert.Equal(409, col.Status);
        var ok = db.Pair(code, other, "B", "PC02", "OUTRO");
        Assert.NotNull(ok.Token);
        Assert.Equal(401, db.Pair(code, Guid.NewGuid().ToString(), "B", "PC03", "Y").Status); // já usado
        Assert.True(db.CheckToken(other, ok.Token!, "B"));
        Assert.False(db.CheckToken(other, ok.Token!, "A"));
        var newer = db.NewPairingCode();
        Assert.NotEqual(code, newer);
    }

    [Fact]
    public void MapRejectsDuplicates()
    {
        var db = new Db(Etapa1Db());
        var map = new LabMap { Lab = "B", Rows = { new MapRow { Name = "F1", Slots = { "x", null, "x" } } } };
        Assert.Throws<InvalidDataException>(() => db.SaveMap(map));
        map.Rows[0].Slots[2] = null;
        db.SaveMap(map);
        Assert.Equal("x", db.GetMap("B").Rows[0].Slots[0]);
    }
}

public class LegacyTableTests
{
    [Fact]
    public void ConflictingSettingsTable_IsPreservedAndReplaced()
    {
        var f = Path.Combine(Directory.CreateTempSubdirectory().FullName, "teacher.db");
        using (var c = new SqliteConnection($"Data Source={f}"))
        {
            c.Open();
            using var m = c.CreateCommand();
            m.CommandText = "CREATE TABLE settings(nome TEXT, valor TEXT); INSERT INTO settings VALUES('a','b');";
            m.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        var db = new Db(f);
        db.SetSetting("x", "1");
        Assert.Equal("1", db.GetSetting("x"));
        using var c2 = new SqliteConnection($"Data Source={f}");
        c2.Open();
        using var q = c2.CreateCommand();
        q.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name LIKE 'legado_settings_%'";
        Assert.Equal(1L, (long)q.ExecuteScalar()!);
    }
}

public class VeyonListTests
{
    [Fact]
    public void ExistingAlunoRecord_WithDashMac_IsAlreadyUpdated()
    {
        var s = new TeacherSettings { LabName = "LAB B", AddressMode = "ip", NameTemplate = Naming.Aluno };
        var m = new MachineRow { AgentId = "a", Lab = "B", Position = "PC02", WindowsName = "D", Ip = "192.168.15.65", Mac = "D8-5E-D3-F1-D0-3E",
            DisplayName = Naming.Friendly(Naming.Aluno, "B", "PC02", "D") };
        var cur = new List<VeyonObject> { new() { IsComputer = true, Location = "LAB B", Name = "ALUNO 02", Host = "192.168.15.65", Mac = "D8-5E-D3-F1-D0-3E" } };
        Assert.Equal("Já atualizado", VeyonEngine.Compare(cur, VeyonEngine.DesiredRecords(new[] { m }, s), new()).Changes[0].Action);
    }
}

public class ReleasePackageTests
{
    static (ECDsa key, string pub) Key()
    {
        var k = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (k, Convert.ToBase64String(k.ExportSubjectPublicKeyInfo()));
    }

    static string Payload(string dir, string product, string version, string extraEntry = "")
    {
        var p = Path.Combine(dir, $"payload-{Guid.NewGuid():N}.zip"); // nome único: um teste pode montar vários pacotes
        using var z = ZipFile.Open(p, ZipArchiveMode.Create);
        using (var w = new StreamWriter(z.CreateEntry("update.json").Open())) w.Write($"{{\"product\":\"{product}\",\"version\":\"{version}\",\"minVersion\":\"2.0.0\"}}");
        using (var w = new StreamWriter(z.CreateEntry("Service/VeyonSync.Student.exe").Open())) w.Write("exe");
        using (var w = new StreamWriter(z.CreateEntry("App/VeyonSync.Student.App.exe").Open())) w.Write("app");
        if (extraEntry != "") z.CreateEntry(extraEntry);
        return p;
    }

    static string Pkg(string dir, ECDsa key, string product = "STD", string version = "2.2.0", string extra = "")
    {
        var pay = Payload(dir, product, version, extra);
        var outF = Path.Combine(dir, $"p-{Guid.NewGuid():N}.vsupd");
        ReleasePackage.Create(pay, new ReleaseManifest { Product = product, Version = version, Notes = "notas" }, key, outF);
        return outF;
    }

    static string Dir() => Directory.CreateTempSubdirectory().FullName;

    [Fact]
    public void ValidPackage_IsAccepted()
    {
        var d = Dir(); var (k, pub) = Key();
        var v = ReleasePackage.Validate(Pkg(d, k), "STD", "2.1.0", Path.Combine(d, "st"), pub);
        Assert.Equal("2.2.0", v.Manifest.Version);
        Assert.True(File.Exists(v.PayloadPath));
    }

    [Fact]
    public void WrongProduct_IsRejected()
    {
        var d = Dir(); var (k, pub) = Key();
        var e = Assert.Throws<InvalidDataException>(() => ReleasePackage.Validate(Pkg(d, k, "THC"), "STD", "2.1.0", Path.Combine(d, "st"), pub));
        Assert.Contains("THC", e.Message);
    }

    [Fact]
    public void OtherPublisherKey_IsRejected()
    {
        var d = Dir(); var (k, _) = Key(); var (_, otherPub) = Key();
        Assert.Throws<InvalidDataException>(() => ReleasePackage.Validate(Pkg(d, k), "STD", "2.1.0", Path.Combine(d, "st"), otherPub));
    }

    [Fact]
    public void TamperedPayload_IsRejected()
    {
        var d = Dir(); var (k, pub) = Key();
        var f = Pkg(d, k);
        using (var z = ZipFile.Open(f, ZipArchiveMode.Update))
        {
            z.GetEntry("payload.zip")!.Delete();
            var other = Payload(Directory.CreateTempSubdirectory().FullName, "STD", "2.2.0", "Service/extra.dll");
            z.CreateEntryFromFile(other, "payload.zip");
        }
        Assert.Throws<InvalidDataException>(() => ReleasePackage.Validate(f, "STD", "2.1.0", Path.Combine(d, "st"), pub));
    }

    [Fact]
    public void InterruptedDownload_IsRejected()
    {
        var d = Dir(); var (k, pub) = Key();
        var f = Pkg(d, k);
        var bytes = File.ReadAllBytes(f);
        File.WriteAllBytes(f, bytes[..(bytes.Length / 2)]);
        Assert.Throws<InvalidDataException>(() => ReleasePackage.Validate(f, "STD", "2.1.0", Path.Combine(d, "st"), pub));
    }

    [Fact]
    public void DowngradeAndSameVersion_AreBlocked()
    {
        var d = Dir(); var (k, pub) = Key();
        Assert.Throws<InvalidDataException>(() => ReleasePackage.Validate(Pkg(d, k, version: "2.0.5"), "STD", "2.1.0", Path.Combine(d, "a"), pub));
        Assert.Throws<InvalidDataException>(() => ReleasePackage.Validate(Pkg(d, k, version: "2.1.0"), "STD", "2.1.0", Path.Combine(d, "b"), pub));
    }

    [Fact]
    public void PathTraversalInsidePayload_IsRejected()
    {
        var d = Dir(); var (k, pub) = Key();
        Assert.Throws<InvalidDataException>(() => ReleasePackage.Validate(Pkg(d, k, extra: "Service/../../evil.dll"), "STD", "2.1.0", Path.Combine(d, "st"), pub));
    }

    [Theory]
    [InlineData("2.1.0", "2.0.9", 1)]
    [InlineData("2.1.0-beta.1", "2.1.0", -1)]
    [InlineData("v2.1.0", "2.1.0", 0)]
    [InlineData("2.10.0", "2.9.0", 1)]
    public void VersionComparison(string a, string b, int sign) => Assert.Equal(sign, Math.Sign(AppInfo.CompareVersions(a, b)));
}

/// <summary>2.2: ZIP de entrega (VeyonSync-THC-Update / VeyonSync-STD-Update) com exatamente um .vsupd.</summary>
public class UpdateZipTests
{
    static (ECDsa key, string pub) Key()
    {
        var k = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (k, Convert.ToBase64String(k.ExportSubjectPublicKeyInfo()));
    }

    static string Vsupd(string dir, ECDsa key, string product, string version)
    {
        var pay = Path.Combine(dir, $"payload-{Guid.NewGuid():N}.zip");
        using (var z = ZipFile.Open(pay, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(z.CreateEntry("update.json").Open())) w.Write($"{{\"product\":\"{product}\",\"version\":\"{version}\",\"minVersion\":\"2.0.0\"}}");
            using (var w = new StreamWriter(z.CreateEntry("Service/x.exe").Open())) w.Write("exe");
            using (var w = new StreamWriter(z.CreateEntry("App/y.exe").Open())) w.Write("app");
        }
        var f = Path.Combine(dir, $"VeyonSync-{product}-{version}-win-x64.vsupd");
        ReleasePackage.Create(pay, new ReleaseManifest { Product = product, Version = version }, key, f);
        return f;
    }

    static string Wrap(string dir, string name, params string[] files)
    {
        var zip = Path.Combine(dir, name);
        using var z = ZipFile.Open(zip, ZipArchiveMode.Create);
        foreach (var f in files) z.CreateEntryFromFile(f, Path.GetFileName(f));
        using (var w = new StreamWriter(z.CreateEntry("LEIA-ME.txt").Open())) w.Write("instruções");
        return zip;
    }

    static string Dir() => Directory.CreateTempSubdirectory().FullName;

    [Fact]
    public void WrapperZip_IsUnwrapped_AndValidated()
    {
        var d = Dir(); var (k, pub) = Key();
        var zip = Wrap(d, "VeyonSync-STD-Update-2.2.0.zip", Vsupd(d, k, "STD", "2.2.0"));
        var inner = ReleasePackage.ResolveUpdateFile(zip, Path.Combine(d, "w"));
        Assert.EndsWith(".vsupd", inner);
        var v = ReleasePackage.Validate(inner, "STD", "2.1.0", Path.Combine(d, "st"), pub);
        Assert.Equal("2.2.0", v.Manifest.Version);
    }

    [Fact]
    public void WrongEdition_InsideZip_IsRejected()
    {
        var d = Dir(); var (k, pub) = Key();
        var zip = Wrap(d, "VeyonSync-THC-Update-2.2.0.zip", Vsupd(d, k, "THC", "2.2.0"));
        var inner = ReleasePackage.ResolveUpdateFile(zip, Path.Combine(d, "w"));
        var e = Assert.Throws<InvalidDataException>(() => ReleasePackage.Validate(inner, "STD", "2.1.0", Path.Combine(d, "st"), pub));
        Assert.Contains("THC", e.Message);
    }

    [Fact]
    public void SourceZip_And_TwoPackages_AreRejected()
    {
        var d = Dir(); var (k, _) = Key();
        var src = Path.Combine(d, "fonte.zip");
        using (var z = ZipFile.Open(src, ZipArchiveMode.Create))
        using (var w = new StreamWriter(z.CreateEntry("Teacher/Program.cs").Open())) w.Write("class X {}");
        Assert.Throws<InvalidDataException>(() => ReleasePackage.ResolveUpdateFile(src, Path.Combine(d, "a")));
        var two = Wrap(d, "dois.zip", Vsupd(d, k, "THC", "2.2.0"), Vsupd(d, k, "STD", "2.2.0"));
        Assert.Throws<InvalidDataException>(() => ReleasePackage.ResolveUpdateFile(two, Path.Combine(d, "b")));
    }

    [Fact]
    public void RenamedVsupd_IsAccepted()
    {
        var d = Dir(); var (k, pub) = Key();
        var f = Vsupd(d, k, "STD", "2.2.0");
        var renamed = Path.ChangeExtension(f, ".zip");
        File.Move(f, renamed);
        Assert.Equal(renamed, ReleasePackage.ResolveUpdateFile(renamed, Path.Combine(d, "w")));
        Assert.Equal("2.2.0", ReleasePackage.Validate(renamed, "STD", "2.1.0", Path.Combine(d, "st"), pub).Manifest.Version);
    }
}

/// <summary>2.2: a resposta à coleta é correlacionada ao pedido; sinais comuns não contam como resposta.</summary>
public class CollectRoundTests
{
    static string NewDb()
    {
        var f = Path.Combine(Directory.CreateTempSubdirectory().FullName, "teacher.db");
        return f;
    }

    static ReportDto R(string id, long seq, string reason, bool auto = true) =>
        new(id, "DESKTOP-1", "B", "PC01", null, "10.0.0.5", "AA-BB-CC-DD-EE-01", DateTimeOffset.UtcNow, seq, "2.2.0", reason, reason == ReportReasons.Presence, auto);

    [Fact]
    public void PresenceOnlyDeliversRequest_CollectReasonAnswers()
    {
        var db = new Db(NewDb());
        var id = Guid.NewGuid().ToString();
        db.Upsert(R(id, 1, ReportReasons.Start));
        db.RequestCollect(null);
        var (r0, _) = db.LastCollect(180);
        Assert.Equal(1, r0!.Total);
        Assert.Equal(1, r0.Waiting);

        var ack = db.Upsert(R(id, 2, ReportReasons.Presence));
        Assert.True(ack.CollectRequested);
        var (r1, per1) = db.LastCollect(180);
        Assert.Equal(0, r1!.Answered); // presença não é resposta
        Assert.Equal(CollectStates.Waiting, per1[id]);

        db.Upsert(R(id, 3, ReportReasons.Collect));
        var (r2, per2) = db.LastCollect(180);
        Assert.Equal(1, r2!.Answered);
        Assert.True(r2.AllAnswered);
        Assert.Equal(CollectStates.Answered, per2[id]);
    }

    [Fact]
    public void LegacyAgent_ManualAfterSignal_Answers()
    {
        var db = new Db(NewDb());
        var id = Guid.NewGuid().ToString();
        db.Upsert(R(id, 1, ReportReasons.Start));
        db.RequestCollect(new[] { id });
        db.Upsert(R(id, 2, ReportReasons.Presence));      // entrega o pedido (agente 2.1)
        db.Upsert(R(id, 3, ReportReasons.Manual));        // 2.1 responde com motivo "manual"
        Assert.Equal(1, db.LastCollect(180).round!.Answered);
    }

    [Fact]
    public void ManualModeAgent_IsCountedSeparately()
    {
        var db = new Db(NewDb());
        var id = Guid.NewGuid().ToString();
        db.Upsert(R(id, 1, ReportReasons.Manual, auto: false));
        db.RequestCollect(null);
        var (r, per) = db.LastCollect(180);
        Assert.Equal(1, r!.Manual);
        Assert.Equal(CollectStates.Manual, per[id]);
        Assert.False(r.AllAnswered);
    }
}
