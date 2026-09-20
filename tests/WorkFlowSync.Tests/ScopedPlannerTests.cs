using WorkFlowSync.Core.Config;
using WorkFlowSync.Core.Model;
using WorkFlowSync.Core.Planning;
using WorkFlowSync.Core.Scanning;

namespace WorkFlowSync.Tests;

/// <summary>
/// The decision table of docs/product/features/mirror-rules.md re-run with a <see cref="PlanScope"/>
/// (docs/plan-etap5.md §4.2). The rule under test is one sentence: a scoped pass may ADD and UPDATE,
/// and must never conclude that anything is gone — absence inside one folder says nothing about the pair.
///
/// Every destructive row therefore has a twin here asserting that the same input produces NOTHING.
/// </summary>
public class ScopedPlannerTests
{
    private const string Scope = @"Засідання";

    private static readonly DateTimeOffset Now = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T1 = Now.AddDays(-10);
    private static readonly DateTimeOffset T2 = Now.AddDays(-1);
    private static readonly DateTimeOffset Ancient = Now.AddYears(-5);

    private static ScanEntry File(string path, long size = 100, DateTimeOffset? mtime = null) =>
        new() { RelativePath = path, Kind = EntryKind.File, Size = size, MtimeUtc = mtime ?? T1, CtimeUtc = T1 };

    private static ScanEntry Dir(string path) => new() { RelativePath = path, Kind = EntryKind.Directory, MtimeUtc = T1, CtimeUtc = T1 };

