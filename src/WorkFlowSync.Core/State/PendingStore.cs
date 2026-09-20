using System.Globalization;
using Microsoft.Data.Sqlite;
using WorkFlowSync.Core.Model;

namespace WorkFlowSync.Core.State;

/// <summary>
/// SQLite-backed store for the approval queue (<c>pending</c> table in schema v3).
/// Tracks changes detected in moderated target folders that require human approval.
/// </summary>
public sealed class PendingStore
{
    private const string Columns =
        "id, pair, path, from_path, change, kind, src_size, src_mtime, dst_size, dst_mtime, detected, status, resolved, version_path";

    private readonly SqliteConnection _conn;

    public PendingStore(SqliteConnection conn)
    {
        _conn = conn ?? throw new ArgumentNullException(nameof(conn));
    }

    /// <summary>
    /// Upserts a pending change. If an open entry (<c>status = 0</c>) already exists for this (pair, path),
    /// it is updated with new file properties and detected timestamp. Returns the row ID.
    /// </summary>
    public long Upsert(PendingEntry entry)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO pending (
              pair, path, from_path, change, kind,
              src_size, src_mtime, dst_size, dst_mtime,
              detected, status, resolved, version_path
            ) VALUES (
              $pair, $path, $from_path, $change, $kind,
              $src_size, $src_mtime, $dst_size, $dst_mtime,
              $detected, $status, $resolved, $version_path
            )
            ON CONFLICT(pair, path) WHERE status = 0 DO UPDATE SET
              from_path = excluded.from_path,
              change = excluded.change,
              kind = excluded.kind,
              src_size = excluded.src_size,
              src_mtime = excluded.src_mtime,
              dst_size = excluded.dst_size,
              dst_mtime = excluded.dst_mtime,
              detected = excluded.detected
            RETURNING id;
            """;

        cmd.Parameters.AddWithValue("$pair", entry.Pair);
        cmd.Parameters.AddWithValue("$path", entry.Path);
        cmd.Parameters.AddWithValue("$from_path", (object?)entry.FromPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$change", (int)entry.Change);
        cmd.Parameters.AddWithValue("$kind", (int)entry.Kind);
        cmd.Parameters.AddWithValue("$src_size", (object?)entry.SrcSize ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$src_mtime", entry.SrcMtimeUtc is { } sm ? StateStore.ToUnixMs(sm) : DBNull.Value);
        cmd.Parameters.AddWithValue("$dst_size", (object?)entry.DstSize ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$dst_mtime", entry.DstMtimeUtc is { } dm ? StateStore.ToUnixMs(dm) : DBNull.Value);
        cmd.Parameters.AddWithValue("$detected", StateStore.ToUnixMs(entry.DetectedUtc));
        cmd.Parameters.AddWithValue("$status", (int)entry.Status);
        cmd.Parameters.AddWithValue("$resolved", entry.ResolvedUtc is { } res ? StateStore.ToUnixMs(res) : DBNull.Value);
        cmd.Parameters.AddWithValue("$version_path", (object?)entry.VersionPath ?? DBNull.Value);

        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Gets all open entries (<c>status = 0</c>), optionally filtered by pair, ordered by detection time.
    /// </summary>
    public IReadOnlyList<PendingEntry> GetOpen(string? pair = null)
    {
        var list = new List<PendingEntry>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = pair is null
            ? $"SELECT {Columns} FROM pending WHERE status = 0 ORDER BY detected ASC, id ASC"
            : $"SELECT {Columns} FROM pending WHERE pair = $pair AND status = 0 ORDER BY detected ASC, id ASC";

        if (pair is not null) cmd.Parameters.AddWithValue("$pair", pair);

        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadEntry(r));
        return list;
    }

    /// <summary>
    /// Gets a single open entry for the specified pair and relative path, or null if none is open.
    /// </summary>
    public PendingEntry? GetOpenByPath(string pair, string path)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM pending WHERE pair = $pair AND path = $path AND status = 0 LIMIT 1";
        cmd.Parameters.AddWithValue("$pair", pair);
        cmd.Parameters.AddWithValue("$path", path);

        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadEntry(r) : null;
    }

    /// <summary>
    /// Gets an entry by its unique primary key ID, regardless of status.
    /// </summary>
    public PendingEntry? GetById(long id)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM pending WHERE id = $id LIMIT 1";
        cmd.Parameters.AddWithValue("$id", id);

        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadEntry(r) : null;
    }

    /// <summary>
    /// Counts open entries (<c>status = 0</c>), across all pairs or for a specific pair.
    /// </summary>
    public int CountOpen(string? pair = null)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = pair is null
            ? "SELECT COUNT(*) FROM pending WHERE status = 0"
            : "SELECT COUNT(*) FROM pending WHERE pair = $pair AND status = 0";

        if (pair is not null) cmd.Parameters.AddWithValue("$pair", pair);
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Transitions an open entry to a resolved status (Approved, Dismissed, Reverted, or Stale).
    /// Returns true if the entry was found and updated, false otherwise.
    /// </summary>
    public bool Resolve(long id, PendingStatus newStatus, DateTimeOffset resolvedUtc, string? versionPath = null)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            UPDATE pending
            SET status = $status, resolved = $resolved, version_path = COALESCE($version_path, version_path)
            WHERE id = $id AND status = 0
            """;

        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$status", (int)newStatus);
        cmd.Parameters.AddWithValue("$resolved", StateStore.ToUnixMs(resolvedUtc));
        cmd.Parameters.AddWithValue("$version_path", (object?)versionPath ?? DBNull.Value);

        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// Resolves an open entry by pair and relative path. Returns number of rows affected (0 or 1).
    /// </summary>
    public int ResolveByPath(string pair, string path, PendingStatus newStatus, DateTimeOffset resolvedUtc, string? versionPath = null)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            UPDATE pending
            SET status = $status, resolved = $resolved, version_path = COALESCE($version_path, version_path)
            WHERE pair = $pair AND path = $path AND status = 0
            """;

        cmd.Parameters.AddWithValue("$pair", pair);
        cmd.Parameters.AddWithValue("$path", path);
        cmd.Parameters.AddWithValue("$status", (int)newStatus);
        cmd.Parameters.AddWithValue("$resolved", StateStore.ToUnixMs(resolvedUtc));
        cmd.Parameters.AddWithValue("$version_path", (object?)versionPath ?? DBNull.Value);

        return cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Resolves all open entries for a specific pair with the given status.
    /// </summary>
    public int ResolveAll(string pair, PendingStatus newStatus, DateTimeOffset resolvedUtc)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            UPDATE pending
            SET status = $status, resolved = $resolved
            WHERE pair = $pair AND status = 0
            """;

