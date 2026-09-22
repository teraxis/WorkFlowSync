using System.Globalization;
using Microsoft.Data.Sqlite;
using WorkFlowSync.Core.Model;

namespace WorkFlowSync.Core.State;

/// <summary>
/// SQLite-backed memory of the mirror (docs/product/features/state-and-tombstones.md).
/// Loaded into a dictionary per pair at the start of a pass; changes are written in transactions.
///
/// Schema v4 (docs/plan-etap5.md §4.4): times are INTEGER Unix milliseconds, not ISO text. On a million
/// rows the old TEXT columns cost ~135 bytes each and five DateTimeOffset.Parse calls per row on every
/// load. Millisecond precision is far below the 2 s tolerance the planner compares mtimes with.
/// </summary>
public sealed class StateStore : IDisposable
{
    public const int SchemaVersion = 4;

    /// <summary>Rows per transaction when writing in batches; also the checkpoint size during a pass.</summary>
    public const int BatchRows = 2000;

    private readonly SqliteConnection _conn;

    public string Path { get; }

    /// <summary>Approval queue store for moderated target folders (schema v3).</summary>
    public PendingStore Pending { get; }

    /// <summary>Set when the database was migrated from an older schema on open; points at the backup copy.</summary>
    public string? MigratedFromBackup { get; private set; }

    public StateStore(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        var dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        // Pooling=false: release the file handle on Dispose (otherwise the pool keeps state.db open after a pass).
        _conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        _conn.Open();
        Exec("""
            PRAGMA busy_timeout=5000;
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA temp_store=MEMORY;
            PRAGMA mmap_size=268435456;
            PRAGMA cache_size=-32000;
            """);
        Exec("CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT);");
        EnsureSchema();
        Pending = new PendingStore(_conn);
    }

    // ------------------------------------------------------------------ schema

    private const string CreateEntriesV2 = """
        CREATE TABLE IF NOT EXISTS {0} (
          pair TEXT NOT NULL, path TEXT NOT NULL, kind INTEGER NOT NULL, status INTEGER NOT NULL,
          first_seen INTEGER NOT NULL, last_seen INTEGER NOT NULL,
          src_size INTEGER, src_mtime INTEGER, copied_size INTEGER, copied_mtime INTEGER, copied_at INTEGER,
          via_link INTEGER NOT NULL DEFAULT 0, status_changed INTEGER,
          remote_id TEXT, remote_version TEXT,
          PRIMARY KEY (pair, path)) STRICT, WITHOUT ROWID;
        """;

    private const string CreatePendingV3 = """
        CREATE TABLE IF NOT EXISTS pending (
          id INTEGER PRIMARY KEY,
          pair TEXT NOT NULL,
          path TEXT NOT NULL,
          from_path TEXT,
          change INTEGER NOT NULL,
          kind INTEGER NOT NULL,
          src_size INTEGER, src_mtime INTEGER,
          dst_size INTEGER, dst_mtime INTEGER,
          detected INTEGER NOT NULL,
          status INTEGER NOT NULL,
          resolved INTEGER,
          version_path TEXT
        ) STRICT;
        CREATE INDEX IF NOT EXISTS pending_open ON pending (pair, status, detected);
        CREATE UNIQUE INDEX IF NOT EXISTS pending_one_per_path ON pending (pair, path) WHERE status = 0;
        """;

    private void EnsureSchema()
    {
        var found = ReadSchemaVersion();
        switch (found)
        {
            case null:
                Exec(string.Format(CultureInfo.InvariantCulture, CreateEntriesV2, "entries"));
                Exec(CreatePendingV3);
                SetMeta("schema_version", SchemaVersion.ToString(CultureInfo.InvariantCulture));
                break;

            case 1:
                MigrateV1ToV2();
                MigrateV2ToV3(backupRequired: false);
                MigrateV3ToV4(backupRequired: false);
                break;

            case 2:
                MigrateV2ToV3(backupRequired: true);
                MigrateV3ToV4(backupRequired: false);
                break;

            case 3:
                MigrateV3ToV4(backupRequired: true);
                break;

            case SchemaVersion:
                Exec(string.Format(CultureInfo.InvariantCulture, CreateEntriesV2, "entries"));
                Exec(CreatePendingV3);
                break;

            default:
                throw new InvalidDataException(
                    $"{Path}: state database schema is version {found}, this build understands {SchemaVersion}. Use a newer WorkFlowSync.");
        }
    }

