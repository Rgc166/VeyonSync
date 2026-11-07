using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VeyonSync.Shared;

namespace VeyonSync.Teacher;

/// <summary>
/// Persistência do professor. Mantém as tabelas da Etapa 1 e aplica migrações aditivas
/// (ALTER TABLE ADD COLUMN / CREATE TABLE IF NOT EXISTS) com cópia do banco antes de migrar.
/// </summary>
public sealed class Db
{
    public const int SchemaVersion = 2;
    readonly string path;
    static readonly object L = new();
    public static string Dir => AppInfo.TeacherData;

    public Db() : this(Path.Combine(Dir, "teacher.db")) { }

    internal Db(string dbPath)
    {
        path = dbPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var existed = File.Exists(path);
        using var c = Open();
        foreach (var s in new[] {
            // Tabelas da Etapa 1 (inalteradas)
            "CREATE TABLE IF NOT EXISTS machines(agent_id TEXT PRIMARY KEY, lab TEXT, name TEXT, position TEXT, user TEXT, ip TEXT, mac TEXT, last_seen TEXT, last_seq INTEGER, version TEXT)",
            "CREATE TABLE IF NOT EXISTS tokens(agent_id TEXT PRIMARY KEY, hash TEXT, lab TEXT)",
            "CREATE TABLE IF NOT EXISTS codes(hash TEXT PRIMARY KEY, expires TEXT)",
            "CREATE TABLE IF NOT EXISTS history(id INTEGER PRIMARY KEY AUTOINCREMENT, agent_id TEXT, ts TEXT, kind TEXT, detail TEXT)",
            "CREATE TABLE IF NOT EXISTS veyon_queue(agent_id TEXT PRIMARY KEY, status TEXT, created TEXT)",
            "CREATE TABLE IF NOT EXISTS collect_requests(agent_id TEXT PRIMARY KEY)",
            "CREATE TABLE IF NOT EXISTS schema_version(v INTEGER)" }) Exec(c, s);

        var current = Scalar<long?>(c, "SELECT MAX(v) FROM schema_version") ?? 1;
        if (current < SchemaVersion)
        {
            if (existed)
            {
                c.Close();
                SqliteConnection.ClearAllPools();
                File.Copy(path, Path.Combine(Path.GetDirectoryName(path)!, $"teacher.db.antes-v{SchemaVersion}-{DateTime.Now:yyyyMMdd-HHmmss}.bak"), false);
                c.Open();
            }
            MigrateTo2(c);
            Exec(c, "DELETE FROM schema_version");
            Exec(c, "INSERT INTO schema_version(v) VALUES(@v)", ("@v", SchemaVersion));
        }
        EnsureNewTables(c);
        Exec(c, "PRAGMA journal_mode=WAL");
    }

    // Tabelas criadas pelo VeyonSync 2.0 e as colunas que o código usa.
    static readonly (string Table, string[] Cols, string Create)[] NewTables =
    {
        ("errors", new[] { "id", "ts", "last_ts", "lab", "agent_id", "machine", "source", "message", "dedupe", "count", "resolved" },
            "CREATE TABLE IF NOT EXISTS errors(id INTEGER PRIMARY KEY AUTOINCREMENT, ts TEXT, last_ts TEXT, lab TEXT, agent_id TEXT, machine TEXT, source TEXT, message TEXT, dedupe TEXT, count INTEGER DEFAULT 1, resolved INTEGER DEFAULT 0)"),
        ("veyon_managed", new[] { "agent_id", "lab", "name", "host", "mac", "location", "applied_at" },
            "CREATE TABLE IF NOT EXISTS veyon_managed(agent_id TEXT PRIMARY KEY, lab TEXT, name TEXT, host TEXT, mac TEXT, location TEXT, applied_at TEXT)"),
        ("maps", new[] { "lab", "json", "updated" }, "CREATE TABLE IF NOT EXISTS maps(lab TEXT PRIMARY KEY, json TEXT, updated TEXT)"),
        ("settings", new[] { "key", "value" }, "CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY, value TEXT)"),
        ("peers", new[] { "id", "name", "lab", "url", "cert_sha256", "token_protected", "enabled", "added", "last_status", "last_contact" },
            "CREATE TABLE IF NOT EXISTS peers(id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT, lab TEXT, url TEXT, cert_sha256 TEXT, token_protected TEXT, enabled INTEGER DEFAULT 1, added TEXT, last_status TEXT, last_contact TEXT)"),
        ("peer_tokens", new[] { "hash", "caller_name", "caller_lab", "created" }, "CREATE TABLE IF NOT EXISTS peer_tokens(hash TEXT PRIMARY KEY, caller_name TEXT, caller_lab TEXT, created TEXT)"),
        ("updates", new[] { "id", "product", "version", "file", "sha256", "size", "manifest", "signature", "created" },
            "CREATE TABLE IF NOT EXISTS updates(id INTEGER PRIMARY KEY AUTOINCREMENT, product TEXT, version TEXT, file TEXT, sha256 TEXT, size INTEGER, manifest TEXT, signature TEXT, created TEXT)"),
        ("update_targets", new[] { "update_id", "agent_id", "status", "detail", "updated" },
            "CREATE TABLE IF NOT EXISTS update_targets(update_id INTEGER, agent_id TEXT, status TEXT, detail TEXT, updated TEXT, PRIMARY KEY(update_id, agent_id))"),
        ("veyon_backups", new[] { "id", "ts", "file", "note" }, "CREATE TABLE IF NOT EXISTS veyon_backups(id INTEGER PRIMARY KEY AUTOINCREMENT, ts TEXT, file TEXT, note TEXT)"),
        ("batch_results", new[] { "id", "ts", "lab", "agent_id", "machine", "outcome", "detail" },
            "CREATE TABLE IF NOT EXISTS batch_results(id INTEGER PRIMARY KEY AUTOINCREMENT, ts TEXT, lab TEXT, agent_id TEXT, machine TEXT, outcome TEXT, detail TEXT)"),
        // 2.2: acompanhamento de cada coleta solicitada (correlação pedido → resposta).
        ("collect_rounds", new[] { "id", "ts", "total" },
            "CREATE TABLE IF NOT EXISTS collect_rounds(id INTEGER PRIMARY KEY AUTOINCREMENT, ts TEXT, total INTEGER)"),
        ("collect_targets", new[] { "round_id", "agent_id", "status", "signaled", "answered" },
            "CREATE TABLE IF NOT EXISTS collect_targets(round_id INTEGER, agent_id TEXT, status TEXT, signaled TEXT, answered TEXT, PRIMARY KEY(round_id, agent_id))")
    };

    /// <summary>
    /// Versões anteriores podem ter criado tabelas com o mesmo nome e outras colunas. Nesse caso a tabela antiga
    /// é preservada com outro nome (legado_*) e a do 2.0 é criada. Nada é apagado.
    /// </summary>
    static void EnsureNewTables(SqliteConnection c)
    {
        foreach (var (table, cols, create) in NewTables)
        {
            var have = new List<string>();
            using (var m = c.CreateCommand())
            {
                m.CommandText = $"PRAGMA table_info({table})";
                using var r = m.ExecuteReader();
                while (r.Read()) have.Add(r.GetString(1));
            }
            if (have.Count > 0 && cols.Any(col => !have.Contains(col, StringComparer.OrdinalIgnoreCase)))
                Exec(c, $"ALTER TABLE {table} RENAME TO legado_{table}_{DateTime.Now:yyyyMMddHHmmss}");
            Exec(c, create);
        }
    }