        cmd.Parameters.AddWithValue("$pair", pair);
        cmd.Parameters.AddWithValue("$status", (int)newStatus);
        cmd.Parameters.AddWithValue("$resolved", StateStore.ToUnixMs(resolvedUtc));

        return cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Gets all entries for a pair (including resolved ones), up to <paramref name="limit"/>.
    /// </summary>
    public IReadOnlyList<PendingEntry> GetAll(string pair, int limit = 1000)
    {
        var list = new List<PendingEntry>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM pending WHERE pair = $pair ORDER BY detected DESC, id DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$pair", pair);
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 10000));

        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadEntry(r));
        return list;
    }

    private static PendingEntry ReadEntry(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        Pair = r.GetString(1),
        Path = r.GetString(2),
        FromPath = r.IsDBNull(3) ? null : r.GetString(3),
        Change = (PendingChange)r.GetInt32(4),
        Kind = (EntryKind)r.GetInt32(5),
        SrcSize = r.IsDBNull(6) ? null : r.GetInt64(6),
        SrcMtimeUtc = r.IsDBNull(7) ? null : StateStore.FromUnixMs(r.GetInt64(7)),
        DstSize = r.IsDBNull(8) ? null : r.GetInt64(8),
        DstMtimeUtc = r.IsDBNull(9) ? null : StateStore.FromUnixMs(r.GetInt64(9)),
        DetectedUtc = StateStore.FromUnixMs(r.GetInt64(10)),
        Status = (PendingStatus)r.GetInt32(11),
        ResolvedUtc = r.IsDBNull(12) ? null : StateStore.FromUnixMs(r.GetInt64(12)),
        VersionPath = r.IsDBNull(13) ? null : r.GetString(13),
    };
}
