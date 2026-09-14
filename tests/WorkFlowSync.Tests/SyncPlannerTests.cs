using WorkFlowSync.Core.Model;
using WorkFlowSync.Core.Planning;
using WorkFlowSync.Core.Scanning;

namespace WorkFlowSync.Tests;

/// <summary>One test per row of the decision table in docs/product/features/mirror-rules.md.</summary>
public class SyncPlannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T1 = Now.AddDays(-10);
    private static readonly DateTimeOffset T2 = Now.AddDays(-1);

    private static ScanEntry File(string path, long size = 100, DateTimeOffset? mtime = null, DateTimeOffset? ctime = null) =>
        new() { RelativePath = path, Kind = EntryKind.File, Size = size, MtimeUtc = mtime ?? T1, CtimeUtc = ctime ?? T1 };

    private static ScanEntry Dir(string path) => new() { RelativePath = path, Kind = EntryKind.Directory, MtimeUtc = T1, CtimeUtc = T1 };

    private static StateEntry Active(string path, long size = 100, DateTimeOffset? mtime = null, EntryKind kind = EntryKind.File) => new()
    {
        PairName = "p", RelativePath = path, Kind = kind, Status = EntryStatus.Active,
        FirstSeenUtc = T1, LastSeenUtc = T1,
        SourceSize = kind == EntryKind.File ? size : null, SourceMtimeUtc = kind == EntryKind.File ? mtime ?? T1 : null,
        CopiedSize = kind == EntryKind.File ? size : null, CopiedMtimeUtc = kind == EntryKind.File ? mtime ?? T1 : null,
    };

    private static Dictionary<string, T> Map<T>(params T[] items) where T : class
    {
        var d = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        foreach (var i in items)
            d[i is ScanEntry s ? s.RelativePath : ((StateEntry)(object)i).RelativePath] = i;
        return d;
    }

    private static SyncPlan Plan(Dictionary<string, ScanEntry> src, Dictionary<string, ScanEntry> dst, Dictionary<string, StateEntry> state, bool backfill = false) =>
        new SyncPlanner("p", Now, backfill).Plan(src, dst, state);

    [Fact]
    public void New_in_source_is_copied_with_first_seen_now()
    {
        var plan = Plan(Map(File("a.txt")), Map<ScanEntry>(), Map<StateEntry>());
        var a = Assert.Single(plan.Actions);
        Assert.Equal(SyncActionKind.CopyFile, a.Kind);
        Assert.Equal(EntryStatus.Active, a.Proposed.Status);
        Assert.Equal(Now, a.Proposed.FirstSeenUtc);
        Assert.Equal(1, plan.Stats.New);
    }

    [Fact]
    public void Backfill_takes_min_of_ctime_and_mtime()
    {
        var plan = Plan(Map(File("a.txt", mtime: T2, ctime: T1)), Map<ScanEntry>(), Map<StateEntry>(), backfill: true);
        Assert.Equal(T1, Assert.Single(plan.Actions).Proposed.FirstSeenUtc);
    }

    [Fact]
    public void Present_on_both_sides_and_unknown_is_adopted_without_copying()
    {
        var plan = Plan(Map(File("a.txt")), Map(File("a.txt")), Map<StateEntry>());
        Assert.Empty(plan.Actions);
        var u = Assert.Single(plan.StateUpdates);
        Assert.Equal(EntryStatus.Active, u.Status);
        Assert.Equal(100, u.CopiedSize);
        Assert.Equal(1, plan.Stats.Adopted);
    }

    [Fact]
    public void Present_on_both_sides_but_different_is_local_modified()
    {
        var plan = Plan(Map(File("a.txt", size: 100)), Map(File("a.txt", size: 150)), Map<StateEntry>());
        Assert.Empty(plan.Actions);
        Assert.Equal(EntryStatus.LocalModified, Assert.Single(plan.StateUpdates).Status);
    }

    [Fact]
    public void Local_only_item_is_ignored()
    {
        var plan = Plan(Map<ScanEntry>(), Map(File("mine.txt")), Map<StateEntry>());
        Assert.Empty(plan.Actions);
        Assert.Empty(plan.StateUpdates);
        Assert.Equal(1, plan.Stats.IgnoredLocalOnly);
    }

    [Fact]
    public void Active_intact_and_source_changed_is_updated()
    {
        var plan = Plan(Map(File("a.txt", size: 200, mtime: T2)), Map(File("a.txt")), Map(Active("a.txt")));
        var a = Assert.Single(plan.Actions);
        Assert.Equal(SyncActionKind.UpdateFile, a.Kind);
        Assert.Equal(200, a.Proposed.SourceSize);
        Assert.Equal(T1, a.Proposed.FirstSeenUtc);   // never changes
    }

    [Fact]
    public void Active_intact_and_source_same_is_unchanged()
    {
        var plan = Plan(Map(File("a.txt")), Map(File("a.txt")), Map(Active("a.txt")));
        Assert.Empty(plan.Actions);
        Assert.Empty(plan.StateUpdates);
        Assert.Equal(1, plan.Stats.Unchanged);
    }

    [Fact]
    public void Mtime_within_two_seconds_counts_as_same()
    {
        var plan = Plan(Map(File("a.txt", mtime: T1.AddSeconds(1.5))), Map(File("a.txt")), Map(Active("a.txt")));
        Assert.Empty(plan.Actions);
    }

    [Fact]
    public void Active_but_locally_changed_becomes_local_modified_and_source_update_is_ignored()
    {
        var plan = Plan(Map(File("a.txt", size: 999, mtime: T2)), Map(File("a.txt", size: 120, mtime: T2)), Map(Active("a.txt")));
        Assert.Empty(plan.Actions);
        Assert.Equal(EntryStatus.LocalModified, Assert.Single(plan.StateUpdates).Status);
    }

    [Fact]
    public void Active_and_removed_locally_becomes_tombstone_even_though_source_has_it()
    {
        var plan = Plan(Map(File("a.txt")), Map<ScanEntry>(), Map(Active("a.txt")));
        Assert.Empty(plan.Actions);
        Assert.Equal(EntryStatus.Tombstone, Assert.Single(plan.StateUpdates).Status);
    }

    [Fact]
    public void Active_and_deleted_in_source_keeps_local_copy_and_state()
    {
        var plan = Plan(Map<ScanEntry>(), Map(File("a.txt")), Map(Active("a.txt")));
        Assert.Empty(plan.Actions);
        Assert.Empty(plan.StateUpdates);
    }

    [Fact]
    public void Active_and_gone_on_both_sides_becomes_tombstone()
    {
        var plan = Plan(Map<ScanEntry>(), Map<ScanEntry>(), Map(Active("a.txt")));
        Assert.Equal(EntryStatus.Tombstone, Assert.Single(plan.StateUpdates).Status);
    }

    [Fact]
    public void Tombstone_is_never_recopied()
    {
        var dead = Active("a.txt") with { Status = EntryStatus.Tombstone };
        var plan = Plan(Map(File("a.txt", size: 5, mtime: T2)), Map<ScanEntry>(), Map(dead));
        Assert.Empty(plan.Actions);
        Assert.Empty(plan.StateUpdates);
    }

    [Fact]
    public void Local_modified_is_never_overwritten_and_becomes_tombstone_when_removed()
    {
        var mod = Active("a.txt") with { Status = EntryStatus.LocalModified };
        var keep = Plan(Map(File("a.txt", size: 5, mtime: T2)), Map(File("a.txt", size: 77)), Map(mod));
        Assert.Empty(keep.Actions);
        Assert.Empty(keep.StateUpdates);

        var gone = Plan(Map(File("a.txt")), Map<ScanEntry>(), Map(mod));
        Assert.Equal(EntryStatus.Tombstone, Assert.Single(gone.StateUpdates).Status);
    }

    [Fact]
    public void Deleting_a_folder_locally_tombstones_descendants_and_blocks_new_files_under_it()
    {
        var state = Map(
            Active("2025", kind: EntryKind.Directory),
            Active(@"2025\old.txt"));
        var source = Map(
            Dir("2025"),
            File(@"2025\old.txt"),
            File(@"2025\new.txt"));      // appeared in source after the local delete

        var plan = Plan(source, Map<ScanEntry>(), state);

        Assert.Empty(plan.Actions);                         // new.txt must NOT be copied
        Assert.Equal(2, plan.StateUpdates.Count);
        Assert.All(plan.StateUpdates, u => Assert.Equal(EntryStatus.Tombstone, u.Status));
    }

    [Fact]
    public void Existing_tombstoned_folder_blocks_everything_below_it()
    {
        var state = Map(Active("arch", kind: EntryKind.Directory) with { Status = EntryStatus.Tombstone });
        var plan = Plan(Map(Dir("arch"), File(@"arch\x.txt"), File("top.txt")), Map<ScanEntry>(), state);
        var a = Assert.Single(plan.Actions);
        Assert.Equal("top.txt", a.RelativePath);
    }

    [Fact]
    public void Directories_are_created_before_their_files()
    {
        var plan = Plan(Map(File(@"d\a.txt"), Dir("d"), Dir(@"d\e"), File(@"d\e\b.txt")), Map<ScanEntry>(), Map<StateEntry>());
        Assert.Equal(new[] { "d", @"d\a.txt", @"d\e", @"d\e\b.txt" }, plan.Actions.Select(a => a.RelativePath).ToArray());
        Assert.Equal(SyncActionKind.CreateDirectory, plan.Actions[0].Kind);
    }
}