    static void MigrateTo2(SqliteConnection c)
    {
        using var tx = c.BeginTransaction();
        void Add(string table, string col, string def)
        {
            var cols = new List<string>();
            using (var m = c.CreateCommand())
            {
                m.Transaction = tx;
                m.CommandText = $"PRAGMA table_info({table})";
                using var r = m.ExecuteReader();
                while (r.Read()) cols.Add(r.GetString(1));
            }
            if (!cols.Contains(col, StringComparer.OrdinalIgnoreCase))
                ExecTx(c, tx, $"ALTER TABLE {table} ADD COLUMN {col} {def}");
        }
        Add("machines", "friendly_override", "TEXT");
        Add("machines", "comm_state", "TEXT DEFAULT 'ok'");
        Add("machines", "auto_mode", "INTEGER DEFAULT 1");
        Add("machines", "first_seen", "TEXT");
        Add("machines", "mgh", "TEXT");
        Add("machines", "dup_flag", "INTEGER DEFAULT 0");
        Add("machines", "session_note", "TEXT");
        Add("history", "lab", "TEXT");
        Add("history", "machine", "TEXT");
        Add("history", "result", "TEXT DEFAULT 'info'");
        Add("veyon_queue", "last_error", "TEXT");
        Add("veyon_queue", "updated", "TEXT");
        foreach (var s in new[] {
            "CREATE TABLE IF NOT EXISTS errors(id INTEGER PRIMARY KEY AUTOINCREMENT, ts TEXT, last_ts TEXT, lab TEXT, agent_id TEXT, machine TEXT, source TEXT, message TEXT, dedupe TEXT, count INTEGER DEFAULT 1, resolved INTEGER DEFAULT 0)",
            "CREATE INDEX IF NOT EXISTS ix_errors_dedupe ON errors(dedupe, resolved)",
            "CREATE INDEX IF NOT EXISTS ix_history_ts ON history(ts)",
            "CREATE TABLE IF NOT EXISTS veyon_managed(agent_id TEXT PRIMARY KEY, lab TEXT, name TEXT, host TEXT, mac TEXT, location TEXT, applied_at TEXT)",
            "CREATE TABLE IF NOT EXISTS maps(lab TEXT PRIMARY KEY, json TEXT, updated TEXT)",
            "CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY, value TEXT)",
            "CREATE TABLE IF NOT EXISTS peers(id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT, lab TEXT, url TEXT, cert_sha256 TEXT, token_protected TEXT, enabled INTEGER DEFAULT 1, added TEXT, last_status TEXT, last_contact TEXT)",
            "CREATE TABLE IF NOT EXISTS peer_tokens(hash TEXT PRIMARY KEY, caller_name TEXT, caller_lab TEXT, created TEXT)",
            "CREATE TABLE IF NOT EXISTS updates(id INTEGER PRIMARY KEY AUTOINCREMENT, product TEXT, version TEXT, file TEXT, sha256 TEXT, size INTEGER, manifest TEXT, signature TEXT, created TEXT)",
            "CREATE TABLE IF NOT EXISTS update_targets(update_id INTEGER, agent_id TEXT, status TEXT, detail TEXT, updated TEXT, PRIMARY KEY(update_id, agent_id))",
            "CREATE TABLE IF NOT EXISTS veyon_backups(id INTEGER PRIMARY KEY AUTOINCREMENT, ts TEXT, file TEXT, note TEXT)",
            "CREATE TABLE IF NOT EXISTS batch_results(id INTEGER PRIMARY KEY AUTOINCREMENT, ts TEXT, lab TEXT, agent_id TEXT, machine TEXT, outcome TEXT, detail TEXT)" })
            ExecTx(c, tx, s);
        ExecTx(c, tx, "UPDATE machines SET first_seen=last_seen WHERE first_seen IS NULL");
        ExecTx(c, tx, "UPDATE history SET result='info' WHERE result IS NULL");
        ExecTx(c, tx, "UPDATE history SET machine=(SELECT name FROM machines WHERE machines.agent_id=history.agent_id), lab=(SELECT lab FROM machines WHERE machines.agent_id=history.agent_id) WHERE machine IS NULL");
        ExecTx(c, tx, "INSERT INTO history(agent_id,ts,kind,detail,result) VALUES('',@t,'Atualização de versão','Banco migrado para o esquema 2 (Veyon Sync 2.0)','ok')", ("@t", Now()));
        tx.Commit();
    }

    // ---------------- utilitários ----------------
    SqliteConnection Open()
    {
        var c = new SqliteConnection($"Data Source={path};Default Timeout=15");
        c.Open();
        return c;
    }

    static int Exec(SqliteConnection c, string sql, params (string, object?)[] p) => ExecTx(c, null, sql, p);

    static int ExecTx(SqliteConnection c, SqliteTransaction? tx, string sql, params (string, object?)[] p)
    {
        using var m = c.CreateCommand();
        m.Transaction = tx;
        m.CommandText = sql;
        foreach (var (n, v) in p) m.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return m.ExecuteNonQuery();
    }

    static T? Scalar<T>(SqliteConnection c, string sql, params (string, object?)[] p)
    {
        using var m = c.CreateCommand();
        m.CommandText = sql;
        foreach (var (n, v) in p) m.Parameters.AddWithValue(n, v ?? DBNull.Value);
        var r = m.ExecuteScalar();
        if (r == null || r is DBNull) return default;
        var t = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        return (T)Convert.ChangeType(r, t);
    }

    static List<T> Query<T>(SqliteConnection c, string sql, Func<SqliteDataReader, T> map, params (string, object?)[] p)
    {
        using var m = c.CreateCommand();
        m.CommandText = sql;
        foreach (var (n, v) in p) m.Parameters.AddWithValue(n, v ?? DBNull.Value);
        using var r = m.ExecuteReader();
        var list = new List<T>();
        while (r.Read()) list.Add(map(r));
        return list;
    }

