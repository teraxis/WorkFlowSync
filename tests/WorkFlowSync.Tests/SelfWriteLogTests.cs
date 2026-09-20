using WorkFlowSync.Core.Watching;

namespace WorkFlowSync.Tests;

/// <summary>
/// Suppressing our own footsteps (docs/plan-etap5.md §4.3, rule 7). Without this a pair ping-pongs: the
/// pass writes, the watcher sees it, another pass writes again.
/// </summary>
public class SelfWriteLogTests
{
    private DateTimeOffset _now = new(2026, 9, 16, 9, 0, 0, TimeSpan.Zero);

    private SelfWriteLog New() => new(() => _now);

    [Fact]
    public void A_path_we_just_wrote_is_recognised_as_ours()
    {
        var log = New();
        log.Record(@"D:\Дзеркало\звіт.docx");

        Assert.True(log.WasOurs(@"D:\Дзеркало\звіт.docx"));
    }

    [Fact]
    public void A_path_nobody_recorded_is_somebody_elses_change()
    {
        Assert.False(New().WasOurs(@"D:\Дзеркало\чужий.docx"));
    }

    [Fact]
    public void One_write_is_suppressed_however_many_notifications_it_produces()
    {
        // A single File.WriteAllText raises Created and then Changed — on an SMB share one creation was
        // measured producing six events. If the note were consumed by the first, the rest would start a pass.
        var log = New();
        log.Record(@"D:\Дзеркало\звіт.docx");

        for (var i = 0; i < 6; i++)
            Assert.True(log.WasOurs(@"D:\Дзеркало\звіт.docx"), $"notification {i + 1} of the same write");
    }

    [Fact]
    public void The_blind_spot_closes_when_the_window_does()
    {
        // The price of the rule above: an edit at the same path inside the window is taken for ours and
        // waits for the next full pass. It must not last a second longer than the window.
        var log = New();
        log.Record(@"D:\Дзеркало\звіт.docx");
        Assert.True(log.WasOurs(@"D:\Дзеркало\звіт.docx"));

        _now += SelfWriteLog.Window + TimeSpan.FromMilliseconds(1);

        Assert.False(log.WasOurs(@"D:\Дзеркало\звіт.docx"));
    }

    [Fact]
    public void A_note_nobody_claimed_stops_suppressing_once_the_window_passes()
    {
        var log = New();
        log.Record(@"D:\Дзеркало\звіт.docx");

        _now += SelfWriteLog.Window + TimeSpan.FromSeconds(1);

        Assert.False(log.WasOurs(@"D:\Дзеркало\звіт.docx"));
    }

    [Fact]
    public void Paths_are_compared_the_way_Windows_compares_them()
    {
        var log = New();
        log.Record(@"D:\Дзеркало\Звіт.docx");

        Assert.True(log.WasOurs(@"d:\дзеркало\звіт.docx"));
    }

    [Fact]
    public void A_trailing_separator_is_the_same_path()
    {
        var log = New();
        log.Record(@"D:\Дзеркало\Тека\");

        Assert.True(log.WasOurs(@"D:\Дзеркало\Тека"));
    }

    [Fact]
    public void Pruning_clears_what_has_expired_and_keeps_the_rest()
    {
        var log = New();
        log.Record(@"D:\старий.txt");
        _now += SelfWriteLog.Window + TimeSpan.FromSeconds(1);
        log.Record(@"D:\свіжий.txt");

        log.Prune();

        Assert.Equal(1, log.Count);
        Assert.True(log.WasOurs(@"D:\свіжий.txt"));
    }

    [Fact]
    public void Empty_paths_are_ignored_rather_than_stored()
    {
        var log = New();
        log.Record("");
        log.Record("   ");

        Assert.Equal(0, log.Count);
        Assert.False(log.WasOurs(""));
    }
}