    private static StateEntry Active(string path, long size = 100, DateTimeOffset? mtime = null,
        EntryKind kind = EntryKind.File, DateTimeOffset? firstSeen = null) => new()
    {
        PairName = "p", RelativePath = path, Kind = kind, Status = EntryStatus.Active,
        FirstSeenUtc = firstSeen ?? T1, LastSeenUtc = T1,
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

    private static SyncPlan Scoped(Dictionary<string, ScanEntry> src, Dictionary<string, ScanEntry> dst,
        Dictionary<string, StateEntry> state, TimeSpan? autoClean = null) =>
        new SyncPlanner("p", Now, backfillFirstSeen: false, autoClean: autoClean, scope: PlanScope.For(Scope)).Plan(src, dst, state);

    private static SyncPlan Full(Dictionary<string, ScanEntry> src, Dictionary<string, ScanEntry> dst,
        Dictionary<string, StateEntry> state, TimeSpan? autoClean = null) =>
        new SyncPlanner("p", Now, backfillFirstSeen: false, autoClean: autoClean).Plan(src, dst, state);

    // ---------------------------------------------------------------- what a scoped pass still does

    [Fact]
    public void A_new_file_in_the_watched_folder_is_copied()
    {
        var plan = Scoped(Map(File($@"{Scope}\новий.docx")), Map<ScanEntry>(), Map<StateEntry>());

        var action = Assert.Single(plan.Actions);
        Assert.Equal(SyncActionKind.CopyFile, action.Kind);
        Assert.Equal(Now, action.Proposed.FirstSeenUtc);
    }

    [Fact]
    public void A_file_updated_in_the_source_is_updated()
    {
        var plan = Scoped(
            Map(File($@"{Scope}\звіт.docx", size: 200, mtime: T2)),
            Map(File($@"{Scope}\звіт.docx")),
            Map(Active($@"{Scope}\звіт.docx")));

        Assert.Equal(SyncActionKind.UpdateFile, Assert.Single(plan.Actions).Kind);
    }

    [Fact]
    public void A_locally_edited_file_is_still_marked_and_never_overwritten()
    {
        // Bookkeeping, not destruction: it protects the user's copy, so a scoped pass may do it.
        var plan = Scoped(
            Map(File($@"{Scope}\звіт.docx", size: 200, mtime: T2)),
            Map(File($@"{Scope}\звіт.docx", size: 999, mtime: T2)),
            Map(Active($@"{Scope}\звіт.docx")));

        Assert.Empty(plan.Actions);
        Assert.Equal(1, plan.Stats.LocalModified);
        Assert.Equal(EntryStatus.LocalModified, Assert.Single(plan.StateUpdates).Status);
    }

    [Fact]
    public void An_unknown_file_present_on_both_sides_is_adopted()
    {
        var plan = Scoped(Map(File($@"{Scope}\старий.docx")), Map(File($@"{Scope}\старий.docx")), Map<StateEntry>());

        Assert.Empty(plan.Actions);
        Assert.Equal(1, plan.Stats.Adopted);
    }

    [Fact]
    public void A_tombstoned_file_is_still_never_copied_back()
    {
        var dead = Active($@"{Scope}\видалений.docx") with { Status = EntryStatus.Tombstone };

        var plan = Scoped(Map(File($@"{Scope}\видалений.docx")), Map<ScanEntry>(), Map(dead));

        Assert.Empty(plan.Actions);
    }

    // ---------------------------------------------------------------- what it must refuse to do

    [Fact]
    public void A_copy_the_user_deleted_locally_is_not_tombstoned()
    {
        var src = Map(File($@"{Scope}\звіт.docx"));
        var state = Map(Active($@"{Scope}\звіт.docx"));

        var scoped = Scoped(src, Map<ScanEntry>(), state);
        var full = Full(src, Map<ScanEntry>(), state);

        Assert.Empty(scoped.Actions);
        Assert.Empty(scoped.StateUpdates);
        Assert.Equal(1, scoped.Stats.DeferredToFullPass);
        // The same input in a full pass does tombstone it — the difference is the scope, not the data.
        Assert.Equal(EntryStatus.Tombstone, Assert.Single(full.StateUpdates).Status);
    }

    [Fact]
    public void An_item_gone_from_both_sides_is_not_tombstoned()
    {
        var plan = Scoped(Map<ScanEntry>(), Map<ScanEntry>(), Map(Active($@"{Scope}\зник.docx")));

        Assert.Empty(plan.Actions);
        Assert.Empty(plan.StateUpdates);
        Assert.Equal(1, plan.Stats.DeferredToFullPass);
    }

    [Fact]
    public void A_locally_modified_file_whose_copy_disappeared_is_not_tombstoned()
    {
        var edited = Active($@"{Scope}\звіт.docx") with { Status = EntryStatus.LocalModified };

        var plan = Scoped(Map(File($@"{Scope}\звіт.docx")), Map<ScanEntry>(), Map(edited));

        Assert.Empty(plan.StateUpdates);
        Assert.Equal(1, plan.Stats.DeferredToFullPass);
    }

    [Fact]
    public void Auto_clean_never_runs_in_a_scoped_pass()
    {
        var old = Active($@"{Scope}\старе.docx", firstSeen: Ancient);
        var autoClean = TimeSpan.FromDays(365);

        var scoped = Scoped(Map<ScanEntry>(), Map(File($@"{Scope}\старе.docx")), Map(old), autoClean);
        var full = Full(Map<ScanEntry>(), Map(File($@"{Scope}\старе.docx")), Map(old), autoClean);

        Assert.Empty(scoped.Actions);
        Assert.Equal(0, scoped.Stats.Expired);
        Assert.Equal(1, full.Stats.Expired);   // the full pass still expires it
    }

    [Fact]
    public void Nothing_outside_the_scope_is_touched_even_if_it_is_handed_in()
    {
        // The runner only feeds a scoped pass its own subtree; this asserts the planner does not rely on that.
        var outside = $@"Інша тека\чужий.docx";

        var plan = Scoped(
            Map(File($@"{Scope}\новий.docx"), File(outside)),
            Map<ScanEntry>(),
            Map(Active(outside)));

        var action = Assert.Single(plan.Actions);
        Assert.Equal($@"{Scope}\новий.docx", action.RelativePath);
        Assert.Empty(plan.StateUpdates);          // the outside row is neither tombstoned nor refreshed
        Assert.Equal(0, plan.Stats.DeferredToFullPass);
    }

    [Fact]
    public void A_sibling_folder_with_the_same_prefix_is_outside_the_scope()
    {
        var plan = Scoped(Map(File($@"{Scope}-архів\чужий.docx")), Map<ScanEntry>(), Map<StateEntry>());

        Assert.Empty(plan.Actions);
    }

    // ---------------------------------------------------------------- two-way

    [Fact]
    public void Two_way_deletions_do_not_travel_in_a_scoped_pass()
    {
        var a = Map<ScanEntry>();
        var b = Map(File($@"{Scope}\спільний.docx"));
        var state = Map(Active($@"{Scope}\спільний.docx"));

        var scoped = new TwoWayPlanner("p", Now, PlanScope.For(Scope)).Plan(a, b, state);
        var full = new TwoWayPlanner("p", Now).Plan(a, b, state);

        Assert.Empty(scoped.Actions);
        Assert.Equal(1, scoped.Stats.DeferredToFullPass);
        Assert.Equal(SyncActionKind.DeleteFile, Assert.Single(full.Actions).Kind);
    }

    [Fact]
    public void Two_way_additions_still_travel_in_a_scoped_pass()
    {
        var plan = new TwoWayPlanner("p", Now, PlanScope.For(Scope))
            .Plan(Map(File($@"{Scope}\новий.docx")), Map<ScanEntry>(), Map<StateEntry>());

        var action = Assert.Single(plan.Actions);
        Assert.Equal(SyncActionKind.CopyFile, action.Kind);
        Assert.Equal(SyncSide.Target, action.Side);
    }

    [Fact]
    public void Two_way_does_not_forget_a_row_it_only_half_saw()
    {
        var plan = new TwoWayPlanner("p", Now, PlanScope.For(Scope))
            .Plan(Map<ScanEntry>(), Map<ScanEntry>(), Map(Active($@"{Scope}\зник.docx")));

        Assert.Empty(plan.Forget);
        Assert.Equal(1, plan.Stats.DeferredToFullPass);
    }

    // ---------------------------------------------------------------- the scope itself

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\\")]
    public void An_empty_scope_means_the_whole_pair(string? raw) => Assert.Null(PlanScope.For(raw));

    [Theory]
    [InlineData(@"Засідання", @"Засідання", true)]
    [InlineData(@"Засідання", @"Засідання\2026\акт.docx", true)]
    [InlineData(@"Засідання", @"ЗАСІДАННЯ\акт.docx", true)]      // case-insensitive, like NTFS
    [InlineData(@"Засідання", @"Засідання-архів\акт.docx", false)]
    [InlineData(@"Засідання", @"Інша\акт.docx", false)]
    [InlineData(@"Засідання", @"Засіданн", false)]
    public void The_scope_covers_a_folder_and_its_descendants_only(string dir, string path, bool inside) =>
        Assert.Equal(inside, PlanScope.For(dir)!.Contains(path));

    [Theory]
    [InlineData(@"\Засідання\")]
    [InlineData(@"Засідання/")]
    [InlineData(@"  Засідання  ")]
    public void Separators_and_spaces_around_the_scope_are_forgiven(string raw) =>
        Assert.Equal(@"Засідання", PlanScope.For(raw)!.RelativeDir);
}
