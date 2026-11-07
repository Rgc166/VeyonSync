using VeyonSync.Shared;

namespace VeyonSync.Teacher;

/// <summary>Publicação de pacotes STD para distribuição pela rede (somente agentes já pareados com este professor).</summary>
public sealed class Updates(TeacherContext ctx)
{
    public static string Dir => Path.Combine(AppInfo.TeacherData, "Updates");

    public UpdateRow Publish(string packagePath, IReadOnlyCollection<string> agentIds)
    {
        if (!File.Exists(packagePath)) throw new InvalidDataException("Pacote não encontrado.");
        // ZIP de entrega (VeyonSync-STD-Update-x.zip) contém o .vsupd; o ZIP antigo do build (STD-x.zip) contém update.json.
        if (packagePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && !HasUpdateJson(packagePath))
            packagePath = ReleasePackage.ResolveUpdateFile(packagePath, Path.Combine(Dir, "zip"));
        if (packagePath.EndsWith(ReleasePackage.Extension, StringComparison.OrdinalIgnoreCase))
        {
            // Mesmo pacote e mesma validação do GitHub/arquivo; distribui só o conteúdo já conferido.
            var v = ReleasePackage.Validate(packagePath, "STD", null, Path.Combine(Dir, "validado"));
            packagePath = v.PayloadPath;
        }
        var info = PackageInfo.Read(packagePath);
        if (info.Product != "STD") throw new InvalidDataException("Este pacote não é do STD (aluno).");
        if (agentIds.Count == 0) throw new InvalidDataException("Selecione ao menos um computador.");
        var s = ctx.Settings();
        var machines = ctx.Db.Machines(s.NameTemplate, s.RecentSeconds).ToDictionary(m => m.AgentId);
        var targets = agentIds.Where(machines.ContainsKey).ToList();
        if (targets.Count == 0) throw new InvalidDataException("Nenhum computador selecionado está cadastrado neste professor.");
        // Mantém o histórico de destinos que já estão na versão para não repetir à toa.
        targets = targets.Where(a => AppInfo.CompareVersions(machines[a].Version, info.Version) < 0).ToList();
        if (targets.Count == 0) throw new InvalidDataException($"Todos os selecionados já estão na versão {info.Version} ou superior.");

        Directory.CreateDirectory(Dir);
        var file = Path.Combine(Dir, $"STD-{info.Version}-{DateTime.Now:yyyyMMddHHmmss}.zip");
        File.Copy(packagePath, file, overwrite: false);
        var sha = UpdateSigning.FileSha256(file);
        var manifest = new UpdateManifest("STD", info.Version, info.MinVersion, sha, new FileInfo(file).Length,
            Path.GetFileName(file), DateTimeOffset.UtcNow, ctx.Db.ReceiverId());
        using var key = Keys.UpdateKey();
        var json = UpdateSigning.ManifestJson(manifest);
        var sig = UpdateSigning.Sign(json, key);
        var id = ctx.Db.AddUpdate("STD", info.Version, file, sha, manifest.Size, json, sig, targets);
        return ctx.Db.Updates().First(u => u.Id == id);
    }

    static bool HasUpdateJson(string zip)
    {
        try { using var z = System.IO.Compression.ZipFile.OpenRead(zip); return z.GetEntry("update.json") != null; }
        catch (InvalidDataException) { throw new InvalidDataException("O arquivo não é um ZIP válido."); }
    }

    public SignedManifest? PendingFor(string agentId)
    {
        var p = ctx.Db.PendingUpdateFor(agentId);
        if (p == null) return null;
        return new SignedManifest(p.Value.manifest, p.Value.signature, p.Value.id);
    }
}