    static string? S(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetValue(i).ToString();
    static long Lg(SqliteDataReader r, int i) => r.IsDBNull(i) ? 0 : r.GetInt64(i);
    static DateTimeOffset? D(SqliteDataReader r, int i) =>
        r.IsDBNull(i) ? null : DateTimeOffset.TryParse(r.GetString(i), out var d) ? d : null;

    public static string H(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
    static string Now() => DateTimeOffset.UtcNow.ToString("o");

    // ---------------- configurações ----------------
    public string? GetSetting(string key)
    {
        lock (L) { using var c = Open(); return Scalar<string>(c, "SELECT value FROM settings WHERE key=@k", ("@k", key)); }
    }

    public void SetSetting(string key, string? value)
    {
        lock (L) { using var c = Open(); Exec(c, "INSERT OR REPLACE INTO settings(key,value) VALUES(@k,@v)", ("@k", key), ("@v", value)); }
    }

    public string ReceiverId()
    {
        var id = GetSetting("receiver_id");
        if (Guid.TryParse(id, out _)) return id!;
        id = Guid.NewGuid().ToString();
        SetSetting("receiver_id", id);
        return id;
    }

    // ---------------- histórico e erros ----------------
    static void HistTx(SqliteConnection c, SqliteTransaction? tx, string agentId, string lab, string machine, string kind, string detail, string result = "info") =>
        ExecTx(c, tx, "INSERT INTO history(agent_id,ts,kind,detail,lab,machine,result) VALUES(@a,@t,@k,@d,@l,@m,@r)",
            ("@a", agentId), ("@t", Now()), ("@k", kind), ("@d", detail), ("@l", lab), ("@m", machine), ("@r", result));

    public void Hist(string agentId, string lab, string machine, string kind, string detail, string result = "info")
    {
        lock (L) { using var c = Open(); HistTx(c, null, agentId, lab, machine, kind, detail, result); }
    }

    /// <summary>Registra erro pendente sem duplicar: a mesma origem/mensagem só incrementa o contador.</summary>
    public void Error(string lab, string agentId, string machine, string source, string message)
    {
        var dedupe = H($"{lab}|{agentId}|{source}|{message}");
        lock (L)
        {
            using var c = Open();
            var n = Exec(c, "UPDATE errors SET count=count+1, last_ts=@t WHERE dedupe=@d AND resolved=0", ("@t", Now()), ("@d", dedupe));
            if (n == 0)
            {
                Exec(c, "INSERT INTO errors(ts,last_ts,lab,agent_id,machine,source,message,dedupe) VALUES(@t,@t,@l,@a,@m,@s,@msg,@d)",
                    ("@t", Now()), ("@l", lab), ("@a", agentId), ("@m", machine), ("@s", source), ("@msg", message), ("@d", dedupe));
                HistTx(c, null, agentId, lab, machine, "Falha", $"{source}: {message}", "erro");
            }
        }
    }

    public void ResolveErrors(string? agentId, string source)
    {
        lock (L)
        {
            using var c = Open();
            var n = agentId == null
                ? Exec(c, "UPDATE errors SET resolved=1 WHERE source=@s AND resolved=0", ("@s", source))
                : Exec(c, "UPDATE errors SET resolved=1 WHERE agent_id=@a AND source=@s AND resolved=0", ("@a", agentId), ("@s", source));
            if (n > 0 && agentId != null)
            {
                var info = Query(c, "SELECT lab,name FROM machines WHERE agent_id=@a", r => (S(r, 0) ?? "", S(r, 1) ?? ""), ("@a", agentId)).FirstOrDefault();
                HistTx(c, null, agentId, info.Item1, info.Item2, "Recuperação", source + ": normalizado", "ok");
            }
        }
    }

    public void ResolveError(long id)
    {
        lock (L) { using var c = Open(); Exec(c, "UPDATE errors SET resolved=1 WHERE id=@i", ("@i", id)); }
    }

    public List<ErrorRow> Errors(bool includeResolved, int limit = 1000)
    {
        lock (L)
        {
            using var c = Open();
            return Query(c, $"SELECT id,ts,last_ts,lab,machine,source,message,count,resolved,agent_id FROM errors {(includeResolved ? "" : "WHERE resolved=0")} ORDER BY last_ts DESC LIMIT @n",
                r => new ErrorRow
                {
                    Id = r.GetInt64(0), Ts = D(r, 1) ?? default, LastTs = D(r, 2) ?? default, Lab = S(r, 3) ?? "", Machine = S(r, 4) ?? "",
                    Source = S(r, 5) ?? "", Message = S(r, 6) ?? "", Count = (int)Lg(r, 7), Resolved = Lg(r, 8) == 1, AgentId = S(r, 9) ?? ""
                }, ("@n", limit));
        }
    }

    public List<HistoryRow> History(HistoryQuery q)
    {
        var where = new List<string>();
        var p = new List<(string, object?)>();
        if (q.From != null) { where.Add("ts >= @f"); p.Add(("@f", q.From.Value.ToUniversalTime().ToString("o"))); }
        if (q.To != null) { where.Add("ts <= @to"); p.Add(("@to", q.To.Value.ToUniversalTime().ToString("o"))); }
        if (!string.IsNullOrWhiteSpace(q.Lab)) { where.Add("lab = @l"); p.Add(("@l", q.Lab)); }
        if (!string.IsNullOrWhiteSpace(q.Machine)) { where.Add("(machine LIKE @m OR agent_id = @mid)"); p.Add(("@m", "%" + q.Machine + "%")); p.Add(("@mid", q.Machine)); }
        if (!string.IsNullOrWhiteSpace(q.Kind)) { where.Add("kind = @k"); p.Add(("@k", q.Kind)); }
        if (!string.IsNullOrWhiteSpace(q.Result)) { where.Add("result = @r"); p.Add(("@r", q.Result)); }
        p.Add(("@n", Math.Clamp(q.Limit, 1, 20000)));
        var sql = "SELECT id,ts,lab,agent_id,machine,kind,result,detail FROM history" +
                  (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "") + " ORDER BY id DESC LIMIT @n";
        lock (L)
        {
            using var c = Open();
            return Query(c, sql, r => new HistoryRow
            {
                Id = r.GetInt64(0), Ts = D(r, 1) ?? default, Lab = S(r, 2) ?? "", AgentId = S(r, 3) ?? "", Machine = S(r, 4) ?? "",
                Kind = S(r, 5) ?? "", Result = S(r, 6) ?? "", Detail = S(r, 7) ?? ""
            }, p.ToArray());
        }
    }

    public List<string> HistoryKinds()
    {
        lock (L) { using var c = Open(); return Query(c, "SELECT DISTINCT kind FROM history WHERE kind IS NOT NULL ORDER BY kind", r => r.GetString(0)); }
    }

    /// <summary>Eventos importantes para a visão geral (sem transições de comunicação nem trocas de usuário).</summary>
    public List<HistoryRow> RecentEvents(int limit = 8)
    {
        lock (L)
        {
            using var c = Open();
            return Query(c, "SELECT id,ts,lab,agent_id,machine,kind,result,detail FROM history WHERE kind NOT IN ('Perda de comunicação','Comunicação recuperada','Mudança de usuário') ORDER BY id DESC LIMIT @n",
                r => new HistoryRow
                {
                    Id = r.GetInt64(0), Ts = D(r, 1) ?? default, Lab = S(r, 2) ?? "", AgentId = S(r, 3) ?? "", Machine = S(r, 4) ?? "",
                    Kind = S(r, 5) ?? "", Result = S(r, 6) ?? "", Detail = S(r, 7) ?? ""
                }, ("@n", Math.Clamp(limit, 1, 50)));
        }
    }

    public void Prune(int retentionDays)
    {
        var cut = DateTimeOffset.UtcNow.AddDays(-Math.Max(7, retentionDays)).ToString("o");
        lock (L)
        {
            using var c = Open();
            Exec(c, "DELETE FROM history WHERE ts < @c", ("@c", cut));
            Exec(c, "DELETE FROM errors WHERE resolved=1 AND last_ts < @c", ("@c", cut));
            Exec(c, "DELETE FROM batch_results WHERE ts < @c", ("@c", cut));
            // Limite rígido de tamanho do histórico.
            Exec(c, "DELETE FROM history WHERE id <= (SELECT MAX(id) FROM history) - 200000");
            Exec(c, "DELETE FROM codes WHERE expires < @n", ("@n", Now()));
        }
    }

    // ---------------- pareamento ----------------
    /// <summary>Gera um código novo de uso único e invalida os anteriores.</summary>
    public string NewPairingCode()
    {
        var code = RandomNumberGenerator.GetInt32(0, 100000000).ToString("D8");
        lock (L)
        {
            using var c = Open();
            Exec(c, "DELETE FROM codes");
            Exec(c, "INSERT INTO codes VALUES(@h,@e)", ("@h", H(code)), ("@e", DateTimeOffset.UtcNow.AddMinutes(15).ToString("o")));
            HistTx(c, null, "", "", "", "Ação administrativa", "Novo código de credenciamento gerado (válido por 15 min)");
        }
        return code;
    }

    public DateTimeOffset? CodeExpires()
    {
        lock (L)
        {
            using var c = Open();
            var s = Scalar<string>(c, "SELECT MAX(expires) FROM codes WHERE expires > @n", ("@n", Now()));
            return s != null && DateTimeOffset.TryParse(s, out var d) ? d : null;
        }
    }

    public sealed record PairResult(string? Token, int Status, string? Error);

    public PairResult Pair(string code, string agentId, string lab, string? position, string machineName)
    {
        lock (L)
        {
            using var c = Open();
            Exec(c, "DELETE FROM codes WHERE expires < @n", ("@n", Now()));
            var exists = Scalar<long>(c, "SELECT COUNT(*) FROM codes WHERE hash=@h", ("@h", H(code))) > 0;
            if (!exists) return new PairResult(null, 401, "Código inválido, expirado ou já usado. Gere um novo código no THC.");

            string? normalized = null;
            if (!string.IsNullOrWhiteSpace(position))
            {
                normalized = Naming.NormalizePosition(position);
                var other = Query(c, "SELECT agent_id,name,position FROM machines WHERE lab=@l AND agent_id<>@a",
                        r => (S(r, 0) ?? "", S(r, 1) ?? "", S(r, 2) ?? ""), ("@l", lab), ("@a", agentId))
                    .FirstOrDefault(m => Naming.NormalizePosition(m.Item3) == normalized);
                if (other.Item1 != null && other.Item1 != "")
                    return new PairResult(null, 409, $"A posição {normalized} já está registrada para '{other.Item2}' neste laboratório. Escolha outro número ou remova o registro antigo no THC (Computadores → Remover registro).");
            }

            using var tx = c.BeginTransaction();
            ExecTx(c, tx, "DELETE FROM codes WHERE hash=@h", ("@h", H(code)));
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var repair = Scalar<long>(c, "SELECT COUNT(*) FROM tokens WHERE agent_id=@a", ("@a", agentId)) > 0;
            ExecTx(c, tx, "INSERT OR REPLACE INTO tokens VALUES(@a,@h,@l)", ("@a", agentId), ("@h", H(token)), ("@l", lab));
            var hasMachine = Scalar<long>(c, "SELECT COUNT(*) FROM machines WHERE agent_id=@a", ("@a", agentId)) > 0;
            if (hasMachine && normalized != null)
                ExecTx(c, tx, "UPDATE machines SET position=@p WHERE agent_id=@a", ("@p", normalized), ("@a", agentId));
            HistTx(c, tx, agentId, lab, machineName, repair ? "Novo credenciamento" : "Primeiro credenciamento",
                $"{machineName}{(normalized != null ? " na posição " + normalized : "")}", "ok");
            tx.Commit();
            return new PairResult(token, 200, null);
        }
    }

    public bool CheckToken(string agentId, string token, string lab)
    {
        lock (L)
        {
            using var c = Open();
            using var m = c.CreateCommand();
            m.CommandText = "SELECT hash,lab FROM tokens WHERE agent_id=@a";
            m.Parameters.AddWithValue("@a", agentId);
            using var r = m.ExecuteReader();
            if (!r.Read()) return false;
            var stored = Encoding.ASCII.GetBytes(r.GetString(0));
            var given = Encoding.ASCII.GetBytes(H(token));
            return CryptographicOperations.FixedTimeEquals(stored, given) && string.Equals(r.GetString(1), lab, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ---------------- relatórios ----------------
    public long RequestCollect(IEnumerable<string>? agentIds)
    {
        lock (L)
        {
            using var c = Open();
            var known = Query(c, "SELECT agent_id FROM machines", r => r.GetString(0)).ToHashSet();
            var targets = (agentIds == null ? known : agentIds.Where(known.Contains)).Distinct().ToList();
            using var tx = c.BeginTransaction();
            foreach (var a in targets) ExecTx(c, tx, "INSERT OR IGNORE INTO collect_requests VALUES(@a)", ("@a", a));
            ExecTx(c, tx, "INSERT INTO collect_rounds(ts,total) VALUES(@t,@n)", ("@t", Now()), ("@n", targets.Count));
            long id;
            using (var m = c.CreateCommand()) { m.Transaction = tx; m.CommandText = "SELECT last_insert_rowid()"; id = (long)m.ExecuteScalar()!; }
            foreach (var a in targets)
                ExecTx(c, tx, "INSERT OR REPLACE INTO collect_targets(round_id,agent_id,status) VALUES(@r,@a,'aguardando')", ("@r", id), ("@a", a));
            // Coletas antigas deixam de ser acompanhadas (mantém as 30 mais recentes).
            ExecTx(c, tx, "DELETE FROM collect_targets WHERE round_id <= @r - 30", ("@r", id));
            ExecTx(c, tx, "DELETE FROM collect_rounds WHERE id <= @r - 30", ("@r", id));
            HistTx(c, tx, "", "", "", "Ação administrativa", agentIds == null ? $"Nova coleta solicitada a todos ({targets.Count})" : $"Nova coleta solicitada a {targets.Count} computador(es)");
            tx.Commit();
            return id;
        }
    }

    /// <summary>Resumo da coleta mais recente. Prazo: depois dele, quem não respondeu aparece como "sem resposta no prazo".</summary>
    public (CollectRound? round, Dictionary<string, string> perMachine) LastCollect(int recentSeconds)
    {
        lock (L)
        {
            using var c = Open();
            var r = Query(c, "SELECT id,ts,total FROM collect_rounds ORDER BY id DESC LIMIT 1",
                x => new CollectRound { Id = x.GetInt64(0), Ts = D(x, 1) ?? DateTimeOffset.UtcNow, Total = (int)Lg(x, 2) }).FirstOrDefault();
            var per = new Dictionary<string, string>();
            if (r == null) return (null, per);
            r.DeadlineSeconds = Math.Max(120, recentSeconds);
            var expired = DateTimeOffset.UtcNow - r.Ts > TimeSpan.FromSeconds(r.DeadlineSeconds);
            var rows = Query(c, "SELECT t.agent_id,t.status,m.auto_mode FROM collect_targets t LEFT JOIN machines m ON m.agent_id=t.agent_id WHERE t.round_id=@r",
                x => (x.GetString(0), S(x, 1) ?? "", x.IsDBNull(2) || x.GetInt64(2) == 1), ("@r", r.Id));
            foreach (var (agent, status, auto) in rows)
            {
                string st;
                if (status == "respondido") { st = CollectStates.Answered; r.Answered++; }
                else if (!auto) { st = CollectStates.Manual; r.Manual++; }
                else if (expired) { st = CollectStates.NoResponse; r.NoResponse++; }
                else { st = CollectStates.Waiting; r.Waiting++; }
                per[agent] = st;
            }
            r.Total = rows.Count;
            return (r, per);
        }
    }

    public void RequestCollectAll() => RequestCollect(null);

    public ReportAck Upsert(ReportDto d)
    {
        lock (L)
        {
            using var c = Open();
            using var tx = c.BeginTransaction();
            string? oIp = null, oUser = null, oName = null, oPos = null, oMac = null, oVer = null, oComm = null, oLastSeen = null, oMgh = null;
            long oSeq = -1, oAuto = 1; bool exists = false;
            using (var m = c.CreateCommand())
            {
                m.Transaction = tx;
                m.CommandText = "SELECT ip,user,last_seq,name,position,mac,version,comm_state,auto_mode,last_seen,mgh FROM machines WHERE agent_id=@a";
                m.Parameters.AddWithValue("@a", d.AgentId);
                using var r = m.ExecuteReader();
                if (r.Read())
                {
                    exists = true; oIp = S(r, 0); oUser = S(r, 1); oSeq = Lg(r, 2); oName = S(r, 3); oPos = S(r, 4); oMac = S(r, 5);
                    oVer = S(r, 6); oComm = S(r, 7); oAuto = r.IsDBNull(8) ? 1 : r.GetInt64(8); oLastSeen = S(r, 9); oMgh = S(r, 10);
                }
            }
            var pos = string.IsNullOrWhiteSpace(d.Position) ? "" : Naming.NormalizePosition(d.Position);
            var p = new (string, object?)[] { ("@a", d.AgentId), ("@l", d.Lab), ("@n", d.MachineName), ("@p", pos), ("@u", d.User), ("@i", d.Ip),
                ("@m", d.Mac), ("@t", Now()), ("@s", d.Seq), ("@v", d.AgentVersion), ("@am", d.AutoMode ? 1 : 0), ("@sn", d.SessionNote), ("@g", d.MachineGuidHash) };
            void Log(string k, string det, string result = "info") => HistTx(c, tx, d.AgentId, d.Lab, d.MachineName, k, det, result);
            void Queue() => ExecTx(c, tx, "INSERT OR REPLACE INTO veyon_queue(agent_id,status,created,updated) VALUES(@a,'pendente',@t,@t)", ("@a", d.AgentId), ("@t", Now()));
            bool stale = false;
            string? warning = null;
            if (!exists)
            {
                ExecTx(c, tx, "INSERT INTO machines(agent_id,lab,name,position,user,ip,mac,last_seen,last_seq,version,auto_mode,session_note,mgh,first_seen,comm_state) VALUES(@a,@l,@n,@p,@u,@i,@m,@t,@s,@v,@am,@sn,@g,@t,'ok')", p);
                Log("Primeiro cadastro", $"{d.MachineName} posição={pos} ip={d.Ip} mac={d.Mac}", "ok");
                Queue();
            }
            else if (d.Seq <= oSeq)
            {
                stale = true; // fora de ordem: não sobrescreve estado mais novo
                ExecTx(c, tx, "UPDATE machines SET last_seen=@t WHERE agent_id=@a", ("@a", d.AgentId), ("@t", Now()));
            }
            else
            {
                // Imagens clonadas sem Sysprep: o mesmo agente aparecendo com outro MachineGuid/MAC e outro nome em sequência.
                var recent = DateTimeOffset.TryParse(oLastSeen, out var ls) && DateTimeOffset.UtcNow - ls < TimeSpan.FromMinutes(10);
                if (recent && oMgh != null && d.MachineGuidHash != null && oMgh != d.MachineGuidHash
                    || recent && oName != null && oMac != null && d.Mac != null && oName != d.MachineName && oMac != d.Mac)
                {
                    ExecTx(c, tx, "UPDATE machines SET dup_flag=1 WHERE agent_id=@a", ("@a", d.AgentId));
                    warning = "Possível identidade duplicada (imagem clonada). Reinstale o STD nesta máquina e pareie novamente.";
                }
                ExecTx(c, tx, "UPDATE machines SET lab=@l,name=@n,position=@p,user=@u,ip=@i,mac=@m,last_seen=@t,last_seq=@s,version=@v,auto_mode=@am,session_note=@sn,mgh=COALESCE(@g,mgh),comm_state='ok' WHERE agent_id=@a", p);
                if (oIp != d.Ip) { Log("Mudança de IP", $"{oIp ?? "(nenhum)"} → {d.Ip ?? "(nenhum)"}"); Queue(); }
                if (oMac != d.Mac && oMac != null) { Log("Mudança de MAC", $"{oMac} → {d.Mac ?? "(nenhum)"}"); Queue(); }
                if (oUser != d.User) Log("Mudança de usuário", $"{oUser ?? "(sem usuário conectado)"} → {d.User ?? "(sem usuário conectado)"}");
                if (oName != d.MachineName) { Log("Alteração de nome", $"{oName} → {d.MachineName}"); Queue(); }
                if ((oPos ?? "") != pos) { Log("Alteração de posição", $"{(string.IsNullOrEmpty(oPos) ? "(sem posição)" : oPos)} → {(pos == "" ? "(sem posição)" : pos)}"); Queue(); }
                if (oVer != d.AgentVersion) Log("Atualização de versão", $"STD {oVer} → {d.AgentVersion}", "ok");
                if (oComm == "lost") Log("Comunicação recuperada", "Agente voltou a se comunicar", "ok");
                if ((oAuto == 1) != d.AutoMode) Log("Ação administrativa", d.AutoMode ? "Envio automático ligado no STD" : "STD em modo manual");
            }
            if (!string.IsNullOrEmpty(pos))
            {
                var clash = Query(c, "SELECT name FROM machines WHERE lab=@l AND agent_id<>@a AND position=@p", r => S(r, 0) ?? "", ("@l", d.Lab), ("@a", d.AgentId), ("@p", pos)).FirstOrDefault();
                if (clash != null) warning = $"Posição {pos} também usada por '{clash}'. Corrija o número em um dos computadores.";
            }
            var collect = ExecTx(c, tx, "DELETE FROM collect_requests WHERE agent_id=@a", ("@a", d.AgentId)) > 0;
            // Correlação com a coleta solicitada: um envio comum (presença, login...) só entrega o pedido ao agente;
            // a resposta é o envio seguinte feito por causa do pedido (motivo "coleta solicitada" no 2.2, "manual" nos agentes 2.1).
            var open = Query(c, "SELECT round_id,status FROM collect_targets WHERE agent_id=@a AND status IN ('aguardando','sinalizado') ORDER BY round_id DESC LIMIT 1",
                r => (r.GetInt64(0), S(r, 1) ?? ""), ("@a", d.AgentId)).FirstOrDefault();
            if (open.Item2 != null && open.Item2 != "")
            {
                var answered = d.Reason == ReportReasons.Collect || (open.Item2 == "sinalizado" && d.Reason == ReportReasons.Manual);
                if (answered)
                    ExecTx(c, tx, "UPDATE collect_targets SET status='respondido',answered=@t WHERE round_id=@r AND agent_id=@a", ("@t", Now()), ("@r", open.Item1), ("@a", d.AgentId));
                else if (collect && open.Item2 == "aguardando")
                    ExecTx(c, tx, "UPDATE collect_targets SET status='sinalizado',signaled=@t WHERE round_id=@r AND agent_id=@a", ("@t", Now()), ("@r", open.Item1), ("@a", d.AgentId));
            }
            var vq = Scalar<string>(c, "SELECT status FROM veyon_queue WHERE agent_id=@a", ("@a", d.AgentId));
            var managed = Scalar<long>(c, "SELECT COUNT(*) FROM veyon_managed WHERE agent_id=@a", ("@a", d.AgentId)) > 0;
            var update = Scalar<long>(c, "SELECT COUNT(*) FROM update_targets WHERE agent_id=@a AND status IN ('pendente','falhou-repetir')", ("@a", d.AgentId)) > 0;
            tx.Commit();
            if (warning != null) Error(d.Lab, d.AgentId, d.MachineName, "Identificação", warning);
            var vstate = VeyonStateText(vq, managed);
            return new ReportAck(true, stale, vstate == VeyonStates.Applied, VeyonStates.AvailabilityUnknown, collect, vstate, warning, update);
        }
    }

    static string VeyonStateText(string? queueStatus, bool managed) => queueStatus switch
    {
        "pendente" => VeyonStates.Pending,
        "erro" => VeyonStates.Error,
        "conflito" => VeyonStates.Conflict,
        "aplicado" => VeyonStates.Applied,
        _ => managed ? VeyonStates.Applied : VeyonStates.NotManaged
    };

    // ---------------- visão geral ----------------
    public List<MachineRow> Machines(string nameTemplate, int recentSeconds)
    {
        var (_, collectState) = LastCollect(recentSeconds);
        lock (L)
        {
            using var c = Open();
            var collect = Query(c, "SELECT agent_id FROM collect_requests", r => r.GetString(0)).ToHashSet();
            var queue = Query(c, "SELECT agent_id,status FROM veyon_queue", r => (r.GetString(0), S(r, 1))).ToDictionary(x => x.Item1, x => x.Item2);
            var managed = Query(c, "SELECT agent_id FROM veyon_managed", r => r.GetString(0)).ToHashSet();
            var errs = Query(c, "SELECT agent_id,COUNT(*) FROM errors WHERE resolved=0 GROUP BY agent_id", r => (S(r, 0) ?? "", r.GetInt64(1))).ToDictionary(x => x.Item1, x => (int)x.Item2);
            var now = DateTimeOffset.UtcNow;
            return Query(c, "SELECT agent_id,lab,name,position,user,ip,mac,last_seen,version,friendly_override,auto_mode,dup_flag,session_note,first_seen FROM machines ORDER BY lab,position,name",
                r =>
                {
                    var row = new MachineRow
                    {
                        AgentId = r.GetString(0), Lab = S(r, 1) ?? "", WindowsName = S(r, 2) ?? "", Position = S(r, 3) ?? "", User = S(r, 4),
                        Ip = S(r, 5), Mac = S(r, 6), LastSeen = D(r, 7), Version = S(r, 8) ?? "", FriendlyOverride = S(r, 9),
                        AutoMode = r.IsDBNull(10) || r.GetInt64(10) == 1, Duplicate = !r.IsDBNull(11) && r.GetInt64(11) == 1, SessionNote = S(r, 12),
                        FirstSeen = D(r, 13)
                    };
                    row.DisplayName = string.IsNullOrWhiteSpace(row.FriendlyOverride)
                        ? Naming.Friendly(nameTemplate, row.Lab, row.Position, row.WindowsName)
                        : Naming.Sanitize(row.FriendlyOverride);
                    row.Recent = row.LastSeen != null && now - row.LastSeen.Value <= TimeSpan.FromSeconds(recentSeconds);
                    row.CollectPending = collect.Contains(row.AgentId);
                    row.CollectStatus = collectState.TryGetValue(row.AgentId, out var cs) ? cs : "";
                    row.AgentState = row.Recent ? "Comunicação recente"
                        : row.AutoMode ? "Sem comunicação recente"
                        : "Modo manual: aguardando envio manual";
                    queue.TryGetValue(row.AgentId, out var q);
                    row.VeyonState = VeyonStateText(q, managed.Contains(row.AgentId));
                    row.OpenErrors = errs.TryGetValue(row.AgentId, out var e) ? e : 0;
                    return row;
                });
        }
    }

    /// <summary>Transições de comunicação para o histórico (uma entrada por transição, não por sinal).</summary>
    public void MarkLost(int recentSeconds)
    {
        var cut = DateTimeOffset.UtcNow.AddSeconds(-recentSeconds).ToString("o");
        lock (L)
        {
            using var c = Open();
            var lost = Query(c, "SELECT agent_id,lab,name,auto_mode FROM machines WHERE comm_state='ok' AND last_seen < @c",
                r => (r.GetString(0), S(r, 1) ?? "", S(r, 2) ?? "", r.IsDBNull(3) || r.GetInt64(3) == 1), ("@c", cut));
            foreach (var (a, lab, name, auto) in lost)
            {
                Exec(c, "UPDATE machines SET comm_state='lost' WHERE agent_id=@a", ("@a", a));
                HistTx(c, null, a, lab, name, "Perda de comunicação",
                    auto ? "Sem comunicação recente (não indica que o computador esteja desligado)" : "Modo manual: sem envios periódicos");
            }
        }
    }

    public void SetFriendlyOverride(string agentId, string? name)
    {
        lock (L)
        {
            using var c = Open();
            Exec(c, "UPDATE machines SET friendly_override=@f WHERE agent_id=@a", ("@f", string.IsNullOrWhiteSpace(name) ? null : Naming.Sanitize(name)), ("@a", agentId));
            Exec(c, "INSERT OR REPLACE INTO veyon_queue(agent_id,status,created,updated) VALUES(@a,'pendente',@t,@t)", ("@a", agentId), ("@t", Now()));
            HistTx(c, null, agentId, "", "", "Ação administrativa", $"Nome exibido alterado para '{name}'");
        }
    }

    public void QueueAll(string lab)
    {
        lock (L)
        {
            using var c = Open();
            Exec(c, "INSERT OR REPLACE INTO veyon_queue(agent_id,status,created,updated) SELECT agent_id,'pendente',@t,@t FROM machines WHERE lab=@l", ("@t", Now()), ("@l", lab));
        }
    }

    public void RemoveMachine(string agentId)
    {
        lock (L)
        {
            using var c = Open();
            var info = Query(c, "SELECT lab,name FROM machines WHERE agent_id=@a", r => (S(r, 0) ?? "", S(r, 1) ?? ""), ("@a", agentId)).FirstOrDefault();
            using var tx = c.BeginTransaction();
            foreach (var t in new[] { "machines", "tokens", "veyon_queue", "collect_requests" })
                ExecTx(c, tx, $"DELETE FROM {t} WHERE agent_id=@a", ("@a", agentId));
            ExecTx(c, tx, "UPDATE errors SET resolved=1 WHERE agent_id=@a", ("@a", agentId));
            HistTx(c, tx, agentId, info.Item1 ?? "", info.Item2 ?? "", "Ação administrativa", "Registro e credencial removidos pelo administrador (histórico preservado; registro no Veyon não foi apagado)");
            tx.Commit();
        }
        RemoveFromMaps(agentId);
    }

    // ---------------- mapa ----------------
    public LabMap GetMap(string lab)
    {
        lock (L)
        {
            using var c = Open();
            var json = Scalar<string>(c, "SELECT json FROM maps WHERE lab=@l", ("@l", lab));
            var map = json == null ? null : JsonSerializer.Deserialize<LabMap>(json, IpcJson.Options);
            return map ?? new LabMap { Lab = lab };
        }
    }

    public void SaveMap(LabMap map)
    {
        var seen = new HashSet<string>();
        if (map.Rows.Count > 50) throw new InvalidDataException("Máximo de 50 fileiras.");
        foreach (var r in map.Rows)
        {
            if (r.Slots.Count > 60) throw new InvalidDataException("Máximo de 60 posições por fileira.");
            r.Name = (r.Name ?? "").Trim();
            if (r.Name.Length > 40) r.Name = r.Name[..40];
            foreach (var s in r.Slots)
                if (s != null && !seen.Add(s)) throw new InvalidDataException("A mesma máquina aparece duas vezes no mapa.");
        }
        lock (L)
        {
            using var c = Open();
            Exec(c, "INSERT OR REPLACE INTO maps(lab,json,updated) VALUES(@l,@j,@t)", ("@l", map.Lab), ("@j", JsonSerializer.Serialize(map, IpcJson.Options)), ("@t", Now()));
            HistTx(c, null, "", map.Lab, "", "Ação administrativa", "Mapa do laboratório salvo");
        }
    }

    void RemoveFromMaps(string agentId)
    {
        List<(string lab, string json)> maps;
        lock (L) { using var c = Open(); maps = Query(c, "SELECT lab,json FROM maps", r => (r.GetString(0), r.GetString(1))); }
        foreach (var (lab, json) in maps)
        {
            var m = JsonSerializer.Deserialize<LabMap>(json, IpcJson.Options);
            if (m == null) continue;
            var changed = false;
            foreach (var r in m.Rows)
                for (int i = 0; i < r.Slots.Count; i++)
                    if (r.Slots[i] == agentId) { r.Slots[i] = null; changed = true; }
            if (changed) lock (L) { using var c = Open(); Exec(c, "UPDATE maps SET json=@j WHERE lab=@l", ("@j", JsonSerializer.Serialize(m, IpcJson.Options)), ("@l", lab)); }
        }
    }

    // ---------------- Veyon ----------------
    public sealed record ManagedRecord(string AgentId, string Lab, string Name, string Host, string Mac, string Location);

    public Dictionary<string, ManagedRecord> Managed(string lab)
    {
        lock (L)
        {
            using var c = Open();
            return Query(c, "SELECT agent_id,lab,name,host,mac,location FROM veyon_managed WHERE lab=@l",
                r => new ManagedRecord(r.GetString(0), S(r, 1) ?? "", S(r, 2) ?? "", S(r, 3) ?? "", S(r, 4) ?? "", S(r, 5) ?? ""), ("@l", lab))
                .ToDictionary(x => x.AgentId);
        }
    }

    public Dictionary<string, string> QueueStatus()
    {
        lock (L) { using var c = Open(); return Query(c, "SELECT agent_id,status FROM veyon_queue", r => (r.GetString(0), S(r, 1) ?? "")).ToDictionary(x => x.Item1, x => x.Item2); }
    }

    public void VeyonResult(string agentId, string lab, string machine, string outcome, string detail, ManagedRecord? applied)
    {
        lock (L)
        {
            using var c = Open();
            using var tx = c.BeginTransaction();
            var status = outcome switch { "Integrado" or "Já atualizado" => "aplicado", "Conflito" => "conflito", _ => "erro" };
            ExecTx(c, tx, "INSERT OR REPLACE INTO veyon_queue(agent_id,status,created,updated,last_error) VALUES(@a,@s,COALESCE((SELECT created FROM veyon_queue WHERE agent_id=@a),@t),@t,@e)",
                ("@a", agentId), ("@s", status), ("@t", Now()), ("@e", status == "aplicado" ? null : detail));
            if (applied != null)
                ExecTx(c, tx, "INSERT OR REPLACE INTO veyon_managed VALUES(@a,@l,@n,@h,@m,@loc,@t)",
                    ("@a", agentId), ("@l", applied.Lab), ("@n", applied.Name), ("@h", applied.Host), ("@m", applied.Mac), ("@loc", applied.Location), ("@t", Now()));
            ExecTx(c, tx, "INSERT INTO batch_results(ts,lab,agent_id,machine,outcome,detail) VALUES(@t,@l,@a,@m,@o,@d)",
                ("@t", Now()), ("@l", lab), ("@a", agentId), ("@m", machine), ("@o", outcome), ("@d", detail));
            if (outcome != "Já atualizado")
                HistTx(c, tx, agentId, lab, machine, "Integração Veyon", $"{outcome}: {detail}", outcome == "Integrado" ? "ok" : outcome == "Conflito" ? "aviso" : "erro");
            tx.Commit();
        }
        if (outcome is "Integrado" or "Já atualizado") ResolveErrors(agentId, "Veyon");
        else Error(lab, agentId, machine, "Veyon", detail);
    }

    public void AddBackup(string file, string note)
    {
        lock (L)
        {
            using var c = Open();
            Exec(c, "INSERT INTO veyon_backups(ts,file,note) VALUES(@t,@f,@n)", ("@t", Now()), ("@f", file), ("@n", note));
            HistTx(c, null, "", "", "", "Integração Veyon", "Backup da configuração do Veyon: " + Path.GetFileName(file), "ok");
        }
    }

    public List<BackupRow> Backups()
    {
        lock (L)
        {
            using var c = Open();
            return Query(c, "SELECT id,ts,file,note FROM veyon_backups ORDER BY id DESC LIMIT 200",
                r => new BackupRow { Id = r.GetInt64(0), Ts = D(r, 1) ?? default, File = S(r, 2) ?? "", Note = S(r, 3) ?? "" });
        }
    }

    public BackupRow? Backup(long id) => Backups().FirstOrDefault(b => b.Id == id);

    // ---------------- professores parceiros ----------------
    public List<PeerRow> Peers()
    {
        lock (L)
        {
            using var c = Open();
            return Query(c, "SELECT id,name,lab,url,enabled,last_status,last_contact FROM peers ORDER BY lab",
                r => new PeerRow { Id = r.GetInt64(0), Name = S(r, 1) ?? "", Lab = S(r, 2) ?? "", Url = S(r, 3) ?? "", Enabled = Lg(r, 4) == 1, LastStatus = S(r, 5) ?? "", LastContact = D(r, 6) });
        }
    }

    public (PeerRow row, string certSha256, string? tokenProtected)? Peer(long id)
    {
        lock (L)
        {
            using var c = Open();
            return Query(c, "SELECT id,name,lab,url,enabled,last_status,last_contact,cert_sha256,token_protected FROM peers WHERE id=@i",
                r => ((PeerRow row, string certSha256, string? tokenProtected)?)(new PeerRow { Id = r.GetInt64(0), Name = S(r, 1) ?? "", Lab = S(r, 2) ?? "", Url = S(r, 3) ?? "", Enabled = Lg(r, 4) == 1, LastStatus = S(r, 5) ?? "", LastContact = D(r, 6) },
                    S(r, 7) ?? "", S(r, 8)), ("@i", id)).FirstOrDefault();
        }
    }

    public void AddPeer(string name, string lab, string url, string certSha256, string tokenProtected)
    {
        lock (L)
        {
            using var c = Open();
            Exec(c, "DELETE FROM peers WHERE lab=@l", ("@l", lab));
            Exec(c, "INSERT INTO peers(name,lab,url,cert_sha256,token_protected,enabled,added,last_status) VALUES(@n,@l,@u,@c,@t,1,@d,'Autorizado')",
                ("@n", name), ("@l", lab), ("@u", url), ("@c", certSha256), ("@t", tokenProtected), ("@d", Now()));
            HistTx(c, null, "", lab, name, "Ação administrativa", $"Professor do laboratório {lab} ({name}) autorizado para integração em lote");
        }
    }

    public void SetPeerEnabled(long id, bool enabled)
    {
        lock (L) { using var c = Open(); Exec(c, "UPDATE peers SET enabled=@e WHERE id=@i", ("@e", enabled ? 1 : 0), ("@i", id)); }
    }

    public void RemovePeer(long id)
    {
        lock (L) { using var c = Open(); Exec(c, "DELETE FROM peers WHERE id=@i", ("@i", id)); }
    }

    public void PeerStatus(long id, string status)
    {
        lock (L) { using var c = Open(); Exec(c, "UPDATE peers SET last_status=@s,last_contact=@t WHERE id=@i", ("@s", status), ("@t", Now()), ("@i", id)); }
    }

    public string IssuePeerToken(string callerName, string callerLab)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        lock (L)
        {
            using var c = Open();
            Exec(c, "INSERT INTO peer_tokens VALUES(@h,@n,@l,@t)", ("@h", H(token)), ("@n", callerName), ("@l", callerLab), ("@t", Now()));
            HistTx(c, null, "", callerLab, callerName, "Ação administrativa", $"Professor central {callerName} (lab {callerLab}) autorizado a solicitar integrações neste laboratório");
        }
        return token;
    }

    public string? CheckPeerToken(string token)
    {
        lock (L) { using var c = Open(); return Scalar<string>(c, "SELECT caller_name FROM peer_tokens WHERE hash=@h", ("@h", H(token))); }
    }

    public bool ConsumeCode(string code)
    {
        lock (L)
        {
            using var c = Open();
            Exec(c, "DELETE FROM codes WHERE expires < @n", ("@n", Now()));
            return Exec(c, "DELETE FROM codes WHERE hash=@h", ("@h", H(code))) > 0;
        }
    }

    // ---------------- atualizações ----------------
    public long AddUpdate(string product, string version, string file, string sha, long size, string manifestJson, string signature, IEnumerable<string> agentIds)
    {
        lock (L)
        {
            using var c = Open();
            using var tx = c.BeginTransaction();
            ExecTx(c, tx, "INSERT INTO updates(product,version,file,sha256,size,manifest,signature,created) VALUES(@p,@v,@f,@s,@z,@m,@g,@t)",
                ("@p", product), ("@v", version), ("@f", file), ("@s", sha), ("@z", size), ("@m", manifestJson), ("@g", signature), ("@t", Now()));
            long id;
            using (var m = c.CreateCommand()) { m.Transaction = tx; m.CommandText = "SELECT last_insert_rowid()"; id = (long)m.ExecuteScalar()!; }
            // Uma distribuição nova substitui pendências antigas do mesmo agente.
            foreach (var a in agentIds)
            {
                ExecTx(c, tx, "UPDATE update_targets SET status='substituída' WHERE agent_id=@a AND status IN ('pendente','falhou-repetir','baixando')", ("@a", a));
                ExecTx(c, tx, "INSERT OR REPLACE INTO update_targets VALUES(@u,@a,'pendente','Aguardando o agente se comunicar',@t)", ("@u", id), ("@a", a), ("@t", Now()));
            }
            HistTx(c, tx, "", "", "", "Ação administrativa", $"Atualização {product} {version} distribuída para {agentIds.Count()} computador(es)");
            tx.Commit();
            return id;
        }
    }

    public (long id, string manifest, string signature, string file)? PendingUpdateFor(string agentId)
    {
        lock (L)
        {
            using var c = Open();
            return Query(c, "SELECT u.id,u.manifest,u.signature,u.file FROM update_targets t JOIN updates u ON u.id=t.update_id WHERE t.agent_id=@a AND t.status IN ('pendente','falhou-repetir','baixando') ORDER BY u.id DESC LIMIT 1",
                r => ((long, string, string, string)?)(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3)), ("@a", agentId)).FirstOrDefault();
        }
    }

    public bool IsTarget(long updateId, string agentId)
    {
        lock (L) { using var c = Open(); return Scalar<long>(c, "SELECT COUNT(*) FROM update_targets WHERE update_id=@u AND agent_id=@a AND status NOT IN ('substituída')", ("@u", updateId), ("@a", agentId)) > 0; }
    }

    public void UpdateTarget(long updateId, string agentId, string status, string detail)
    {
        lock (L)
        {
            using var c = Open();
            Exec(c, "UPDATE update_targets SET status=@s,detail=@d,updated=@t WHERE update_id=@u AND agent_id=@a",
                ("@s", status), ("@d", detail), ("@t", Now()), ("@u", updateId), ("@a", agentId));
            if (status is "concluída" or "falhou")
            {
                var info = Query(c, "SELECT lab,name FROM machines WHERE agent_id=@a", r => (S(r, 0) ?? "", S(r, 1) ?? ""), ("@a", agentId)).FirstOrDefault();
                HistTx(c, null, agentId, info.Item1 ?? "", info.Item2 ?? "", "Atualização de versão", detail, status == "concluída" ? "ok" : "erro");
            }
        }
    }

    public void RetryFailedUpdates(long updateId)
    {
        lock (L) { using var c = Open(); Exec(c, "UPDATE update_targets SET status='falhou-repetir',detail='Nova tentativa solicitada',updated=@t WHERE update_id=@u AND status='falhou'", ("@t", Now()), ("@u", updateId)); }
    }

    public List<UpdateRow> Updates()
    {
        lock (L)
        {
            using var c = Open();
            var names = Query(c, "SELECT agent_id,name,position FROM machines", r => (r.GetString(0), $"{S(r, 2)} {S(r, 1)}".Trim())).ToDictionary(x => x.Item1, x => x.Item2);
            var ups = Query(c, "SELECT id,product,version,created FROM updates ORDER BY id DESC LIMIT 50",
                r => new UpdateRow { Id = r.GetInt64(0), Product = S(r, 1) ?? "", Version = S(r, 2) ?? "", Created = D(r, 3) ?? default });
            foreach (var u in ups)
            {
                u.Targets = Query(c, "SELECT agent_id,status,detail,updated FROM update_targets WHERE update_id=@u",
                    r => new UpdateTargetRow { AgentId = r.GetString(0), Status = S(r, 1) ?? "", Detail = S(r, 2) ?? "", Updated = D(r, 3) }, ("@u", u.Id));
                foreach (var t in u.Targets) t.Machine = names.TryGetValue(t.AgentId, out var n) ? n : "(removido)";
                u.Pending = u.Targets.Count(t => t.Status is "pendente" or "falhou-repetir");
                u.Downloading = u.Targets.Count(t => t.Status is "baixando" or "aplicando" or "instalando");
                u.Done = u.Targets.Count(t => t.Status == "concluída");
                u.Failed = u.Targets.Count(t => t.Status == "falhou");
            }
            return ups;
        }
    }
}