    /// <summary>Version of the database on disk: null for a brand new file, otherwise 1 (legacy) or higher.</summary>
    private int? ReadSchemaVersion()
    {
        var recorded = GetMeta("schema_version");
        if (recorded is not null && int.TryParse(recorded, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) return v;
        // A database written before the meta row existed still has the table; anything else is a fresh file.
        return TableExists("entries") ? 1 : null;
    }

    private bool TableExists(string name)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $n";
        cmd.Parameters.AddWithValue("$n", name);
        return cmd.ExecuteScalar() is not null;
    }

    /// <summary>
    /// Rewrites the v1 table (ISO-text times) into v2 (Unix milliseconds), streaming row by row so a huge
    /// database does not have to fit in memory. A consistent backup is taken first — the one operation in
    /// this product that could lose the whole history if it went wrong.
    /// </summary>
    private void MigrateV1ToV2()
    {
        var backup = Path + ".v1-backup";
        if (File.Exists(backup)) File.Delete(backup);
        using (var vacuum = _conn.CreateCommand())
        {
            vacuum.CommandText = "VACUUM INTO $dest";     // consistent copy from the live connection, WAL and all
            vacuum.Parameters.AddWithValue("$dest", backup);
            vacuum.ExecuteNonQuery();
        }
        MigratedFromBackup = backup;

        using var tx = _conn.BeginTransaction();
        Exec(string.Format(CultureInfo.InvariantCulture, CreateEntriesV2, "entries_v2"), tx);

        using (var insert = _conn.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO entries_v2 (pair, path, kind, status, first_seen, last_seen, src_size, src_mtime,
                                        copied_size, copied_mtime, via_link, status_changed, remote_id, remote_version)
                VALUES ($pair, $path, $kind, $status, $first_seen, $last_seen, $src_size, $src_mtime,
                        $copied_size, $copied_mtime, $via_link, $status_changed, NULL, NULL)
                """;
            var p = AddEntryParameters(insert);

            using var read = _conn.CreateCommand();
            read.Transaction = tx;
            read.CommandText = "SELECT pair, path, kind, status, first_seen, last_seen, src_size, src_mtime, copied_size, copied_mtime, via_link, status_changed FROM entries";
            using var r = read.ExecuteReader();
            while (r.Read())
            {
                // Parsing in C# rather than in SQL on purpose: the "O" format carries seven fractional
                // digits, which SQLite's own date functions do not accept.
                p.Pair.Value = r.GetString(0);
                p.PathValue.Value = r.GetString(1);
                p.Kind.Value = r.GetInt32(2);
                p.Status.Value = r.GetInt32(3);
                p.First.Value = ToUnixMs(ParseLegacyTime(r.GetString(4)));
                p.Last.Value = ToUnixMs(ParseLegacyTime(r.GetString(5)));
                p.SrcSize.Value = r.IsDBNull(6) ? DBNull.Value : r.GetInt64(6);
                p.SrcMtime.Value = r.IsDBNull(7) ? DBNull.Value : ToUnixMs(ParseLegacyTime(r.GetString(7)));
                p.CopiedSize.Value = r.IsDBNull(8) ? DBNull.Value : r.GetInt64(8);
                p.CopiedMtime.Value = r.IsDBNull(9) ? DBNull.Value : ToUnixMs(ParseLegacyTime(r.GetString(9)));
                p.ViaLink.Value = r.GetInt32(10);
                p.StatusChanged.Value = r.IsDBNull(11) ? DBNull.Value : ToUnixMs(ParseLegacyTime(r.GetString(11)));
                insert.ExecuteNonQuery();
            }
        }

        Exec("DROP TABLE entries; ALTER TABLE entries_v2 RENAME TO entries;", tx);
        tx.Commit();
        SetMeta("schema_version", "2");
        // Dropping the old table only frees pages inside the file; without this the migrated database is
        // BIGGER than the one it replaced (measured: 409 MB vs 279 MB on a million rows before VACUUM).
        Exec("VACUUM;");
    }

    /// <summary>
    /// Migrates schema v2 to v3 by creating the <c>pending</c> table and its indexes.
    /// Takes a consistent backup (<c>.v2-backup</c>) when requested.
    /// </summary>
    private void MigrateV2ToV3(bool backupRequired)
    {
        if (backupRequired)
        {
            var backup = Path + ".v2-backup";
            if (File.Exists(backup)) File.Delete(backup);
            using (var vacuum = _conn.CreateCommand())
            {
                vacuum.CommandText = "VACUUM INTO $dest";
                vacuum.Parameters.AddWithValue("$dest", backup);
                vacuum.ExecuteNonQuery();
            }
            MigratedFromBackup = backup;
        }

        using var tx = _conn.BeginTransaction();
        Exec(CreatePendingV3, tx);
        tx.Commit();
        SetMeta("schema_version", "3");
    }

    /// <summary>Adds the target-copy timestamp used as the primary ordering key for disk rotation.</summary>
    private void MigrateV3ToV4(bool backupRequired)
    {
        if (backupRequired)
        {
            var backup = Path + ".v3-backup";
            if (File.Exists(backup)) File.Delete(backup);
            using var vacuum = _conn.CreateCommand();
            vacuum.CommandText = "VACUUM INTO $dest";
            vacuum.Parameters.AddWithValue("$dest", backup);
            vacuum.ExecuteNonQuery();
            MigratedFromBackup = backup;
        }

        if (!ColumnExists("entries", "copied_at"))
            Exec("ALTER TABLE entries ADD COLUMN copied_at INTEGER;");
        SetMeta("schema_version", SchemaVersion.ToString(CultureInfo.InvariantCulture));
    }

    private bool ColumnExists(string table, string column)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            if (reader.GetString(1).Equals(column, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // ------------------------------------------------------------------ reading

    public Dictionary<string, StateEntry> Load(string pair) => Read(pair, null);

    /// <summary>
    /// Loads only the rows for one directory and everything below it — what a partial pass needs
    /// (docs/plan-etap5.md §4.2). Empty or null scope means the whole pair.
    ///
    /// The range walks the primary key, which SQLite compares byte by byte, while paths are matched
    /// case-insensitively everywhere else. A directory whose CASE changed since the last pass is therefore
    /// invisible to a scoped read; the full pass picks it up. That is the intended division of labour:
    /// a partial pass is best-effort, the full pass is the truth.
    /// </summary>
    public Dictionary<string, StateEntry> LoadScope(string pair, string? relativeDir)
    {
        var dir = relativeDir?.Trim().Trim('\\', '/');
        return string.IsNullOrEmpty(dir) ? Read(pair, null) : Read(pair, dir);
    }

    private Dictionary<string, StateEntry> Read(string pair, string? scopeDir)
    {
        const string Columns = "path, kind, status, first_seen, last_seen, src_size, src_mtime, copied_size, copied_mtime, copied_at, via_link, status_changed";
        var dict = new Dictionary<string, StateEntry>(StringComparer.OrdinalIgnoreCase);
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = scopeDir is null
            ? $"SELECT {Columns} FROM entries WHERE pair = $pair"
            // Two index lookups joined by UNION ALL, not one OR: an OR over the primary key makes SQLite fall
            // back to a full table scan (measured: 394 ms for 1000 rows out of a million, versus ~1 ms here).
            // '\' is 0x5C and ']' is 0x5D, so [dir\ , dir]) is exactly "the directory's children, all depths" —
            // and a sibling like "dir-архів" stays outside, which a plain [dir, dir]) range would not manage.
            : $"SELECT {Columns} FROM entries WHERE pair = $pair AND path = $dir " +
              $"UNION ALL SELECT {Columns} FROM entries WHERE pair = $pair AND path >= $lo AND path < $hi";
        cmd.Parameters.AddWithValue("$pair", pair);
        if (scopeDir is not null)
        {
            cmd.Parameters.AddWithValue("$dir", scopeDir);
            cmd.Parameters.AddWithValue("$lo", scopeDir + "\\");
            cmd.Parameters.AddWithValue("$hi", scopeDir + "]");
        }

        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var e = new StateEntry
            {
                PairName = pair,
                RelativePath = r.GetString(0),
                Kind = (EntryKind)r.GetInt32(1),
                Status = (EntryStatus)r.GetInt32(2),
                FirstSeenUtc = FromUnixMs(r.GetInt64(3)),
                LastSeenUtc = FromUnixMs(r.GetInt64(4)),
                SourceSize = r.IsDBNull(5) ? null : r.GetInt64(5),
                SourceMtimeUtc = r.IsDBNull(6) ? null : FromUnixMs(r.GetInt64(6)),
                CopiedSize = r.IsDBNull(7) ? null : r.GetInt64(7),
                CopiedMtimeUtc = r.IsDBNull(8) ? null : FromUnixMs(r.GetInt64(8)),
                CopiedAtUtc = r.IsDBNull(9) ? null : FromUnixMs(r.GetInt64(9)),
                ViaLink = r.GetInt32(10) != 0,
                StatusChangedUtc = r.IsDBNull(11) ? null : FromUnixMs(r.GetInt64(11)),
            };
            dict[e.RelativePath] = e;
        }
        return dict;
    }

    /// <summary>One line of the «recently appeared» list (docs/plan-etap5.md §5.6).</summary>
    public sealed record RecentItem(string Pair, string RelativePath, DateTimeOffset FirstSeenUtc, long? Size, EntryKind Kind);

    /// <summary>
    /// What has appeared lately, newest first, across every pair.
    ///
    /// Deliberately a query over <c>entries</c> rather than a separate event log: <c>first_seen</c> already
    /// records the moment an item was first observed and never changes afterwards (invariant 5), so the answer
    /// is exact and no second copy of the truth has to be kept in step. Directories are left out — nobody
    /// wants "a folder appeared" in a list of new documents.
    /// </summary>
    public IReadOnlyList<RecentItem> RecentlyAdded(int limit = 50, DateTimeOffset? since = null)
    {
        var items = new List<RecentItem>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            "SELECT pair, path, first_seen, src_size FROM entries " +
            "WHERE kind = $file AND status = $active" + (since is null ? "" : " AND first_seen >= $since") +
            " ORDER BY first_seen DESC, path LIMIT $limit";
        cmd.Parameters.AddWithValue("$file", (int)EntryKind.File);
        cmd.Parameters.AddWithValue("$active", (int)EntryStatus.Active);
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
        if (since is { } s) cmd.Parameters.AddWithValue("$since", ToUnixMs(s));

        using var r = cmd.ExecuteReader();
        while (r.Read())
            items.Add(new RecentItem(r.GetString(0), r.GetString(1), FromUnixMs(r.GetInt64(2)),
                r.IsDBNull(3) ? null : r.GetInt64(3), EntryKind.File));
        return items;
    }

    /// <summary>How many files first appeared since the given moment — the number a notification announces.</summary>
    public int CountAddedSince(DateTimeOffset since)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM entries WHERE kind = $file AND status = $active AND first_seen >= $since";
        cmd.Parameters.AddWithValue("$file", (int)EntryKind.File);
        cmd.Parameters.AddWithValue("$active", (int)EntryStatus.Active);
        cmd.Parameters.AddWithValue("$since", ToUnixMs(since));
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public int Count(string pair)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM entries WHERE pair = $pair";
        cmd.Parameters.AddWithValue("$pair", pair);
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public Dictionary<EntryStatus, int> CountByStatus(string pair)
    {
        var res = new Dictionary<EntryStatus, int>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT status, COUNT(*) FROM entries WHERE pair = $pair GROUP BY status";
        cmd.Parameters.AddWithValue("$pair", pair);
        using var r = cmd.ExecuteReader();
        while (r.Read()) res[(EntryStatus)r.GetInt32(0)] = r.GetInt32(1);
        return res;
    }

    // ------------------------------------------------------------------ writing

    /// <summary>
    /// Upserts all given entries, committing every <see cref="BatchRows"/> rows. Batching keeps a first
    /// pass over a million files from building one enormous transaction, and means an interrupted pass
    /// keeps the work it already did (docs/plan-etap5.md §4.4, point 4).
    /// </summary>
    public void Upsert(IEnumerable<StateEntry> entries)
    {
        var tx = _conn.BeginTransaction();
        var cmd = _conn.CreateCommand();
        try
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO entries (pair, path, kind, status, first_seen, last_seen, src_size, src_mtime,
                                     copied_size, copied_mtime, copied_at, via_link, status_changed)
                VALUES ($pair, $path, $kind, $status, $first_seen, $last_seen, $src_size, $src_mtime,
                        $copied_size, $copied_mtime, $copied_at, $via_link, $status_changed)
                ON CONFLICT(pair, path) DO UPDATE SET
                  kind = excluded.kind, status = excluded.status, first_seen = excluded.first_seen, last_seen = excluded.last_seen,
                  src_size = excluded.src_size, src_mtime = excluded.src_mtime, copied_size = excluded.copied_size,
                  copied_mtime = excluded.copied_mtime, copied_at = excluded.copied_at,
                  via_link = excluded.via_link, status_changed = excluded.status_changed
                """;
            var p = AddEntryParameters(cmd);

            var inBatch = 0;
            foreach (var e in entries)
            {
                p.Pair.Value = e.PairName;
                p.PathValue.Value = e.RelativePath;
                p.Kind.Value = (int)e.Kind;
                p.Status.Value = (int)e.Status;
                p.First.Value = ToUnixMs(e.FirstSeenUtc);
                p.Last.Value = ToUnixMs(e.LastSeenUtc);
                p.SrcSize.Value = (object?)e.SourceSize ?? DBNull.Value;
                p.SrcMtime.Value = e.SourceMtimeUtc is { } sm ? ToUnixMs(sm) : DBNull.Value;
                p.CopiedSize.Value = (object?)e.CopiedSize ?? DBNull.Value;
                p.CopiedMtime.Value = e.CopiedMtimeUtc is { } cm ? ToUnixMs(cm) : DBNull.Value;
                p.CopiedAt.Value = e.CopiedAtUtc is { } ca ? ToUnixMs(ca) : DBNull.Value;
                p.ViaLink.Value = e.ViaLink ? 1 : 0;
                p.StatusChanged.Value = e.StatusChangedUtc is { } sc ? ToUnixMs(sc) : DBNull.Value;
                cmd.ExecuteNonQuery();

                if (++inBatch < BatchRows) continue;
                tx.Commit();
                tx.Dispose();
                inBatch = 0;
                tx = _conn.BeginTransaction();
                cmd.Transaction = tx;
            }
            tx.Commit();
        }
        finally
        {
            cmd.Dispose();
            tx.Dispose();
        }
    }

