using System.Globalization;
using Microsoft.Data.Sqlite;
using WorkFlowSync.Core.Model;

namespace WorkFlowSync.Core.State;

/// <summary>
/// SQLite-backed memory of the mirror (docs/product/features/state-and-tombstones.md).
/// Loaded fully into a dictionary per pair at the start of a pass; changes are written in one transaction.
/// </summary>
public sealed class StateStore : IDisposable
{
    public const int SchemaVersion = 1;

    private readonly SqliteConnection _conn;

    public string Path { get; }

    public StateStore(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        var dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, Mode = SqliteOpenMode.ReadWriteCreate }.ToString());
        _conn.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
        Exec("""
            CREATE TABLE IF NOT EXISTS entries (
              pair TEXT NOT NULL, path TEXT NOT NULL, kind INTEGER NOT NULL, status INTEGER NOT NULL,
              first_seen TEXT NOT NULL, last_seen TEXT NOT NULL,
              src_size INTEGER, src_mtime TEXT, copied_size INTEGER, copied_mtime TEXT,
              via_link INTEGER NOT NULL DEFAULT 0, status_changed TEXT,
              PRIMARY KEY (pair, path)) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT);
            INSERT OR IGNORE INTO meta(key, value) VALUES ('schema_version', '1');
            """);
    }

    public Dictionary<string, StateEntry> Load(string pair)
    {
        var dict = new Dictionary<string, StateEntry>(StringComparer.OrdinalIgnoreCase);
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT path, kind, status, first_seen, last_seen, src_size, src_mtime, copied_size, copied_mtime, via_link, status_changed FROM entries WHERE pair = $pair";
        cmd.Parameters.AddWithValue("$pair", pair);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var e = new StateEntry
            {
                PairName = pair,
                RelativePath = r.GetString(0),
                Kind = (EntryKind)r.GetInt32(1),
                Status = (EntryStatus)r.GetInt32(2),
                FirstSeenUtc = ParseTime(r.GetString(3)),
                LastSeenUtc = ParseTime(r.GetString(4)),
                SourceSize = r.IsDBNull(5) ? null : r.GetInt64(5),
                SourceMtimeUtc = r.IsDBNull(6) ? null : ParseTime(r.GetString(6)),
                CopiedSize = r.IsDBNull(7) ? null : r.GetInt64(7),
                CopiedMtimeUtc = r.IsDBNull(8) ? null : ParseTime(r.GetString(8)),
                ViaLink = r.GetInt32(9) != 0,
                StatusChangedUtc = r.IsDBNull(10) ? null : ParseTime(r.GetString(10)),
            };
            dict[e.RelativePath] = e;
        }
        return dict;
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

    /// <summary>Upserts all given entries in a single transaction.</summary>
    public void Upsert(IEnumerable<StateEntry> entries)
    {
        using var tx = _conn.BeginTransaction();
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO entries (pair, path, kind, status, first_seen, last_seen, src_size, src_mtime, copied_size, copied_mtime, via_link, status_changed)
            VALUES ($pair, $path, $kind, $status, $first_seen, $last_seen, $src_size, $src_mtime, $copied_size, $copied_mtime, $via_link, $status_changed)
            ON CONFLICT(pair, path) DO UPDATE SET
              kind = excluded.kind, status = excluded.status, first_seen = excluded.first_seen, last_seen = excluded.last_seen,
              src_size = excluded.src_size, src_mtime = excluded.src_mtime, copied_size = excluded.copied_size, copied_mtime = excluded.copied_mtime,
              via_link = excluded.via_link, status_changed = excluded.status_changed
            """;
        var pPair = cmd.Parameters.Add("$pair", SqliteType.Text);
        var pPath = cmd.Parameters.Add("$path", SqliteType.Text);
        var pKind = cmd.Parameters.Add("$kind", SqliteType.Integer);
        var pStatus = cmd.Parameters.Add("$status", SqliteType.Integer);
        var pFirst = cmd.Parameters.Add("$first_seen", SqliteType.Text);
        var pLast = cmd.Parameters.Add("$last_seen", SqliteType.Text);
        var pSrcSize = cmd.Parameters.Add("$src_size", SqliteType.Integer);
        var pSrcMtime = cmd.Parameters.Add("$src_mtime", SqliteType.Text);
        var pCopSize = cmd.Parameters.Add("$copied_size", SqliteType.Integer);
        var pCopMtime = cmd.Parameters.Add("$copied_mtime", SqliteType.Text);
        var pVia = cmd.Parameters.Add("$via_link", SqliteType.Integer);
        var pChanged = cmd.Parameters.Add("$status_changed", SqliteType.Text);

        foreach (var e in entries)
        {
            pPair.Value = e.PairName;
            pPath.Value = e.RelativePath;
            pKind.Value = (int)e.Kind;
            pStatus.Value = (int)e.Status;
            pFirst.Value = Fmt(e.FirstSeenUtc);
            pLast.Value = Fmt(e.LastSeenUtc);
            pSrcSize.Value = (object?)e.SourceSize ?? DBNull.Value;
            pSrcMtime.Value = e.SourceMtimeUtc is { } sm ? Fmt(sm) : DBNull.Value;
            pCopSize.Value = (object?)e.CopiedSize ?? DBNull.Value;
            pCopMtime.Value = e.CopiedMtimeUtc is { } cm ? Fmt(cm) : DBNull.Value;
            pVia.Value = e.ViaLink ? 1 : 0;
            pChanged.Value = e.StatusChangedUtc is { } sc ? Fmt(sc) : DBNull.Value;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
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

    private void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public static string Fmt(DateTimeOffset t) => t.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTime(string s) => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    public void Dispose() => _conn.Dispose();
}
