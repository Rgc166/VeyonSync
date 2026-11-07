using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VeyonSync.Shared;

// Uso:
//   keygen  <pasta-da-chave> <arquivo-chave-publica>
//   pubkey  <pasta-da-chave>
//   pack    --product THC|STD --version X --min Y --notes-file F --service DIR --app DIR --key-dir DIR --out FILE
//   verify  <arquivo.vsupd> --product THC|STD --pub <arquivo-chave-publica>
// A chave privada fica protegida com DPAPI do usuário atual em <pasta-da-chave>\publisher.key (nunca no repositório).

try
{
    if (args.Length == 0) return Fail("Comando ausente (keygen, pubkey, pack, verify).");
    return args[0] switch
    {
        "keygen" => KeyGen(args[1], args[2]),
        "pubkey" => PubKey(args[1]),
        "pack" => Pack(Opts(args)),
        "verify" => Verify(args[1], Opts(args)),
        _ => Fail("Comando desconhecido: " + args[0])
    };
}
catch (Exception e) { return Fail(e.Message); }

static int Fail(string m) { Console.Error.WriteLine("ERRO: " + m); return 1; }

static Dictionary<string, string> Opts(string[] a)
{
    var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (int i = 1; i < a.Length - 1; i++) if (a[i].StartsWith("--")) d[a[i][2..]] = a[i + 1];
    return d;
}

static string Need(Dictionary<string, string> o, string k) => o.TryGetValue(k, out var v) && v.Length > 0 ? v : throw new ArgumentException("Falta --" + k);

static string KeyFile(string dir) => Path.Combine(dir, "publisher.key");

static ECDsa LoadKey(string dir)
{
    var f = KeyFile(dir);
    if (!File.Exists(f)) throw new FileNotFoundException("Chave do publicador não encontrada em " + dir + ". Rode o PUBLICAR_UPDATE.bat para criar.");
    var raw = ProtectedData.Unprotect(File.ReadAllBytes(f), Encoding.UTF8.GetBytes("VeyonSync.Publisher"), DataProtectionScope.CurrentUser);
    var k = ECDsa.Create();
    k.ImportPkcs8PrivateKey(raw, out _);
    CryptographicOperations.ZeroMemory(raw);
    return k;
}

static int KeyGen(string dir, string pubOut)
{
    Directory.CreateDirectory(dir);
    if (File.Exists(KeyFile(dir))) return Fail("Já existe uma chave em " + dir + ". Não foi substituída (instalações confiam nela).");
    using var k = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var pkcs8 = k.ExportPkcs8PrivateKey();
    File.WriteAllBytes(KeyFile(dir), ProtectedData.Protect(pkcs8, Encoding.UTF8.GetBytes("VeyonSync.Publisher"), DataProtectionScope.CurrentUser));
    CryptographicOperations.ZeroMemory(pkcs8);
    File.WriteAllText(pubOut, Convert.ToBase64String(k.ExportSubjectPublicKeyInfo()));
    Console.WriteLine("Chave criada. Privada (protegida): " + KeyFile(dir));
    Console.WriteLine("Pública: " + pubOut);
    return 0;
}

static int PubKey(string dir)
{
    using var k = LoadKey(dir);
    Console.WriteLine(Convert.ToBase64String(k.ExportSubjectPublicKeyInfo()));
    return 0;
}

static int Pack(Dictionary<string, string> o)
{
    var product = Need(o, "product");
    if (product is not ("THC" or "STD")) throw new ArgumentException("--product deve ser THC ou STD.");
    var version = Need(o, "version");
    var service = Need(o, "service");
    var app = Need(o, "app");
    var exe = product == "THC" ? "VeyonSync.Teacher.exe" : "VeyonSync.Student.exe";
    if (!File.Exists(Path.Combine(service, exe))) throw new FileNotFoundException("Compilação ausente: " + Path.Combine(service, exe));
    var notes = o.TryGetValue("notes-file", out var nf) && File.Exists(nf) ? File.ReadAllText(nf).Trim() : "";
    var min = o.TryGetValue("min", out var mv) ? mv : "2.0.0";
    var outFile = Need(o, "out");

    var tmp = Path.Combine(Path.GetTempPath(), "vsync-pack-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tmp);
    try
    {
        var payload = Path.Combine(tmp, "payload.zip");
        using (var z = ZipFile.Open(payload, ZipArchiveMode.Create))
        {
            var info = JsonSerializer.Serialize(new { product, version, minVersion = min });
            using (var s = new StreamWriter(z.CreateEntry("update.json").Open())) s.Write(info);
            AddDir(z, service, "Service/", skip: "App", exclude: new[] { "appsettings.json" });
            AddDir(z, app, "App/", skip: null, exclude: Array.Empty<string>());
        }
        var m = new ReleaseManifest { Product = product, Version = version, MinVersion = min, Arch = "x64", Notes = notes, Prerelease = AppInfo.IsPrerelease(version) };
        using var key = LoadKey(Need(o, "key-dir"));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
        ReleasePackage.Create(payload, m, key, outFile);
        Console.WriteLine($"Pacote: {outFile} ({new FileInfo(outFile).Length / 1048576} MB)");
        return 0;
    }
    finally { try { Directory.Delete(tmp, true); } catch (Exception) { } }
}

static void AddDir(ZipArchive z, string dir, string prefix, string? skip, string[] exclude)
{
    var root = Path.GetFullPath(dir).TrimEnd('\\', '/');
    foreach (var f in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
    {
        var rel = Path.GetRelativePath(root, f).Replace('\\', '/');
        if (skip != null && rel.StartsWith(skip + "/", StringComparison.OrdinalIgnoreCase)) continue;
        if (exclude.Contains(Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)) continue;
        // Nunca empacotar dados reais nem segredos por engano.
        var ext = Path.GetExtension(f).ToLowerInvariant();
        if (ext is ".db" or ".key" or ".pfx" or ".pem" or ".vsync") continue;
        z.CreateEntryFromFile(f, prefix + rel, CompressionLevel.Optimal);
    }
}

static int Verify(string file, Dictionary<string, string> o)
{
    var pub = File.ReadAllText(Need(o, "pub")).Trim();
    var staging = Path.Combine(Path.GetTempPath(), "vsync-verify-" + Guid.NewGuid().ToString("N"));
    try
    {
        var v = ReleasePackage.Validate(file, Need(o, "product"), null, staging, pub);
        Console.WriteLine($"OK: {v.Manifest.Product} {v.Manifest.Version} ({v.Manifest.Arch}), assinatura e conteúdo conferidos.");
        return 0;
    }
    finally { try { Directory.Delete(staging, true); } catch (Exception) { } }
}
