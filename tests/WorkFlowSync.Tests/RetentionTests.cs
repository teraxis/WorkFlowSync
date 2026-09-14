using WorkFlowSync.Core.Model;
using WorkFlowSync.Core.Planning;
using WorkFlowSync.Core.Scanning;

namespace WorkFlowSync.Tests;

/// <summary>Retention rows of docs/product/features/first-seen-and-retention.md (planner level).</summary>
public class RetentionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Year = TimeSpan.FromDays(365);
    private static readonly DateTimeOffset Old = Now.AddDays(-400);
    private static readonly DateTimeOffset Fresh = Now.AddDays(-30);

    private static ScanEntry File(string path) => new() { RelativePath = path, Kind = EntryKind.File, Size = 10, MtimeUtc = Old, CtimeUtc = Old };
    private static ScanEntry Dir(string path) => new() { RelativePath = path, Kind = EntryKind.Directory, MtimeUtc = Old, CtimeUtc = Old };

    private static StateEntry Row(string path, DateTimeOffset firstSeen, EntryKind kind = EntryKind.File, EntryStatus status = EntryStatus.Active) => new()
    {
        PairName = "p", RelativePath = path, Kind = kind, Status = status, FirstSeenUtc = firstSeen, LastSeenUtc = firstSeen,
        SourceSize = kind == EntryKind.File ? 10 : null, SourceMtimeUtc = kind == EntryKind.File ? Old : null,
        CopiedSize = kind == EntryKind.File ? 10 : null, CopiedMtimeUtc = kind == EntryKind.File ? Old : null,
    };

    private static Dictionary<string, ScanEntry> Scan(params ScanEntry[] e) => e.ToDictionary(x => x.RelativePath, x => x, StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, StateEntry> State(params StateEntry[] e) => e.ToDictionary(x => x.RelativePath, x => x, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Expired_untouched_file_is_recycled_and_tombstoned_even_if_source_still_has_it()
    {
        var plan = new SyncPlanner("p", Now, false, Year).Plan(Scan(File("a.txt")), Scan(File("a.txt")), State(Row("a.txt", Old)));
        var a = Assert.Single(plan.Actions);
        Assert.Equal(SyncActionKind.RecycleFile, a.Kind);
        Assert.Equal(EntryStatus.Tombstone, a.Proposed.Status);
        Assert.Equal(1, plan.Stats.Expired);
    }

    [Fact]
    public void Expired_file_already_deleted_in_source_is_recycled_too()
    {
        var plan = new SyncPlanner("p", Now, false, Year).Plan(Scan(), Scan(File("a.txt")), State(Row("a.txt", Old)));
        Assert.Equal(SyncActionKind.RecycleFile, Assert.Single(plan.Actions).Kind);
    }

    [Fact]
    public void Fresh_file_and_no_retention_are_left_alone()
    {
        var fresh = new SyncPlanner("p", Now, false, Year).Plan(Scan(File("a.txt")), Scan(File("a.txt")), State(Row("a.txt", Fresh)));
        Assert.Empty(fresh.Actions);
        var forever = new SyncPlanner("p", Now, false, null).Plan(Scan(File("a.txt")), Scan(File("a.txt")), State(Row("a.txt", Old)));
        Assert.Empty(forever.Actions);
    }

    [Fact]
    public void Locally_modified_files_never_expire()
    {
        var modified = Row("a.txt", Old, status: EntryStatus.LocalModified);
        var plan = new SyncPlanner("p", Now, false, Year).Plan(Scan(File("a.txt")), Scan(File("a.txt")), State(modified));
        Assert.Empty(plan.Actions);
        Assert.Empty(plan.StateUpdates);

        // Active but edited locally right now: becomes local_modified, not recycled.
        var edited = new ScanEntry { RelativePath = "b.txt", Kind = EntryKind.File, Size = 99, MtimeUtc = Now, CtimeUtc = Old };
        var plan2 = new SyncPlanner("p", Now, false, Year).Plan(Scan(File("b.txt")), Scan(edited), State(Row("b.txt", Old)));
        Assert.Empty(plan2.Actions);
        Assert.Equal(EntryStatus.LocalModified, Assert.Single(plan2.StateUpdates).Status);
    }

    [Fact]
    public void Directory_emptied_by_retention_is_recycled_and_forgotten_but_directory_with_fresh_file_stays()
    {
        var source = Scan(Dir("old"), File(@"old\a.txt"), Dir("mixed"), File(@"mixed\a.txt"), File(@"mixed\new.txt"));
        var target = Scan(Dir("old"), File(@"old\a.txt"), Dir("mixed"), File(@"mixed\a.txt"), File(@"mixed\new.txt"));
        var state = State(
            Row("old", Old, EntryKind.Directory), Row(@"old\a.txt", Old),
            Row("mixed", Old, EntryKind.Directory), Row(@"mixed\a.txt", Old), Row(@"mixed\new.txt", Fresh));

        var plan = new SyncPlanner("p", Now, false, Year).Plan(source, target, state);

        var kinds = plan.Actions.Select(a => (a.Kind, a.RelativePath)).ToList();
        Assert.Contains((SyncActionKind.RecycleFile, @"old\a.txt"), kinds);
        Assert.Contains((SyncActionKind.RecycleFile, @"mixed\a.txt"), kinds);
        Assert.Contains((SyncActionKind.RecycleEmptyDirectory, "old"), kinds);
        Assert.DoesNotContain(kinds, k => k.RelativePath == "mixed" && k.Kind == SyncActionKind.RecycleEmptyDirectory);
        // Files before their directory so the executor empties bottom-up.
        Assert.True(kinds.FindIndex(k => k.RelativePath == @"old\a.txt") < kinds.FindIndex(k => k.RelativePath == "old"));
        Assert.Equal(1, plan.Stats.EmptyDirs);
    }

    [Fact]
    public void Directory_with_a_foreign_local_file_is_kept()
    {
        var source = Scan(Dir("d"), File(@"d\a.txt"));
        var target = Scan(Dir("d"), File(@"d\a.txt"), File(@"d\mine.txt"));   // mine.txt is not ours
        var state = State(Row("d", Old, EntryKind.Directory), Row(@"d\a.txt", Old));
        var plan = new SyncPlanner("p", Now, false, Year).Plan(source, target, state);
        Assert.Single(plan.Actions);
        Assert.Equal(SyncActionKind.RecycleFile, plan.Actions[0].Kind);
    }

    [Fact]
    public void Nested_empty_directories_are_recycled_deepest_first()
    {
        var source = Scan(Dir("a"), Dir(@"a\b"), File(@"a\b\x.txt"));
        var target = Scan(Dir("a"), Dir(@"a\b"), File(@"a\b\x.txt"));
        var state = State(Row("a", Old, EntryKind.Directory), Row(@"a\b", Old, EntryKind.Directory), Row(@"a\b\x.txt", Old));
        var plan = new SyncPlanner("p", Now, false, Year).Plan(source, target, state);
        Assert.Equal(new[] { @"a\b\x.txt", @"a\b", "a" }, plan.Actions.Select(x => x.RelativePath).ToArray());
    }
}