    public void Delete(string pair, IEnumerable<string> paths)
    {
        using var tx = _conn.BeginTransaction();
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM entries WHERE pair = $pair AND path = $path";
        var pPair = cmd.Parameters.Add("$pair", SqliteType.Text);
        var pPath = cmd.Parameters.Add("$path", SqliteType.Text);
        pPair.Value = pair;
        foreach (var p in paths) { pPath.Value = p; cmd.ExecuteNonQuery(); }
        tx.Commit();
    }

    /// <summary>Forgets a path and everything below it (case-insensitive), so it can be mirrored again as new. Returns rows removed.</summary>
    public int DeleteSubtree(string pair, string relativePath)
    {
        var rel = relativePath.Trim().Trim('\\', '/');
        var prefix = rel + "\\";
        // Deliberately the full load, not LoadScope: `forget` takes a path a human typed, so it has to match
        // whatever case is stored — including Cyrillic, which neither SQLite's BINARY nor its NOCASE collation
        // folds. LoadScope is for the hot path, where the prefix comes from the file system with exact casing.
        var victims = Load(pair).Keys
            .Where(k => k.Equals(rel, StringComparison.OrdinalIgnoreCase) || k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (victims.Count > 0) Delete(pair, victims);
        return victims.Count;
    }

    public string? GetMeta(string key)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    public void SetMeta(string key, string value)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT INTO meta(key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    public long FileSizeBytes => File.Exists(Path) ? new FileInfo(Path).Length : 0;

    // ------------------------------------------------------------------ helpers

    private readonly record struct EntryParameters(
        SqliteParameter Pair, SqliteParameter PathValue, SqliteParameter Kind, SqliteParameter Status,
        SqliteParameter First, SqliteParameter Last, SqliteParameter SrcSize, SqliteParameter SrcMtime,
        SqliteParameter CopiedSize, SqliteParameter CopiedMtime, SqliteParameter CopiedAt,
        SqliteParameter ViaLink, SqliteParameter StatusChanged);

    private static EntryParameters AddEntryParameters(SqliteCommand cmd) => new(
        cmd.Parameters.Add("$pair", SqliteType.Text),
        cmd.Parameters.Add("$path", SqliteType.Text),
        cmd.Parameters.Add("$kind", SqliteType.Integer),
        cmd.Parameters.Add("$status", SqliteType.Integer),
        cmd.Parameters.Add("$first_seen", SqliteType.Integer),
        cmd.Parameters.Add("$last_seen", SqliteType.Integer),
        cmd.Parameters.Add("$src_size", SqliteType.Integer),
        cmd.Parameters.Add("$src_mtime", SqliteType.Integer),
        cmd.Parameters.Add("$copied_size", SqliteType.Integer),
        cmd.Parameters.Add("$copied_mtime", SqliteType.Integer),
        cmd.Parameters.Add("$copied_at", SqliteType.Integer),
        cmd.Parameters.Add("$via_link", SqliteType.Integer),
        cmd.Parameters.Add("$status_changed", SqliteType.Integer));

    private void Exec(string sql, SqliteTransaction? tx = null)
    {
        using var cmd = _conn.CreateCommand();
        if (tx is not null) cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    internal static long ToUnixMs(DateTimeOffset t) => t.ToUniversalTime().ToUnixTimeMilliseconds();

    internal static DateTimeOffset FromUnixMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms);

    /// <summary>Human-readable UTC stamp for <c>meta</c> values (last_run and friends), which stay text.</summary>
    public static string Fmt(DateTimeOffset t) => t.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseLegacyTime(string s) => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    public void Dispose() => _conn.Dispose();
}
