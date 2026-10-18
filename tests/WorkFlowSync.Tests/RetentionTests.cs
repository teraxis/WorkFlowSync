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
        CopiedAtUtc = kind == EntryKind.File ? firstSeen : null,
    };

    private static Dictionary<string, ScanEntry> Scan(params ScanEntry[] e) => e.ToDictionary(x => x.RelativePath, x => x, StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, StateEntry> State(params StateEntry[] e) => e.ToDictionary(x => x.RelativePath, x => x, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Expired_untouched_file_is_recycled_and_tombstoned_even_if_source_still_has_it()
    {
        var plan = new SyncPlanner("p", Now, false, autoClean: Year).Plan(Scan(File("a.txt")), Scan(File("a.txt")), State(Row("a.txt", Old)));
        var a = Assert.Single(plan.Actions);
        Assert.Equal(SyncActionKind.RecycleFile, a.Kind);
        Assert.Equal(EntryStatus.Tombstone, a.Proposed.Status);
        Assert.Equal(1, plan.Stats.Expired);
    }

    [Fact]
    public void Expired_file_already_deleted_in_source_is_recycled_too()
    {
        var plan = new SyncPlanner("p", Now, false, autoClean: Year).Plan(Scan(), Scan(File("a.txt")), State(Row("a.txt", Old)));
        Assert.Equal(SyncActionKind.RecycleFile, Assert.Single(plan.Actions).Kind);
    }

    [Fact]
    public void Fresh_file_and_no_retention_are_left_alone()
    {
        var fresh = new SyncPlanner("p", Now, false, autoClean: Year).Plan(Scan(File("a.txt")), Scan(File("a.txt")), State(Row("a.txt", Fresh)));
        Assert.Empty(fresh.Actions);
        var forever = new SyncPlanner("p", Now, false, autoClean: null).Plan(Scan(File("a.txt")), Scan(File("a.txt")), State(Row("a.txt", Old)));
        Assert.Empty(forever.Actions);
    }

    [Fact]
    public void Locally_modified_files_never_expire()
    {
        var modified = Row("a.txt", Old, status: EntryStatus.LocalModified);
        var plan = new SyncPlanner("p", Now, false, autoClean: Year).Plan(Scan(File("a.txt")), Scan(File("a.txt")), State(modified));
        Assert.Empty(plan.Actions);
        Assert.Empty(plan.StateUpdates);

        // Active but edited locally right now: becomes local_modified, not recycled.
        var edited = new ScanEntry { RelativePath = "b.txt", Kind = EntryKind.File, Size = 99, MtimeUtc = Now, CtimeUtc = Old };
        var plan2 = new SyncPlanner("p", Now, false, autoClean: Year).Plan(Scan(File("b.txt")), Scan(edited), State(Row("b.txt", Old)));
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

        var plan = new SyncPlanner("p", Now, false, autoClean: Year).Plan(source, target, state);

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
        var plan = new SyncPlanner("p", Now, false, autoClean: Year).Plan(source, target, state);
        Assert.Single(plan.Actions);
        Assert.Equal(SyncActionKind.RecycleFile, plan.Actions[0].Kind);
    }

    [Fact]
    public void Nested_empty_directories_are_recycled_deepest_first()
    {
        var source = Scan(Dir("a"), Dir(@"a\b"), File(@"a\b\x.txt"));
        var target = Scan(Dir("a"), Dir(@"a\b"), File(@"a\b\x.txt"));
        var state = State(Row("a", Old, EntryKind.Directory), Row(@"a\b", Old, EntryKind.Directory), Row(@"a\b\x.txt", Old));
        var plan = new SyncPlanner("p", Now, false, autoClean: Year).Plan(source, target, state);
        Assert.Equal(new[] { @"a\b\x.txt", @"a\b", "a" }, plan.Actions.Select(x => x.RelativePath).ToArray());
    }

    [Fact]
    public void Keep_latest_files_recycles_oldest_files_when_count_exceeds_limit()
    {
        var t1 = Now.AddDays(-10);
        var t2 = Now.AddDays(-5);
        var t3 = Now.AddDays(-2);
        var t4 = Now.AddDays(-1);

        var source = Scan(File("f1.txt"), File("f2.txt"), File("f3.txt"), File("f4.txt"));
        var target = Scan(File("f1.txt"), File("f2.txt"), File("f3.txt"), File("f4.txt"));
        var state = State(
            Row("f1.txt", t1),
            Row("f2.txt", t2),
            Row("f3.txt", t3),
            Row("f4.txt", t4));

        var plan = new SyncPlanner("p", Now, false, keepLatestFiles: 2).Plan(source, target, state);

        var recycled = plan.Actions.Where(a => a.Kind == SyncActionKind.RecycleFile).Select(a => a.RelativePath).OrderBy(x => x).ToList();
        Assert.Equal(new[] { "f1.txt", "f2.txt" }, recycled);
        Assert.Equal(2, plan.Stats.Expired);
        Assert.Equal(2, plan.Stats.Unchanged);
    }

    [Fact]
    public void Keep_latest_files_preserves_newest_incoming_files_and_recycles_older_target_files()
    {
        var oldDate = Now.AddDays(-10);
        var midDate = Now.AddDays(-5);

        // source has existing midDate, oldDate, plus a new file "new.txt"
        var source = Scan(File("f1.txt"), File("f2.txt"), File("new.txt"));
        var target = Scan(File("f1.txt"), File("f2.txt"));
        var state = State(Row("f1.txt", oldDate), Row("f2.txt", midDate));

        // Limit to 2 newest files: "new.txt" (Now) and "f2.txt" (midDate) are kept; "f1.txt" (oldDate) is recycled
        var plan = new SyncPlanner("p", Now, false, keepLatestFiles: 2).Plan(source, target, state);

        var copyActions = plan.Actions.Where(a => a.Kind == SyncActionKind.CopyFile).Select(a => a.RelativePath).ToList();
        var recycleActions = plan.Actions.Where(a => a.Kind == SyncActionKind.RecycleFile).Select(a => a.RelativePath).ToList();

        Assert.Equal(new[] { "new.txt" }, copyActions);
        Assert.Equal(new[] { "f1.txt" }, recycleActions);
        Assert.Equal(1, plan.Stats.New);
        Assert.Equal(1, plan.Stats.Expired);
        Assert.Equal(1, plan.Stats.Unchanged);
    }

    [Fact]
    public void Keep_latest_files_never_touches_locally_modified_files()
    {
        var oldDate = Now.AddDays(-10);
        var source = Scan(File("f1.txt"), File("f2.txt"));
        var target = Scan(File("f1.txt"), File("f2.txt"));
        var state = State(
            Row("f1.txt", oldDate, status: EntryStatus.LocalModified),
            Row("f2.txt", oldDate));

        // limit = 1: f1.txt is local_modified so it is never pruned. f2.txt is active intact so it can be kept.
        var plan = new SyncPlanner("p", Now, false, keepLatestFiles: 1).Plan(source, target, state);
        Assert.Empty(plan.Actions);
    }

    [Fact]
    public void Keep_latest_files_recycles_emptied_directories()
    {
        var oldDate = Now.AddDays(-10);
        var newDate = Now.AddDays(-1);

        var source = Scan(Dir("old_dir"), File(@"old_dir\old.txt"), File("new.txt"));
        var target = Scan(Dir("old_dir"), File(@"old_dir\old.txt"), File("new.txt"));
        var state = State(
            Row("old_dir", oldDate, EntryKind.Directory),
            Row(@"old_dir\old.txt", oldDate),
            Row("new.txt", newDate));

        // Limit = 1: keeps "new.txt", recycles "old_dir\old.txt" and "old_dir"
        var plan = new SyncPlanner("p", Now, false, keepLatestFiles: 1).Plan(source, target, state);

        var kinds = plan.Actions.Select(a => (a.Kind, a.RelativePath)).ToList();
        Assert.Contains((SyncActionKind.RecycleFile, @"old_dir\old.txt"), kinds);
        Assert.Contains((SyncActionKind.RecycleEmptyDirectory, "old_dir"), kinds);
        Assert.Equal(1, plan.Stats.EmptyDirs);
    }

    [Fact]
    public void Keep_latest_files_combines_with_auto_clean()
    {
        var expiredDate = Now.AddDays(-400); // older than 365 days
        var oldDate = Now.AddDays(-50);      // within 365 days
        var newDate = Now.AddDays(-5);       // fresh

        var source = Scan(File("f1.txt"), File("f2.txt"), File("f3.txt"));
        var target = Scan(File("f1.txt"), File("f2.txt"), File("f3.txt"));
        var state = State(
            Row("f1.txt", expiredDate),
            Row("f2.txt", oldDate),
            Row("f3.txt", newDate));

        // autoClean expires f1.txt (> 365d); keepLatestFiles = 1 further prunes f2.txt, leaving f3.txt
        var plan = new SyncPlanner("p", Now, false, autoClean: Year, keepLatestFiles: 1).Plan(source, target, state);

        var recycled = plan.Actions.Where(a => a.Kind == SyncActionKind.RecycleFile).Select(a => a.RelativePath).OrderBy(x => x).ToList();
        Assert.Equal(new[] { "f1.txt", "f2.txt" }, recycled);
        Assert.Equal(2, plan.Stats.Expired);
        Assert.Equal(1, plan.Stats.Unchanged);
    }

    [Fact]
    public void Keep_latest_files_preserves_existing_files_when_clean_existing_is_false()
    {
        var cutoff = Now.AddHours(-1);
        var oldDate = cutoff.AddDays(-10);

        var source = Scan(File("f1.txt"), File("f2.txt"), File("f3.txt"));
        var target = Scan(File("f1.txt"), File("f2.txt"), File("f3.txt"));
        var state = State(
            Row("f1.txt", oldDate),
            Row("f2.txt", oldDate),
            Row("f3.txt", oldDate));

        // When cleanExistingFiles is false, existing files prior to cutoff are protected
        var planPreserved = new SyncPlanner("p", Now, false, keepLatestFiles: 1, cleanExistingFiles: false, cleanupFromUtc: cutoff)
            .Plan(source, target, state);
        Assert.Empty(planPreserved.Actions.Where(a => a.Kind == SyncActionKind.RecycleFile));

        // When cleanExistingFiles is true, surplus existing files are pruned
        var planCleaned = new SyncPlanner("p", Now, false, keepLatestFiles: 1, cleanExistingFiles: true, cleanupFromUtc: null)
            .Plan(source, target, state);
        Assert.Equal(2, planCleaned.Actions.Count(a => a.Kind == SyncActionKind.RecycleFile));
    }

    [Fact]
    public void Auto_clean_preserves_existing_files_when_clean_existing_is_false()
    {
        var cutoff = Now.AddHours(-1);
        var expiredDate = cutoff.AddDays(-400);

        var source = Scan(File("old.txt"));
        var target = Scan(File("old.txt"));
        var state = State(Row("old.txt", expiredDate));

        // cleanExistingFiles false => protected
        var planPreserved = new SyncPlanner("p", Now, false, autoClean: Year, cleanExistingFiles: false, cleanupFromUtc: cutoff)
            .Plan(source, target, state);
        Assert.Empty(planPreserved.Actions);

        // cleanExistingFiles true => expired
        var planCleaned = new SyncPlanner("p", Now, false, autoClean: Year, cleanExistingFiles: true, cleanupFromUtc: null)
            .Plan(source, target, state);
        Assert.Single(planCleaned.Actions, a => a.Kind == SyncActionKind.RecycleFile);
    }

    [Fact]
    public void Cleanup_identifies_files_strictly_by_sync_time_not_ctime_or_mtime()
    {
        var ancientFsDate = Now.AddYears(-5); // 5 years old on disk
        var recentFsDate = Now.AddHours(-2);   // 2 hours old on disk

        var syncDateRecent = Now.AddDays(-2);  // synced 2 days ago
        var syncDateOld = Now.AddDays(-60);    // synced 60 days ago

        // file1 has ancient ctime/mtime (5 years old), but was synced 2 days ago
        var scan1 = new ScanEntry { RelativePath = "f1.txt", Kind = EntryKind.File, Size = 10, CtimeUtc = ancientFsDate, MtimeUtc = ancientFsDate };
        var state1 = new StateEntry
        {
            PairName = "p", RelativePath = "f1.txt", Kind = EntryKind.File, Status = EntryStatus.Active,
            FirstSeenUtc = syncDateRecent, LastSeenUtc = syncDateRecent,
            SourceSize = 10, SourceMtimeUtc = ancientFsDate,
            CopiedSize = 10, CopiedMtimeUtc = ancientFsDate,
            CopiedAtUtc = syncDateRecent,
        };

        // file2 has recent ctime/mtime (2 hours old), but was synced 60 days ago
        var scan2 = new ScanEntry { RelativePath = "f2.txt", Kind = EntryKind.File, Size = 10, CtimeUtc = recentFsDate, MtimeUtc = recentFsDate };
        var state2 = new StateEntry
        {
            PairName = "p", RelativePath = "f2.txt", Kind = EntryKind.File, Status = EntryStatus.Active,
            FirstSeenUtc = syncDateOld, LastSeenUtc = syncDateOld,
            SourceSize = 10, SourceMtimeUtc = recentFsDate,
            CopiedSize = 10, CopiedMtimeUtc = recentFsDate,
            CopiedAtUtc = syncDateOld,
        };

        var source = Scan(scan1, scan2);
        var target = Scan(scan1, scan2);
        var state = State(state1, state2);

        // Auto-clean with 30 days window:
        // f1.txt (synced 2 days ago) MUST NOT be expired despite 5-year-old ctime/mtime
        // f2.txt (synced 60 days ago) MUST be expired despite 2-hour-old ctime/mtime
        var autoCleanPlan = new SyncPlanner("p", Now, false, autoClean: TimeSpan.FromDays(30)).Plan(source, target, state);
        var expired = Assert.Single(autoCleanPlan.Actions);
        Assert.Equal(SyncActionKind.RecycleFile, expired.Kind);
        Assert.Equal("f2.txt", expired.RelativePath);

        // Keep latest 1 file:
        // f1.txt (synced 2 days ago) is NEWER by sync time than f2.txt (synced 60 days ago)
        // Therefore, f1.txt is KEPT and f2.txt is RECYCLED
        var keepLatestPlan = new SyncPlanner("p", Now, false, keepLatestFiles: 1).Plan(source, target, state);
        var pruned = Assert.Single(keepLatestPlan.Actions);
        Assert.Equal(SyncActionKind.RecycleFile, pruned.Kind);
        Assert.Equal("f2.txt", pruned.RelativePath);
    }
}

