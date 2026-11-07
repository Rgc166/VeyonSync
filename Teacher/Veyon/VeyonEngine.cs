using VeyonSync.Shared;

namespace VeyonSync.Teacher.Veyon;

/// <summary>
/// Integração com o Veyon deste professor (somente o laboratório local).
/// Fluxo: ler diretório → identificar registros gerenciados → comparar → backup → aplicar só o necessário
/// → ler novamente e verificar → registrar resultado por máquina.
/// </summary>
public sealed class VeyonEngine(TeacherContext ctx, ILogger<VeyonEngine> log)
{
    readonly SemaphoreSlim gate = new(1, 1);
    static string TempDir => Path.Combine(AppInfo.TeacherData, "veyon-tmp");
    public static string BackupDir => Path.Combine(AppInfo.TeacherData, "VeyonBackups");

    public bool Busy => gate.CurrentCount == 0;

    VeyonCli? Cli(out string? error)
    {
        var path = VeyonCli.Locate(ctx.Settings().VeyonCliPath);
        error = path == null ? "veyon-cli.exe não encontrado. Instale o Veyon neste professor ou informe o caminho em Configurações." : null;
        return path == null ? null : new VeyonCli(path);
    }

    internal sealed record Desired(MachineRow Machine, string Name, string Host, string Mac, string Location);

    internal sealed class Plan
    {
        public List<VeyonChange> Changes { get; } = new();
        public Dictionary<string, Desired> Desired { get; } = new();
    }

    internal static List<Desired> DesiredRecords(IEnumerable<MachineRow> machines, TeacherSettings s)
    {
        var list = new List<Desired>();
        foreach (var m in machines)
        {
            var host = s.AddressMode == "hostname" ? m.WindowsName : m.Ip ?? "";
            list.Add(new Desired(m, m.DisplayName, host, VeyonCli.VeyonMac(m.Mac), Naming.Sanitize(s.LabName)));
        }
        return list;
    }

