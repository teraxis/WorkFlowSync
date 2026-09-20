using WorkFlowSync.Core.Model;
using WorkFlowSync.Core.Planning;
using WorkFlowSync.Core.Scanning;

namespace WorkFlowSync.Tests;

/// <summary>
/// The intake window (maxAge) of docs/product/features/first-seen-and-retention.md: what is copied at all,
/// decided before a byte moves. Its whole point is that it is NOT auto-clean — it only ever declines to copy.
/// </summary>
public class IntakeWindowTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Month = TimeSpan.FromDays(30);
    private static readonly DateTimeOffset Ancient = Now.AddDays(-400);
    private static readonly DateTimeOffset Fresh = Now.AddDays(-2);

    private static ScanEntry File(string path, DateTimeOffset stamp) =>
        new() { RelativePath = path, Kind = EntryKind.File, Size = 10, MtimeUtc = stamp, CtimeUtc = stamp };

    private static ScanEntry Dir(string path, DateTimeOffset stamp) =>
        new() { RelativePath = path, Kind = EntryKind.Directory, MtimeUtc = stamp, CtimeUtc = stamp };

    private static StateEntry Row(string path, DateTimeOffset firstSeen, long size = 10) => new()
    {
        PairName = "p", RelativePath = path, Kind = EntryKind.File, Status = EntryStatus.Active,
        FirstSeenUtc = firstSeen, LastSeenUtc = firstSeen,
        SourceSize = size, SourceMtimeUtc = firstSeen, CopiedSize = size, CopiedMtimeUtc = firstSeen,
    };

    private static Dictionary<string, ScanEntry> Scan(params ScanEntry[] e) => e.ToDictionary(x => x.RelativePath, x => x, StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, StateEntry> State(params StateEntry[] e) => e.ToDictionary(x => x.RelativePath, x => x, StringComparer.OrdinalIgnoreCase);

    /// <summary>The bug this window exists to fix: on a first pass an old file used to be copied, then binned.</summary>
    [Fact]
    public void An_old_file_is_never_copied_on_the_first_pass_while_a_fresh_one_is()
    {
        var source = Scan(File("старе.docx", Ancient), File("нове.docx", Fresh));

        var plan = new SyncPlanner("p", Now, backfillFirstSeen: true, maxAge: Month)
            .Plan(source, Scan(), State());

        var action = Assert.Single(plan.Actions);
        Assert.Equal(SyncActionKind.CopyFile, action.Kind);
        Assert.Equal("нове.docx", action.RelativePath);
        Assert.Equal(1, plan.Stats.New);
        Assert.Equal(1, plan.Stats.TooOld);
        Assert.Equal(10, plan.Stats.BytesToCopy);       // only the fresh file's bytes are ever moved
    }

    [Fact]
    public void Without_a_window_everything_is_copied_as_before()
    {
        var source = Scan(File("старе.docx", Ancient), File("нове.docx", Fresh));
        var plan = new SyncPlanner("p", Now, backfillFirstSeen: true).Plan(source, Scan(), State());
        Assert.Equal(2, plan.Stats.New);
        Assert.Equal(0, plan.Stats.TooOld);
    }

    /// <summary>Nothing leaves the target because of the filter — that is auto-clean's job, and it is off here.</summary>
    [Fact]
    public void A_copy_that_ages_past_the_window_is_kept_and_simply_stops_being_updated()
    {
        var changedInSource = File("a.docx", Now);                    // source edited the file today
        var plan = new SyncPlanner("p", Now, false, maxAge: Month)
            .Plan(Scan(changedInSource), Scan(File("a.docx", Ancient)), State(Row("a.docx", Ancient)));

        Assert.Empty(plan.Actions);                                   // no update, and above all no recycle
        Assert.Empty(plan.StateUpdates);
        Assert.Equal(1, plan.Stats.TooOld);
        Assert.Equal(0, plan.Stats.Expired);
    }

    [Fact]
    public void A_file_still_inside_the_window_is_updated_normally()
    {
        var changedInSource = new ScanEntry { RelativePath = "a.docx", Kind = EntryKind.File, Size = 99, MtimeUtc = Now, CtimeUtc = Fresh };
        var plan = new SyncPlanner("p", Now, false, maxAge: Month)
            .Plan(Scan(changedInSource), Scan(File("a.docx", Fresh)), State(Row("a.docx", Fresh)));

        Assert.Equal(SyncActionKind.UpdateFile, Assert.Single(plan.Actions).Kind);
        Assert.Equal(0, plan.Stats.TooOld);
    }

    /// <summary>A folder is never judged by its own age: a new report dropped into «Архів 2019» must arrive.</summary>
    [Fact]
    public void An_old_folder_holding_a_fresh_file_is_created_but_an_entirely_old_folder_is_not()
    {
        var source = Scan(
            Dir("Архів", Ancient), File(@"Архів\старе.docx", Ancient),
            Dir("Робоче", Ancient), File(@"Робоче\нове.docx", Fresh));

        var plan = new SyncPlanner("p", Now, backfillFirstSeen: true, maxAge: Month).Plan(source, Scan(), State());

        var paths = plan.Actions.Select(a => a.RelativePath).ToArray();
        Assert.Equal(new[] { "Робоче", @"Робоче\нове.docx" }, paths);
        Assert.Equal(2, plan.Stats.New);                              // the folder and its one fresh file
        Assert.Equal(1, plan.Stats.TooOld);
    }

    [Fact]
    public void A_chain_of_folders_left_empty_by_the_window_collapses_completely()
    {
        var source = Scan(Dir("a", Ancient), Dir(@"a\b", Ancient), Dir(@"a\b\c", Ancient), File(@"a\b\c\старе.docx", Ancient));
        var plan = new SyncPlanner("p", Now, backfillFirstSeen: true, maxAge: Month).Plan(source, Scan(), State());

        Assert.Empty(plan.Actions);
        Assert.Equal(0, plan.Stats.New);
    }

    /// <summary>
    /// A folder that already exists locally is not a candidate for pruning — the pass must not care why it is
    /// there, only that removing it was never asked for.
    /// </summary>
    [Fact]
    public void A_folder_that_already_exists_locally_is_left_alone()
    {
        var source = Scan(Dir("d", Ancient), File(@"d\старе.docx", Ancient));
        var target = Scan(File(@"d\моє.docx", Fresh));                // something of the user's lives in there
        var plan = new SyncPlanner("p", Now, backfillFirstSeen: true, maxAge: Month).Plan(source, target, State());

        Assert.Equal(new[] { "d" }, plan.Actions.Select(a => a.RelativePath).ToArray());
        Assert.Equal(SyncActionKind.CreateDirectory, plan.Actions[0].Kind);
    }

    /// <summary>
    /// The file's own ctime/mtime only stands in for an appearance date on the pair's very first pass. Once the
    /// pair is being tracked, "appeared" means "we saw it arrive" — so a five-year-old document copied into the
    /// source today is new, and the window lets it through. That is the rule the customer asked for, and it is
    /// what keeps the window from quietly refusing the archive somebody just put there on purpose.
    /// </summary>
    [Fact]
    public void After_the_first_pass_age_is_when_we_noticed_the_file_not_the_file_s_own_date()
    {
        var justDropped = File("архів-2019.docx", Ancient);           // old timestamps, appeared right now
        var alreadyTracked = State(Row("інше.docx", Fresh));          // state is not empty → no backfill

        var plan = new SyncPlanner("p", Now, backfillFirstSeen: false, maxAge: Month)
            .Plan(Scan(justDropped, File("інше.docx", Fresh)), Scan(File("інше.docx", Fresh)), alreadyTracked);

        var action = Assert.Single(plan.Actions);
        Assert.Equal("архів-2019.docx", action.RelativePath);
        Assert.Equal(Now, action.Proposed.FirstSeenUtc);
        Assert.Equal(0, plan.Stats.TooOld);
    }

    /// <summary>
    /// Per-item and needing nothing but the item itself, so unlike auto-clean the filter is safe in a partial
    /// pass. A scoped pass never backfills (an empty scope looks like an empty pair), so what it filters is
    /// what it already has a recorded date for.
    /// </summary>
    [Fact]
    public void The_window_still_applies_in_a_scoped_pass()
    {
        var changedInSource = new ScanEntry { RelativePath = @"Тека\старе.docx", Kind = EntryKind.File, Size = 99, MtimeUtc = Now, CtimeUtc = Ancient };
        var plan = new SyncPlanner("p", Now, backfillFirstSeen: false, maxAge: Month, scope: PlanScope.For("Тека"))
            .Plan(Scan(changedInSource), Scan(File(@"Тека\старе.docx", Ancient)), State(Row(@"Тека\старе.docx", Ancient)));

        Assert.Empty(plan.Actions);
        Assert.Equal(1, plan.Stats.TooOld);
    }

    /// <summary>
    /// The refusal has to be remembered, or it is not a refusal at all: with no row, the next pass meets the
    /// same file with nothing on record, dates it "today" and copies the archive this pass just declined.
    /// </summary>
    [Fact]
    public void A_refused_file_is_recorded_so_later_passes_keep_refusing_it()
    {
        var source = Scan(File("старе.docx", Ancient));

        var first = new SyncPlanner("p", Now, backfillFirstSeen: true, maxAge: Month).Plan(source, Scan(), State());

        var row = Assert.Single(first.StateUpdates);
        Assert.Equal(EntryStatus.TooOld, row.Status);
        Assert.Equal(Ancient, row.FirstSeenUtc);                      // the date the file itself carried
        Assert.Empty(first.Actions);

        // Second pass, days later, state no longer empty (so no backfill): still refused, on OUR record.
        var later = Now.AddDays(3);
        var second = new SyncPlanner("p", later, backfillFirstSeen: false, maxAge: Month)
            .Plan(source, Scan(), State(row));

        Assert.Empty(second.Actions);
        Assert.Equal(1, second.Stats.TooOld);
        Assert.Equal(0, second.Stats.New);
    }

    [Fact]
    public void Widening_the_window_lets_a_refused_file_in_on_the_next_pass()
    {
        var source = Scan(File("старе.docx", Ancient));
        var refused = new SyncPlanner("p", Now, backfillFirstSeen: true, maxAge: Month)
            .Plan(source, Scan(), State()).StateUpdates.Single();

        var admitted = new SyncPlanner("p", Now, backfillFirstSeen: false, maxAge: TimeSpan.FromDays(500))
            .Plan(source, Scan(), State(refused));

        var action = Assert.Single(admitted.Actions);
        Assert.Equal(SyncActionKind.CopyFile, action.Kind);
        Assert.Equal(EntryStatus.Active, action.Proposed.Status);
        Assert.Equal(Ancient, action.Proposed.FirstSeenUtc);          // the date it appeared does not move
        Assert.Equal(1, admitted.Stats.New);
    }

    /// <summary>A held-back file that disappears from the source leaves no row behind to accumulate.</summary>
    [Fact]
    public void A_refused_file_deleted_in_the_source_is_forgotten()
    {
        var refused = new SyncPlanner("p", Now, backfillFirstSeen: true, maxAge: Month)
            .Plan(Scan(File("старе.docx", Ancient)), Scan(), State()).StateUpdates.Single();

        var plan = new SyncPlanner("p", Now, false, maxAge: Month).Plan(Scan(), Scan(), State(refused));

        Assert.Equal("старе.docx", Assert.Single(plan.Forget));
        Assert.Empty(plan.Actions);
    }

    /// <summary>
    /// Delete a pair and add it again: its state rows are gone (a new name means a new pair), but the target
    /// still holds the whole earlier mirror. Every old file therefore arrives as "present on both sides,
    /// unknown to us" — adoption, not a copy. Adopting means "ours, keep it in sync", which is precisely what
    /// the window forbids, so this path has to be gated too. Reported from real use; the window looked dead.
    /// </summary>
    [Fact]
    public void Re_adding_a_pair_does_not_adopt_files_the_window_excludes()
    {
        var source = Scan(File("старе.docx", Ancient), File("нове.docx", Fresh));
        var target = Scan(File("старе.docx", Ancient), File("нове.docx", Fresh));   // left over from the old pair

        var plan = new SyncPlanner("p-2", Now, backfillFirstSeen: true, maxAge: Month).Plan(source, target, State());

        Assert.Empty(plan.Actions);                                   // nothing copied, and nothing removed
        Assert.Equal(1, plan.Stats.Adopted);
        Assert.Equal(1, plan.Stats.TooOld);

        var old = plan.StateUpdates.Single(u => u.RelativePath == "старе.docx");
        Assert.Equal(EntryStatus.TooOld, old.Status);
        Assert.Equal(EntryStatus.Active, plan.StateUpdates.Single(u => u.RelativePath == "нове.docx").Status);
    }

    /// <summary>
    /// Same pair name = same pair, so its memory survives on purpose (that is what keeps a locally deleted
    /// file from coming back). Turning the window on afterwards must still stop the old files being synced.
    /// </summary>
    [Fact]
    public void Turning_the_window_on_for_an_existing_pair_stops_updating_its_old_files()
    {
        var changedInSource = new ScanEntry { RelativePath = "старе.docx", Kind = EntryKind.File, Size = 99, MtimeUtc = Now, CtimeUtc = Ancient };

        var plan = new SyncPlanner("p", Now, backfillFirstSeen: false, maxAge: Month)
            .Plan(Scan(changedInSource), Scan(File("старе.docx", Ancient)), State(Row("старе.docx", Ancient)));

        Assert.Empty(plan.Actions);
        Assert.Equal(1, plan.Stats.TooOld);
    }

    /// <summary>
    /// The two windows are independent: a narrow intake filter must not start binning what is already here,
    /// and auto-clean must not start copying. Only rows auto-clean owns are recycled.
    /// </summary>
    [Fact]
    public void The_two_windows_do_not_borrow_each_others_behaviour()
    {
        var source = Scan(File("старе.docx", Ancient), File("нове.docx", Fresh));
        var target = Scan(File("давнє.docx", Ancient));
        var state = State(Row("давнє.docx", Ancient));

        var filterOnly = new SyncPlanner("p", Now, backfillFirstSeen: true, maxAge: Month).Plan(source, target, state);
        Assert.Equal(new[] { "нове.docx" }, filterOnly.Actions.Select(a => a.RelativePath).ToArray());
        Assert.Equal(0, filterOnly.Stats.Expired);

        var both = new SyncPlanner("p", Now, backfillFirstSeen: true, maxAge: Month, autoClean: Month).Plan(source, target, state);
        Assert.Contains(both.Actions, a => a.Kind == SyncActionKind.CopyFile && a.RelativePath == "нове.docx");
        Assert.Contains(both.Actions, a => a.Kind == SyncActionKind.RecycleFile && a.RelativePath == "давнє.docx");
        Assert.Equal(1, both.Stats.Expired);
    }
}
