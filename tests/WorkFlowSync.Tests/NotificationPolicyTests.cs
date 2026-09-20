using WorkFlowSync.Core.Config;
using WorkFlowSync.Core.Notifications;

namespace WorkFlowSync.Tests;

/// <summary>
/// When the program is allowed to interrupt, and with what words (docs/plan-etap5.md §5.6b).
/// The governing idea under test: a tool that talks too much gets muted, and a muted tool cannot warn
/// you when it matters.
/// </summary>
public class NotificationPolicyTests
{
    private static readonly DateTimeOffset Noon = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Midnight = new(2026, 9, 16, 0, 30, 0, TimeSpan.Zero);

    private static SyncConfig Config(NotifyMode mode = NotifyMode.All, int from = 0, int to = 0) =>
        new() { Notifications = mode, QuietHoursFrom = from, QuietHoursTo = to };

    [Fact]
    public void One_message_per_pass_however_many_files_it_carried()
    {
        var note = NotificationPolicy.ForPass(Config(), added: 12, errors: 0, heldBack: 0, Noon);

        Assert.NotNull(note);
        Assert.Equal("Додано 12 нових файлів", note!.Body);
        Assert.False(note.IsProblem);
    }

    [Fact]
    public void A_pass_that_changed_nothing_says_nothing()
    {
        Assert.Null(NotificationPolicy.ForPass(Config(), added: 0, errors: 0, heldBack: 0, Noon));
    }

    [Theory]
    [InlineData(1, "Додано новий файл")]
    [InlineData(2, "Додано 2 нові файли")]
    [InlineData(4, "Додано 4 нові файли")]
    [InlineData(5, "Додано 5 нових файлів")]
    [InlineData(11, "Додано 11 нових файлів")]   // 11–14 take the many form despite ending in 1
    [InlineData(12, "Додано 12 нових файлів")]
    [InlineData(21, "Додано 21 нові файли")]
    [InlineData(101, "Додано 101 нові файли")]
    public void Counts_are_written_in_Ukrainian_rather_than_bolted_on(int added, string expected) =>
        Assert.Equal(expected, NotificationPolicy.ForPass(Config(), added, 0, 0, Noon)!.Body);

    [Fact]
    public void Problems_are_announced_instead_of_the_good_news()
    {
        var note = NotificationPolicy.ForPass(Config(), added: 5, errors: 2, heldBack: 0, Noon);

        Assert.True(note!.IsProblem);
        Assert.Contains("Не вдалося обробити 2 файли", note.Body, StringComparison.Ordinal);
        Assert.Contains("журнал", note.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void Removals_that_were_held_back_are_worth_interrupting_for()
    {
        // The user needs to know their files are still there AND that the program noticed something odd.
        var note = NotificationPolicy.ForPass(Config(), added: 0, errors: 0, heldBack: 200, Noon);

        Assert.True(note!.IsProblem);
        Assert.Contains("200", note.Body, StringComparison.Ordinal);
        Assert.Contains("повториться", note.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void Switched_off_means_silence_even_about_problems()
    {
        Assert.Null(NotificationPolicy.ForPass(Config(NotifyMode.Off), added: 9, errors: 3, heldBack: 5, Noon));
    }

    [Fact]
    public void Errors_only_skips_the_routine_news_but_keeps_the_warnings()
    {
        Assert.Null(NotificationPolicy.ForPass(Config(NotifyMode.ErrorsOnly), added: 9, errors: 0, heldBack: 0, Noon));
        Assert.NotNull(NotificationPolicy.ForPass(Config(NotifyMode.ErrorsOnly), added: 0, errors: 1, heldBack: 0, Noon));
    }

    [Fact]
    public void Quiet_hours_hold_back_the_news_but_never_a_problem()
    {
        var night = Config(from: 22, to: 8);

        Assert.Null(NotificationPolicy.ForPass(night, added: 7, errors: 0, heldBack: 0, Midnight));
        Assert.NotNull(NotificationPolicy.ForPass(night, added: 0, errors: 1, heldBack: 0, Midnight));
        Assert.NotNull(NotificationPolicy.ForPass(night, added: 7, errors: 0, heldBack: 0, Noon));
    }

    [Theory]
    [InlineData(22, 8, 23, true)]      // crosses midnight: late evening
    [InlineData(22, 8, 3, true)]       // …and the small hours
    [InlineData(22, 8, 8, false)]      // the end hour is already awake
    [InlineData(22, 8, 12, false)]
    [InlineData(9, 18, 12, true)]      // an ordinary daytime range
    [InlineData(9, 18, 20, false)]
    [InlineData(0, 0, 3, false)]       // equal bounds mean no quiet period at all
    [InlineData(-5, 99, 3, false)]     // nonsense in the config must not silence the program
    public void The_quiet_range_may_cross_midnight(int from, int to, int hour, bool quiet)
    {
        var now = new DateTimeOffset(2026, 9, 16, hour, 0, 0, TimeSpan.Zero);

        Assert.Equal(quiet, NotificationPolicy.InQuietHours(Config(from: from, to: to), now));
    }

    [Fact]
    public void A_config_written_before_this_feature_talks_by_default()
    {
        var cfg = SyncConfig.Parse("""{ "pairs": [ { "name": "p", "source": "S:\\a", "target": "D:\\b" } ] }""");

        Assert.Equal(NotifyMode.All, cfg.Notifications);
        Assert.False(NotificationPolicy.InQuietHours(cfg, Midnight));
    }
}
