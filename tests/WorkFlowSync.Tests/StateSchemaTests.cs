using Microsoft.Data.Sqlite;
using WorkFlowSync.Core.Execution;
using WorkFlowSync.Core.Logging;
using WorkFlowSync.Core.Model;
using WorkFlowSync.Core.Planning;
using WorkFlowSync.Core.State;

namespace WorkFlowSync.Tests;

/// <summary>
/// Schema v2 (docs/plan-etap5.md §4.4): integer times, migration from the text-based v1, scoped reads,
/// batched writes and checkpoints. The migration is the one operation here that could lose the whole
/// history, so it is covered from a hand-built v1 database rather than from anything this build wrote.
/// </summary>
public class StateSchemaTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wfs-schema-" + Guid.NewGuid().ToString("N"));

    public StateSchemaTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* the store may still hold the file on a failure */ }
        GC.SuppressFinalize(this);
    }

    private string DbPath => Path.Combine(_dir, "state.db");

    /// <summary>Writes a database exactly as schema v1 did: ISO-8601 "O" strings, no STRICT, no remote columns.</summary>
    private void WriteLegacyDatabase(params (string Pair, string RelPath, DateTimeOffset FirstSeen, long? Size)[] rows)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        conn.Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE entries (
                  pair TEXT NOT NULL, path TEXT NOT NULL, kind INTEGER NOT NULL, status INTEGER NOT NULL,
                  first_seen TEXT NOT NULL, last_seen TEXT NOT NULL,
                  src_size INTEGER, src_mtime TEXT, copied_size INTEGER, copied_mtime TEXT,
                  via_link INTEGER NOT NULL DEFAULT 0, status_changed TEXT,
                  PRIMARY KEY (pair, path)) WITHOUT ROWID;
                CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT);
                INSERT OR IGNORE INTO meta(key, value) VALUES ('schema_version', '1');
                """;
            cmd.ExecuteNonQuery();
        }

        foreach (var (pair, rel, firstSeen, size) in rows)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO entries (pair, path, kind, status, first_seen, last_seen, src_size, src_mtime, via_link)
                VALUES ($pair, $path, 1, 0, $first, $last, $size, $mtime, 0)
                """;
            cmd.Parameters.AddWithValue("$pair", pair);
            cmd.Parameters.AddWithValue("$path", rel);
            cmd.Parameters.AddWithValue("$first", StateStore.Fmt(firstSeen));
            cmd.Parameters.AddWithValue("$last", StateStore.Fmt(firstSeen.AddMinutes(1)));
            cmd.Parameters.AddWithValue("$size", (object?)size ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$mtime", StateStore.Fmt(firstSeen.AddSeconds(30)));
            cmd.ExecuteNonQuery();
        }
    }

    [Fact]
    public void Legacy_database_is_migrated_with_every_row_and_date_intact()
    {
        var seen = new DateTimeOffset(2024, 3, 7, 14, 21, 9, TimeSpan.Zero);
        WriteLegacyDatabase(
            ("vrp", @"Засідання ВРП\протокол.docx", seen, 1234),
            ("vrp", @"Засідання ВРП", seen, null),
            ("other", @"a.txt", seen.AddDays(-10), 7));

        using var store = new StateStore(DbPath);
        var vrp = store.Load("vrp");

        Assert.Equal(2, vrp.Count);
        Assert.Equal(1, store.Count("other"));
        var file = vrp[@"Засідання ВРП\протокол.docx"];
        Assert.Equal(seen, file.FirstSeenUtc);                       // the date survives the text -> integer rewrite
        Assert.Equal(seen.AddSeconds(30), file.SourceMtimeUtc);
        Assert.Equal(1234, file.SourceSize);
        Assert.Null(vrp[@"Засідання ВРП"].SourceSize);

        Assert.Equal("3", store.GetMeta("schema_version"));
        Assert.NotNull(store.MigratedFromBackup);
        Assert.True(File.Exists(store.MigratedFromBackup!), "the v1 database must be backed up before it is rewritten");
    }

    [Fact]
    public void V2_database_is_migrated_to_v3_with_v2_backup_and_pending_table()
    {
        var seen = new DateTimeOffset(2026, 3, 7, 14, 21, 9, TimeSpan.Zero);
        WriteV2Database(
            ("vrp", @"Засідання ВРП\протокол.docx", seen, 5432),
            ("other", @"test.txt", seen, 12));

        using var store = new StateStore(DbPath);

        Assert.Equal("3", store.GetMeta("schema_version"));
        Assert.NotNull(store.MigratedFromBackup);
        Assert.EndsWith(".v2-backup", store.MigratedFromBackup, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(store.MigratedFromBackup), "v2 database must be backed up before v3 migration");

        // entries are completely preserved
        var vrp = store.Load("vrp");
        Assert.Single(vrp);
        Assert.Equal(5432, vrp[@"Засідання ВРП\протокол.docx"].SourceSize);
        Assert.Equal(1, store.Count("other"));

        // pending table exists and works
        Assert.Equal(0, store.Pending.CountOpen());
        store.Pending.Upsert(new PendingEntry
        {
            Pair = "vrp",
            Path = "new.docx",
            Change = PendingChange.Added,
            Kind = EntryKind.File,
            DetectedUtc = DateTimeOffset.UtcNow,
        });
        Assert.Equal(1, store.Pending.CountOpen("vrp"));
    }

    private void WriteV2Database(params (string Pair, string RelPath, DateTimeOffset FirstSeen, long? Size)[] rows)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        conn.Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE entries (
                  pair TEXT NOT NULL, path TEXT NOT NULL, kind INTEGER NOT NULL, status INTEGER NOT NULL,
                  first_seen INTEGER NOT NULL, last_seen INTEGER NOT NULL,
                  src_size INTEGER, src_mtime INTEGER, copied_size INTEGER, copied_mtime INTEGER,
                  via_link INTEGER NOT NULL DEFAULT 0, status_changed INTEGER,
                  remote_id TEXT, remote_version TEXT,
                  PRIMARY KEY (pair, path)) STRICT, WITHOUT ROWID;
                CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT);
                INSERT OR IGNORE INTO meta(key, value) VALUES ('schema_version', '2');
                """;
            cmd.ExecuteNonQuery();
        }

        foreach (var (pair, rel, firstSeen, size) in rows)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO entries (pair, path, kind, status, first_seen, last_seen, src_size, src_mtime, via_link)
                VALUES ($pair, $path, 0, 0, $first, $last, $size, $mtime, 0)
                """;
            cmd.Parameters.AddWithValue("$pair", pair);
            cmd.Parameters.AddWithValue("$path", rel);
            cmd.Parameters.AddWithValue("$first", firstSeen.ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("$last", firstSeen.AddMinutes(1).ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("$size", (object?)size ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$mtime", firstSeen.AddSeconds(30).ToUnixTimeMilliseconds());
            cmd.ExecuteNonQuery();
        }
    }

    [Fact]
    public void Migration_runs_once_and_a_reopened_database_is_left_alone()
    {
        WriteLegacyDatabase(("vrp", "a.txt", DateTimeOffset.UtcNow, 1));
        using (var first = new StateStore(DbPath)) Assert.NotNull(first.MigratedFromBackup);

        using var second = new StateStore(DbPath);

        Assert.Null(second.MigratedFromBackup);
        Assert.Equal(1, second.Count("vrp"));
    }

    [Fact]
    public void A_database_from_a_newer_build_is_refused_instead_of_being_mangled()
    {
        using (var store = new StateStore(DbPath)) store.SetMeta("schema_version", "99");

        var ex = Assert.Throws<InvalidDataException>(() => new StateStore(DbPath));

        Assert.Contains("99", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Times_round_trip_through_the_integer_columns()
    {
        var t = new DateTimeOffset(2026, 9, 15, 22, 6, 59, 123, TimeSpan.Zero);
        using var store = new StateStore(DbPath);
        store.Upsert(new[]
        {
            new StateEntry
            {
                PairName = "p", RelativePath = "a.txt", Kind = EntryKind.File, Status = EntryStatus.Active,
                FirstSeenUtc = t, LastSeenUtc = t.AddHours(1), SourceMtimeUtc = t.AddMinutes(2),
                CopiedMtimeUtc = t.AddMinutes(3), StatusChangedUtc = t.AddMinutes(4), SourceSize = 10, CopiedSize = 10,
            },
        });

        var row = store.Load("p")["a.txt"];

        Assert.Equal(t, row.FirstSeenUtc);
        Assert.Equal(t.AddHours(1), row.LastSeenUtc);
        Assert.Equal(t.AddMinutes(2), row.SourceMtimeUtc);
        Assert.Equal(t.AddMinutes(3), row.CopiedMtimeUtc);
        Assert.Equal(t.AddMinutes(4), row.StatusChangedUtc);
    }

    [Fact]
    public void Scoped_read_returns_one_folder_and_its_descendants_only()
    {
        using var store = new StateStore(DbPath);
        store.Upsert(new[]
        {
            Entry("p", @"Засідання"),
            Entry("p", @"Засідання\2026\протокол.docx"),
            Entry("p", @"Засідання\2025\старий.docx"),
            Entry("p", @"Засідання-архів\інше.docx"),   // same prefix, different folder: must NOT be included
            Entry("p", @"Інша тека\файл.txt"),
            Entry("q", @"Засідання\чуже.docx"),          // another pair
        });

        var scope = store.LoadScope("p", @"Засідання");

        Assert.Equal(3, scope.Count);
        Assert.Contains(@"Засідання", scope.Keys);
        Assert.Contains(@"Засідання\2026\протокол.docx", scope.Keys);
        Assert.Contains(@"Засідання\2025\старий.docx", scope.Keys);
        Assert.DoesNotContain(@"Засідання-архів\інше.docx", scope.Keys);
        Assert.DoesNotContain(@"Інша тека\файл.txt", scope.Keys);
    }

    [Fact]
    public void An_empty_scope_means_the_whole_pair()
    {
        using var store = new StateStore(DbPath);
        store.Upsert(new[] { Entry("p", "a.txt"), Entry("p", @"dir\b.txt") });

        Assert.Equal(2, store.LoadScope("p", null).Count);
        Assert.Equal(2, store.LoadScope("p", "  ").Count);
    }

    [Fact]
    public void Writing_more_rows_than_one_batch_keeps_all_of_them()
    {
        using var store = new StateStore(DbPath);
        var many = Enumerable.Range(0, StateStore.BatchRows + 517).Select(i => Entry("p", $@"dir\file{i}.txt"));

        store.Upsert(many);

        Assert.Equal(StateStore.BatchRows + 517, store.Count("p"));
    }

    [Fact]
    public void Checkpoints_persist_copied_files_before_the_pass_ends()
    {
        var src = Path.Combine(_dir, "src");
        var dst = Path.Combine(_dir, "dst");
        Directory.CreateDirectory(src);
        var plan = new SyncPlan();
        for (var i = 0; i < 5; i++)
        {
            var name = $"f{i}.txt";
            File.WriteAllText(Path.Combine(src, name), "x");
            plan.Actions.Add(new SyncAction(SyncActionKind.CopyFile, name, Entry("p", name)));
        }

        var batches = new List<int>();
        var executor = new SyncExecutor(src, dst, new MemorySyncLog(), dryRun: false) { CheckpointEvery = 2 };
        var result = executor.Execute(plan, "[p]", checkpoint: rows => batches.Add(rows.Count));

        Assert.Equal(5, result.FilesCopied);
        Assert.Equal(new[] { 2, 2 }, batches);          // two checkpoints of two rows
        Assert.Equal(4, result.CheckpointedRows);
        Assert.Single(result.Completed);                 // only the tail is left for the caller to write
    }

    [Fact]
    public void A_failed_checkpoint_keeps_the_rows_for_the_next_attempt()
    {
        var src = Path.Combine(_dir, "src2");
        var dst = Path.Combine(_dir, "dst2");
        Directory.CreateDirectory(src);
        var plan = new SyncPlan();
        for (var i = 0; i < 3; i++)
        {
            var name = $"g{i}.txt";
            File.WriteAllText(Path.Combine(src, name), "x");
            plan.Actions.Add(new SyncAction(SyncActionKind.CopyFile, name, Entry("p", name)));
        }

        var executor = new SyncExecutor(src, dst, new MemorySyncLog(), dryRun: false) { CheckpointEvery = 2 };

        // The executor logs and continues on any failure inside the loop; the rows must not disappear with it.
        var result = executor.Execute(plan, "[p]", checkpoint: _ => throw new IOException("state.db is locked"));

        Assert.Equal(0, result.CheckpointedRows);
        Assert.Equal(3, result.Completed.Count);
    }

    private static StateEntry Entry(string pair, string rel) => new()
    {
        PairName = pair,
        RelativePath = rel,
        Kind = EntryKind.File,
        Status = EntryStatus.Active,
        FirstSeenUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        LastSeenUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        SourceSize = 1,
    };
}
