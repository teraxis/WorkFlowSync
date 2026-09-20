using WorkFlowSync.Core.Scanning;
using WorkFlowSync.Core.Watching;

namespace WorkFlowSync.Tests;

/// <summary>
/// The rules that turn a storm of events into a short list of folders (docs/plan-etap5.md §4.3, §4.8).
/// The clock is injected, so timing is asserted exactly instead of being slept through.
/// </summary>
public class ChangeQueueTests
{
    private DateTimeOffset _now = new(2026, 9, 16, 9, 0, 0, TimeSpan.Zero);

    private ChangeQueue Queue(ChangeQueue.Options? options = null, ExcludeMatcher? excludes = null) =>
        new(options ?? new ChangeQueue.Options
        {
            Quiet = TimeSpan.FromSeconds(5),
            MaxDelay = TimeSpan.FromSeconds(30),
            MaxDirectories = 200,
        }, excludes, () => _now);

    private void Advance(TimeSpan by) => _now += by;

    [Fact]
    public void Nothing_is_handed_out_while_the_file_is_still_being_written()
    {
        var q = Queue();
        q.Add(@"Тека\звіт.docx");

        Advance(TimeSpan.FromSeconds(4));
        Assert.Null(q.TakeReady());          // still within the quiet period

        Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(@"Тека", Assert.Single(q.TakeReady()!.Directories));
    }

    [Fact]
    public void Every_touch_restarts_the_quiet_period()
    {
        var q = Queue();
        for (var i = 0; i < 5; i++)
        {
            q.Add(@"Тека\великий.iso");
            Advance(TimeSpan.FromSeconds(4));
            Assert.Null(q.TakeReady());       // a long save is never scanned halfway through
        }

        Advance(TimeSpan.FromSeconds(5));
        Assert.NotNull(q.TakeReady());
    }

    [Fact]
    public void A_folder_someone_keeps_touching_is_still_synchronised_eventually()
    {
        var q = Queue();
        for (var i = 0; i < 20; i++)
        {
            q.Add(@"Тека\журнал.log");
            Advance(TimeSpan.FromSeconds(2));
        }

        // Never quiet for five seconds, but the thirty-second ceiling on patience has passed.
        Assert.Equal(@"Тека", Assert.Single(q.TakeReady()!.Directories));
    }

    [Fact]
    public void Many_files_in_one_folder_are_one_entry()
    {
        var q = Queue();
        for (var i = 0; i < 5000; i++) q.Add($@"Тека\файл{i}.txt");
        Advance(TimeSpan.FromSeconds(6));

        var batch = q.TakeReady()!;

        Assert.Single(batch.Directories);
        Assert.False(batch.NeedsFullPass);
    }

    [Fact]
    public void Too_many_folders_at_once_becomes_a_full_pass()
    {
        var q = Queue();
        for (var i = 0; i < 250; i++) q.Add($@"Тека{i}\файл.txt");
        Advance(TimeSpan.FromSeconds(6));

        var batch = q.TakeReady()!;

        Assert.True(batch.NeedsFullPass);
        Assert.Empty(batch.Directories);
        Assert.Contains("folders changed at once", batch.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_lost_notification_is_answered_with_a_full_pass_straight_away()
    {
        var q = Queue();
        q.Add(@"Тека\файл.txt");

        q.RequestFullPass("watching interrupted: InternalBufferOverflowException");
        var batch = q.TakeReady()!;          // no waiting: there is nothing to settle

        Assert.True(batch.NeedsFullPass);
        Assert.Empty(batch.Directories);
        Assert.False(q.FullPassWanted);      // taking it clears it
    }

    [Theory]
    [InlineData(@"Тека\~$звіт.docx")]
    [InlineData(@"Тека\ABCD.tmp")]
    [InlineData(@"Тека\.~lock.звіт.odt")]
    [InlineData(@"Тека\файл.crdownload")]
    [InlineData(@"Тека\звіт.docx.wfs-tmp")]
    [InlineData(@"Тека\.нотатка.swp")]
    public void Editor_and_download_droppings_never_enter_the_queue(string path)
    {
        var q = Queue();

        Assert.True(q.IsNoise(path));
        q.Add(path);
        Advance(TimeSpan.FromSeconds(10));

        Assert.Null(q.TakeReady());
    }

    [Fact]
    public void The_users_own_excludes_are_honoured_too()
    {
        var q = Queue(excludes: new ExcludeMatcher(new[] { @"\Архів" }));

        q.Add(@"Архів\старе.docx");
        q.Add(@"Тека\нове.docx");
        Advance(TimeSpan.FromSeconds(6));

        Assert.Equal(@"Тека", Assert.Single(q.TakeReady()!.Directories));
    }

    [Fact]
    public void A_real_document_is_not_mistaken_for_a_temporary_file()
    {
        var q = Queue();

        Assert.False(q.IsNoise(@"Тека\звіт.docx"));
        Assert.False(q.IsNoise(@"Тека\темп.txt"));        // "темп" is not "*.tmp"
        Assert.False(q.IsNoise(@"Тека\партія.xlsx"));     // not "*.part"
    }

    [Fact]
    public void Folders_come_back_parents_first()
    {
        var q = Queue();
        q.Add(@"А\Б\В\глибокий.txt");
        q.Add(@"А\мілкий.txt");
        q.Add(@"А\Б\середній.txt");
        Advance(TimeSpan.FromSeconds(6));

        var dirs = q.TakeReady()!.Directories;

        Assert.Equal(new[] { @"А", @"А\Б", @"А\Б\В" }, dirs);
    }

    [Fact]
    public void A_nested_path_narrows_to_the_folder_that_holds_it()
    {
        Assert.Equal(@"Тека", ChangeQueue.DirectoryOf(@"Тека\файл.txt"));
        Assert.Equal(@"Тека\Під", ChangeQueue.DirectoryOf(@"Тека\Під\файл.txt"));
        Assert.Equal(@"Тека", ChangeQueue.DirectoryOf(@"Тека/файл.txt"));
    }

    [Fact]
    public void A_top_level_name_is_returned_as_a_candidate_for_the_caller_to_resolve()
    {
        // Writing a file into «Тека» also raises an event for «Тека» itself. Collapsing that to "the root"
        // turned every save into a full pass and partial passes never ran — so the name is passed on and the
        // loop checks whether it is really a folder.
        Assert.Equal(@"Тека", ChangeQueue.DirectoryOf(@"Тека"));
        Assert.Equal("верхній.txt", ChangeQueue.DirectoryOf("верхній.txt"));
    }

    [Fact]
    public void Taking_a_batch_empties_it()
    {
        var q = Queue();
        q.Add(@"Тека\файл.txt");
        Advance(TimeSpan.FromSeconds(6));

        Assert.NotNull(q.TakeReady());
        Assert.Null(q.TakeReady());
        Assert.Equal(0, q.PendingDirectories);
    }

    [Fact]
    public void Only_what_has_settled_is_handed_over()
    {
        var q = Queue();
        q.Add(@"Тиха\файл.txt");
        Advance(TimeSpan.FromSeconds(6));
        q.Add(@"Жвава\файл.txt");            // touched just now

        var batch = q.TakeReady()!;

        Assert.Equal(@"Тиха", Assert.Single(batch.Directories));
        Assert.Equal(1, q.PendingDirectories);
    }
}