    internal static Plan Compare(List<VeyonObject> current, List<Desired> wanted, Dictionary<string, Db.ManagedRecord> managed)
    {
        var plan = new Plan();
        var computers = current.Where(o => o.IsComputer).ToList();
        int NameCount(string name) => current.Count(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase));
        // Nomes desejados repetidos entre máquinas (ex.: duas no mesmo posto) tornariam o cadastro ambíguo.
        var dupWanted = wanted.GroupBy(w => w.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var d in wanted)
        {
            var ch = new VeyonChange
            {
                AgentId = d.Machine.AgentId, Machine = $"{d.Machine.Position} {d.Machine.WindowsName}".Trim(), Lab = d.Machine.Lab,
                NewName = d.Name, NewHost = d.Host, NewMac = d.Mac, NewLocation = d.Location
            };
            plan.Desired[d.Machine.AgentId] = d;
            plan.Changes.Add(ch);

            if (string.IsNullOrEmpty(d.Host)) { ch.Action = "Conflito"; ch.Detail = "Sem endereço de conexão (IP ainda não recebido)."; continue; }
            if (string.IsNullOrEmpty(d.Name)) { ch.Action = "Conflito"; ch.Detail = "Nome exibido vazio."; continue; }
            if (dupWanted.Contains(d.Name)) { ch.Action = "Conflito"; ch.Detail = $"Outro computador também seria cadastrado como '{d.Name}'. Corrija a posição duplicada."; continue; }

            bool Same(VeyonObject o) => Eq(o.Location, d.Location) && Eq(o.Name, d.Name) && Eq(o.Host, d.Host)
                                        && VeyonCli.NormMac(o.Mac) == VeyonCli.NormMac(d.Mac);

            managed.TryGetValue(d.Machine.AgentId, out var m);
            VeyonObject? target = null;
            if (m != null)
            {
                var hits = computers.Where(o => Eq(o.Location, m.Location) && Eq(o.Name, m.Name)).ToList();
                if (hits.Count > 1) { Conflict(ch, $"Há {hits.Count} registros '{m.Name}' em '{m.Location}' no Veyon."); continue; }
                target = hits.FirstOrDefault();
            }
            if (target == null)
            {
                // Sem registro gerenciado visível (novo, removido à mão ou backup restaurado): associa pelo MAC.
                var mac = VeyonCli.NormMac(d.Mac);
                var byMac = mac == "" ? new List<VeyonObject>() : computers.Where(o => VeyonCli.NormMac(o.Mac) == mac).ToList();
                if (byMac.Count > 1) { Conflict(ch, $"O MAC {d.Mac} aparece em {byMac.Count} registros do Veyon."); continue; }
                if (byMac.Count == 1) target = byMac[0]; // associação inequívoca a um registro criado antes
                else
                {
                    var similar = computers.Where(o => (Eq(o.Location, d.Location) && Eq(o.Name, d.Name)) || Eq(o.Host, d.Host)).ToList();
                    if (similar.Count > 0)
                    {
                        var s0 = similar[0];
                        Conflict(ch, $"Registro existente '{s0.Name}' ({s0.Host}, MAC {(s0.Mac == "" ? "vazio" : s0.Mac)}) em '{s0.Location}' tem o mesmo nome ou endereço, mas não pertence a este computador. Remova ou corrija no Veyon Configurator e tente de novo.");
                        continue;
                    }
                }
            }

            if (target != null)
            {
                ch.OldName = target.Name; ch.OldHost = target.Host; ch.OldMac = target.Mac; ch.OldLocation = target.Location;
                if (Same(target)) { ch.Action = "Já atualizado"; ch.Detail = "Cadastro no Veyon já corresponde."; continue; }
                if (NameCount(target.Name) > 1) { Conflict(ch, $"O nome '{target.Name}' aparece mais de uma vez no Veyon; a remoção por nome seria ambígua."); continue; }
                if (!Eq(target.Name, d.Name) && NameCount(d.Name) > 0) { Conflict(ch, $"Já existe outro objeto chamado '{d.Name}' no Veyon."); continue; }
                ch.Action = "Atualizar";
                ch.Detail = "Substituir o registro antigo pelo atual.";
            }
            else
            {
                var exact = computers.Where(Same).ToList();
                if (exact.Count == 1) { ch.Action = "Já atualizado"; ch.Detail = "Registro idêntico já existe."; ch.OldName = d.Name; ch.OldHost = d.Host; ch.OldMac = d.Mac; ch.OldLocation = d.Location; continue; }
                if (NameCount(d.Name) > 0) { Conflict(ch, $"Já existe um objeto chamado '{d.Name}' no Veyon (em outra sala ou sem MAC correspondente)."); continue; }
                ch.Action = "Adicionar";
                ch.Detail = "Novo registro.";
            }
        }
        return plan;

