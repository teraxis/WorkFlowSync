using Microsoft.Data.Sqlite;
using WorkFlowSync.Core.Model;
using WorkFlowSync.Core.State;

namespace WorkFlowSync.Tests;

public class PendingStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wfs-pending-" + Guid.NewGuid().ToString("N"));

    public PendingStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private string DbPath => Path.Combine(_dir, "state.db");

    [Fact]
    public void Upsert_inserts_new_pending_entry_and_returns_id()
    {
        using var store = new StateStore(DbPath);
        var now = new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

        var id = store.Pending.Upsert(new PendingEntry
        {
            Pair = "pair1",
            Path = "doc.txt",
            Change = PendingChange.Modified,
            Kind = EntryKind.File,
            SrcSize = 100,
            SrcMtimeUtc = now.AddMinutes(-10),
            DstSize = 150,
            DstMtimeUtc = now,
            DetectedUtc = now,
        });

        Assert.True(id > 0);
        var item = store.Pending.GetById(id);
        Assert.NotNull(item);
        Assert.Equal("pair1", item.Pair);
        Assert.Equal("doc.txt", item.Path);
        Assert.Equal(PendingChange.Modified, item.Change);
        Assert.Equal(EntryKind.File, item.Kind);
        Assert.Equal(100, item.SrcSize);
        Assert.Equal(150, item.DstSize);
        Assert.Equal(PendingStatus.Pending, item.Status);
        Assert.Null(item.ResolvedUtc);
    }

    [Fact]
    public void Upsert_on_existing_open_entry_updates_it_without_duplication()
    {
        using var store = new StateStore(DbPath);
        var t1 = new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);
        var t2 = t1.AddMinutes(5);

        var id1 = store.Pending.Upsert(new PendingEntry
        {
            Pair = "pair1",
            Path = "report.docx",
            Change = PendingChange.Modified,
            Kind = EntryKind.File,
            SrcSize = 200,
            DstSize = 250,
            DetectedUtc = t1,
        });

        var id2 = store.Pending.Upsert(new PendingEntry
        {
            Pair = "pair1",
            Path = "report.docx",
            Change = PendingChange.Modified,
            Kind = EntryKind.File,
            SrcSize = 200,
            DstSize = 300,
            DetectedUtc = t2,
        });

        Assert.Equal(id1, id2);
        Assert.Equal(1, store.Pending.CountOpen("pair1"));

        var item = store.Pending.GetById(id1);
        Assert.NotNull(item);
        Assert.Equal(300, item.DstSize);
        Assert.Equal(t2, item.DetectedUtc);
    }

    [Fact]
    public void GetOpen_filters_by_pair_and_orders_by_detected()
    {
        using var store = new StateStore(DbPath);
        var now = DateTimeOffset.UtcNow;

        store.Pending.Upsert(new PendingEntry
        {
            Pair = "pair1",
            Path = "b.txt",
            Change = PendingChange.Added,
            Kind = EntryKind.File,
            DetectedUtc = now.AddMinutes(2),
        });
        store.Pending.Upsert(new PendingEntry
        {
            Pair = "pair1",
            Path = "a.txt",
            Change = PendingChange.Added,
            Kind = EntryKind.File,
            DetectedUtc = now.AddMinutes(1),
        });
        store.Pending.Upsert(new PendingEntry
        {
            Pair = "pair2",
            Path = "c.txt",
            Change = PendingChange.Deleted,
            Kind = EntryKind.File,
            DetectedUtc = now,
        });

        var pair1Open = store.Pending.GetOpen("pair1");
        Assert.Equal(2, pair1Open.Count);
        Assert.Equal("a.txt", pair1Open[0].Path);
        Assert.Equal("b.txt", pair1Open[1].Path);

        var allOpen = store.Pending.GetOpen();
        Assert.Equal(3, allOpen.Count);
        Assert.Equal("c.txt", allOpen[0].Path); // earliest detected
    }

    [Fact]
    public void Resolve_updates_status_resolved_time_and_version_path()
    {
        using var store = new StateStore(DbPath);
        var now = DateTimeOffset.UtcNow;

        var id = store.Pending.Upsert(new PendingEntry
        {
            Pair = "pair1",
            Path = "deleted.txt",
            Change = PendingChange.Deleted,
            Kind = EntryKind.File,
            DetectedUtc = now,
        });

        var resolvedAt = now.AddSeconds(10);
        var success = store.Pending.Resolve(id, PendingStatus.Approved, resolvedAt, versionPath: @"\.wfsversions\2026-09-18\deleted.txt");

        Assert.True(success);
        Assert.Equal(0, store.Pending.CountOpen("pair1"));

        var resolved = store.Pending.GetById(id);
        Assert.NotNull(resolved);
        Assert.Equal(PendingStatus.Approved, resolved.Status);
        Assert.Equal(resolvedAt.ToUnixTimeMilliseconds(), resolved.ResolvedUtc?.ToUnixTimeMilliseconds());
        Assert.Equal(@"\.wfsversions\2026-09-18\deleted.txt", resolved.VersionPath);

        // Trying to resolve again returns false because status is no longer 0
        Assert.False(store.Pending.Resolve(id, PendingStatus.Dismissed, resolvedAt));
    }

    [Fact]
    public void Once_resolved_new_open_entry_for_same_path_can_be_created()
    {
        using var store = new StateStore(DbPath);
        var now = DateTimeOffset.UtcNow;

        var id1 = store.Pending.Upsert(new PendingEntry
        {
            Pair = "pair1",
            Path = "reused.txt",
            Change = PendingChange.Modified,
            Kind = EntryKind.File,
            DetectedUtc = now,
        });

        store.Pending.Resolve(id1, PendingStatus.Dismissed, now.AddSeconds(5));

        // After resolution, an identical path can be detected again in the future
        var id2 = store.Pending.Upsert(new PendingEntry
        {
            Pair = "pair1",
            Path = "reused.txt",
            Change = PendingChange.Modified,
            Kind = EntryKind.File,
            DetectedUtc = now.AddMinutes(1),
        });

        Assert.NotEqual(id1, id2);
        Assert.Equal(1, store.Pending.CountOpen("pair1"));
        Assert.Equal(2, store.Pending.GetAll("pair1").Count);
    }

    [Fact]
    public void ResolveAll_resolves_all_open_entries_for_pair()
    {
        using var store = new StateStore(DbPath);
        var now = DateTimeOffset.UtcNow;

        store.Pending.Upsert(new PendingEntry { Pair = "pair1", Path = "1.txt", Change = PendingChange.Added, Kind = EntryKind.File, DetectedUtc = now });
        store.Pending.Upsert(new PendingEntry { Pair = "pair1", Path = "2.txt", Change = PendingChange.Added, Kind = EntryKind.File, DetectedUtc = now });
        store.Pending.Upsert(new PendingEntry { Pair = "other", Path = "3.txt", Change = PendingChange.Added, Kind = EntryKind.File, DetectedUtc = now });

        var affected = store.Pending.ResolveAll("pair1", PendingStatus.Dismissed, now);

        Assert.Equal(2, affected);
        Assert.Equal(0, store.Pending.CountOpen("pair1"));
        Assert.Equal(1, store.Pending.CountOpen("other"));
    }
}