        static void Conflict(VeyonChange c, string why) { c.Action = "Conflito"; c.Detail = why; }
    }

    static bool Eq(string? a, string? b) => string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

    IEnumerable<MachineRow> Candidates(bool onlyPending, bool retryFailedOnly, IReadOnlyCollection<string>? agentIds)
    {
        var s = ctx.Settings();
        var queue = ctx.Db.QueueStatus();
        // Somente o laboratório deste professor: nunca aplicar outros laboratórios neste Veyon.
        var all = ctx.Db.Machines(s.NameTemplate, s.RecentSeconds).Where(m => string.Equals(m.Lab, ctx.Lab, StringComparison.OrdinalIgnoreCase));
        if (agentIds != null && agentIds.Count > 0) all = all.Where(m => agentIds.Contains(m.AgentId));
        if (retryFailedOnly) return all.Where(m => queue.TryGetValue(m.AgentId, out var q) && q == "erro");
        if (onlyPending) return all.Where(m => !queue.TryGetValue(m.AgentId, out var q) || q != "aplicado");
        return all;
    }

    public async Task<VeyonPreview> PreviewAsync(bool onlyPending, IReadOnlyCollection<string>? agentIds, CancellationToken ct)
    {
        var s = ctx.Settings();
        var pv = new VeyonPreview { Lab = ctx.Lab, Destination = ctx.TeacherName };
        var cli = Cli(out var err);
        if (cli == null) { pv.Error = err; return pv; }
        pv.CliFound = true; pv.CliPath = cli.ExePath; pv.CliVersion = cli.Version();
        try
        {
            var (ok, detail) = await cli.CheckAsync(ct);
            if (!ok) { pv.Error = detail; return pv; }
            var current = await cli.ExportAsync(TempDir, ct);
            var plan = Compare(current, DesiredRecords(Candidates(onlyPending, false, agentIds), s), ctx.Db.Managed(ctx.Lab));
            pv.Changes = plan.Changes;
        }
        catch (Exception e) when (e is not OperationCanceledException) { pv.Error = e.Message; }
        return pv;
    }

    public async Task<string> BackupAsync(string note, CancellationToken ct)
    {
        var cli = Cli(out var err) ?? throw new InvalidOperationException(err);
        Directory.CreateDirectory(BackupDir);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var file = Path.Combine(BackupDir, $"veyon-config-{ctx.Lab}-{stamp}.json");
        var r = await cli.ConfigExportAsync(file, ct);
        if (!r.Ok || !File.Exists(file)) throw new InvalidOperationException("Backup do Veyon falhou (" + r.Summary + "). Nada foi alterado.");
        // Cópia legível do diretório, útil para conferência manual.
        await cli.ExportCsvAsync(Path.Combine(BackupDir, $"veyon-diretorio-{ctx.Lab}-{stamp}.txt"), ct);
        ctx.Db.AddBackup(file, note);
        return file;
    }

    public async Task<BatchSummary> ApplyAsync(bool retryFailedOnly, IReadOnlyCollection<string>? agentIds, CancellationToken ct)
    {
        var summary = new BatchSummary { Lab = ctx.Lab, Destination = ctx.TeacherName };
        if (!await gate.WaitAsync(0, ct))
        {
            summary.Unavailable = true;
            summary.Detail = "Já existe uma integração em andamento neste professor. Aguarde terminar.";
            return summary;
        }
        try
        {
            var s = ctx.Settings();
            var cli = Cli(out var err);
            if (cli == null) { summary.Unavailable = true; summary.Detail = err!; ctx.Db.Error(ctx.Lab, "", ctx.TeacherName, "Veyon", err!); return summary; }
            var (ok, detail) = await cli.CheckAsync(ct);
            if (!ok) { summary.Unavailable = true; summary.Detail = detail; ctx.Db.Error(ctx.Lab, "", ctx.TeacherName, "Veyon", detail); return summary; }

            var current = await cli.ExportAsync(TempDir, ct);
            var plan = Compare(current, DesiredRecords(Candidates(true, retryFailedOnly, agentIds), s), ctx.Db.Managed(ctx.Lab));
            var work = plan.Changes.Where(c => c.Action is "Adicionar" or "Atualizar").ToList();

            if (work.Count > 0)
                summary.BackupFile = await BackupAsync($"Antes da integração de {work.Count} computador(es)", ct);

            var locations = current.Where(o => !o.IsComputer).Select(o => o.Name)
                .Concat(current.Where(o => o.IsComputer).Select(o => o.Location))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var failed = new Dictionary<string, string>();

            foreach (var ch in work)
            {
                ct.ThrowIfCancellationRequested();
                if (!locations.Contains(ch.NewLocation) && await cli.LocationListedAsync(ch.NewLocation, ct)) locations.Add(ch.NewLocation);
                if (!locations.Contains(ch.NewLocation))
                {
                    var lr = await cli.AddLocationAsync(ch.NewLocation, ct);
                    if (!lr.Ok) { failed[ch.AgentId] = $"Não foi possível criar a sala '{ch.NewLocation}' ({lr.Summary})."; continue; }
                    locations.Add(ch.NewLocation);
                }
                if (ch.Action == "Atualizar" && ch.OldName != null)
                {
                    var rr = await cli.RemoveAsync(ch.OldName, ct);
                    if (!rr.Ok) { failed[ch.AgentId] = $"Falha ao remover o registro antigo '{ch.OldName}' ({rr.Summary})."; continue; }
                }
                var ar = await cli.AddComputerAsync(ch.NewName, ch.NewHost, ch.NewMac, ch.NewLocation, ct);
                if (!ar.Ok) failed[ch.AgentId] = $"Falha ao cadastrar ({ar.Summary})." + (ch.Action == "Atualizar" ? " O registro antigo já tinha sido removido; restaure o backup ou tente de novo." : "");
            }

            // Verificação real: lê o diretório outra vez.
            var after = work.Count > 0 ? await cli.ExportAsync(TempDir, ct) : current;
            foreach (var ch in plan.Changes)
            {
                var item = new VeyonItemResult { AgentId = ch.AgentId, Machine = ch.Machine };
                var d = plan.Desired[ch.AgentId];
                Db.ManagedRecord? applied = null;
                if (ch.Action == "Conflito") { item.Outcome = "Conflito"; item.Detail = ch.Detail; summary.Conflicts++; }
                else if (failed.TryGetValue(ch.AgentId, out var why)) { item.Outcome = "Erro"; item.Detail = why; summary.Errors++; }
                else
                {
                    var found = after.Count(o => o.IsComputer && Eq(o.Location, d.Location) && Eq(o.Name, d.Name) && Eq(o.Host, d.Host)
                                                 && VeyonCli.NormMac(o.Mac) == VeyonCli.NormMac(d.Mac));
                    if (found == 1)
                    {
                        applied = new Db.ManagedRecord(ch.AgentId, ctx.Lab, d.Name, d.Host, d.Mac, d.Location);
                        if (ch.Action == "Já atualizado") { item.Outcome = "Já atualizado"; summary.AlreadyUpdated++; }
                        else { item.Outcome = "Integrado"; summary.Integrated++; }
                        item.Detail = $"{d.Name} → {d.Host} ({(d.Mac == "" ? "sem MAC" : d.Mac)}) em {d.Location}";
                    }
                    else
                    {
                        item.Outcome = "Erro";
                        item.Detail = found == 0 ? "O comando terminou, mas o registro não aparece na releitura do Veyon." : $"Releitura encontrou {found} registros iguais.";
                        summary.Errors++;
                    }
                }
                ctx.Db.VeyonResult(ch.AgentId, ctx.Lab, ch.Machine, item.Outcome, item.Detail, applied);
                summary.Items.Add(item);
            }
            ctx.Db.ResolveErrors(null, "Veyon-geral");
            summary.Detail = work.Count == 0 ? "Nenhuma alteração necessária." :
                "Alterações gravadas no Veyon deste professor. O Veyon Master pode precisar ser reaberto para exibir os novos dados.";
            return summary;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogError(e, "Integração Veyon falhou");
            summary.Errors++;
            summary.Detail = e.Message;
            ctx.Db.Error(ctx.Lab, "", ctx.TeacherName, "Veyon-geral", e.Message);
            return summary;
        }
        finally { gate.Release(); }
    }

    /// <summary>Restauração explícita de um backup deste laboratório (não afeta outros professores).</summary>
    public async Task RestoreAsync(long backupId, CancellationToken ct)
    {
        var b = ctx.Db.Backup(backupId) ?? throw new InvalidDataException("Backup não encontrado.");
        var full = Path.GetFullPath(b.File);
        if (!full.StartsWith(Path.GetFullPath(BackupDir), StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            throw new InvalidDataException("Arquivo de backup ausente ou fora da pasta de backups.");
        if (!await gate.WaitAsync(0, ct)) throw new InvalidOperationException("Há uma integração em andamento. Aguarde.");
        try
        {
            var cli = Cli(out var err) ?? throw new InvalidOperationException(err);
            await BackupAsync("Antes de restaurar o backup " + Path.GetFileName(full), ct);
            var r = await cli.ConfigImportAsync(full, ct);
            if (!r.Ok) throw new InvalidOperationException("Restauração falhou (" + r.Summary + ").");
            ctx.Db.Hist("", ctx.Lab, ctx.TeacherName, "Integração Veyon", "Backup restaurado: " + Path.GetFileName(full), "ok");
            ctx.Db.QueueAll(ctx.Lab); // a restauração pode ter desfeito cadastros: reconferir na próxima integração
        }
        finally { gate.Release(); }
    }
}
