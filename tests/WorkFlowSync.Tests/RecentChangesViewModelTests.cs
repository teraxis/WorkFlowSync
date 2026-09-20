using WorkFlowSync.Core;
using WorkFlowSync.Core.I18n;
using WorkFlowSync.Core.Config;
using WorkFlowSync.App.ViewModels;
using WorkFlowSync.Core.Model;
using WorkFlowSync.Core.State;

namespace WorkFlowSync.Tests;

/// <summary>
/// What the tray window shows (docs/plan-etap5.md §5.6): the list itself and the way times are written,
/// which is the part a person reads rather than parses.
/// </summary>
[Collection("I18n Tests")]
public class RecentChangesViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wfs-recentvm-" + Guid.NewGuid().ToString("N"));
    private readonly string _dbPath;
    private readonly DateTimeOffset _now = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    public RecentChangesViewModelTests()
    {
        I18n.Instance.SetLanguage("uk");
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "state.db");
    }

    public void Dispose()
    {
        I18n.Instance.SetLanguage("uk");
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private void Given(params (string Pair, string Path, DateTimeOffset Seen)[] rows)
    {
        using var store = new StateStore(_dbPath);
        store.Upsert(rows.Select(r => new StateEntry
        {
            PairName = r.Pair, RelativePath = r.Path, Kind = EntryKind.File, Status = EntryStatus.Active,
            FirstSeenUtc = r.Seen, LastSeenUtc = r.Seen, SourceSize = 2048,
        }));
    }

    private RecentChangesViewModel Model(params (string Pair, string Target)[] pairs) =>
        new(() => _dbPath, () => pairs.ToDictionary(p => p.Pair, p => p.Target, StringComparer.OrdinalIgnoreCase));

    [Fact]
    public void A_file_shows_its_name_its_folder_and_where_it_can_be_opened()
    {
        Given(("vrp", @"Засідання\протокол.docx", _now));
        var vm = Model(("vrp", @"D:\Дзеркало"));

        vm.Load();

        var item = Assert.Single(vm.Items);
        Assert.Equal("протокол.docx", item.Name);
        Assert.Equal(@"vrp · Засідання", item.Where);
        Assert.Equal(@"D:\Дзеркало\Засідання\протокол.docx", item.FullPath);
        Assert.True(item.CanOpen);
        Assert.Equal("2 KB", item.SizeText);
    }

    [Fact]
    public void A_file_at_the_top_of_a_pair_still_says_which_pair()
    {
        Given(("робота", "нотатка.txt", _now));

        var vm = Model(("робота", @"D:\Робота"));
        vm.Load();

        Assert.Equal("робота", Assert.Single(vm.Items).Where);
    }

    [Fact]
    public void A_row_whose_pair_is_gone_is_still_listed_but_cannot_be_opened()
    {
        // The pair was deleted from the settings; its history is still in the state database.
        Given(("зникла", "файл.docx", _now));

        var vm = Model();
        vm.Load();

        var item = Assert.Single(vm.Items);
        Assert.False(item.CanOpen);
        Assert.Null(item.FullPath);
    }

    [Fact]
    public void An_empty_history_says_so_rather_than_showing_a_blank_box()
    {
        var vm = Model();

        vm.Load();

        Assert.True(vm.IsEmpty);
        Assert.Equal("Поки нічого нового", vm.Summary);
    }

    [Fact]
    public void A_missing_database_is_reported_instead_of_taking_the_tray_down()
    {
        var vm = new RecentChangesViewModel(() => Path.Combine(_dir, "нема", "?", "state.db"),
            () => new Dictionary<string, string>());

        vm.Load();   // must not throw

        Assert.True(vm.IsEmpty);
    }

    [Fact]
    public void The_summary_counts_what_is_shown_and_admits_when_there_is_more()
    {
        Given(Enumerable.Range(0, RecentChangesViewModel.DefaultLimit + 5)
            .Select(i => ("vrp", $"файл{i:D3}.docx", _now.AddMinutes(-i))).ToArray());

        var vm = Model(("vrp", @"D:\Дзеркало"));
        vm.Load();

        Assert.Equal(RecentChangesViewModel.DefaultLimit, vm.Items.Count);
        Assert.Contains("і більше", vm.Summary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, "щойно")]
    [InlineData(30, "щойно")]
    [InlineData(90, "1 хв тому")]
    [InlineData(59 * 60, "59 хв тому")]
    public void Recent_times_are_written_the_way_a_person_would_say_them(int secondsAgo, string expected) =>
        Assert.Equal(expected, RecentChangesViewModel.Humanise(_now.AddSeconds(-secondsAgo), _now));

    [Fact]
    public void Older_times_fall_back_to_the_clock_and_then_to_the_date()
    {
        var now = new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero).ToLocalTime();

        // Earlier today: the time is enough.
        Assert.Matches(@"^\d{2}:\d{2}$", RecentChangesViewModel.Humanise(now.AddHours(-5), now));
        Assert.StartsWith("вчора, ", RecentChangesViewModel.Humanise(now.AddDays(-1), now), StringComparison.Ordinal);
        Assert.DoesNotContain("вчора", RecentChangesViewModel.Humanise(now.AddDays(-5), now), StringComparison.Ordinal);
    }

    [Fact]
    public void A_clock_that_runs_backwards_does_not_produce_a_negative_age()
    {
        // Clock changes and timezone shifts happen; "-3 хв тому" must never appear.
        var text = RecentChangesViewModel.Humanise(_now.AddMinutes(5), _now);

        Assert.DoesNotContain("-", text, StringComparison.Ordinal);
        Assert.Matches(@"^\d{2}:\d{2}$", text);
    }
}

/// <summary>The configurable length of the history list (settings → «Файлів у списку змін»).</summary>
public class RecentLimitTests
{
    [Fact]
    public void A_config_without_the_setting_lists_forty()
    {
        var cfg = SyncConfig.Parse("""{ "pairs": [] }""");

        Assert.Equal(RecentChangesViewModel.DefaultLimit, cfg.RecentLimit);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(501)]
    public void A_nonsensical_length_is_rejected_by_validation(int limit)
    {
        var cfg = new SyncConfig
        {
            Pairs = { new FolderPair { Name = "p", Source = @"S:\a", Target = @"D:\b" } },
            RecentLimit = limit,
        };

        Assert.Contains(cfg.Validate(), p => p.Contains("RecentLimit", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(40)]
    [InlineData(500)]
    public void The_allowed_range_passes(int limit)
    {
        var cfg = new SyncConfig
        {
            Pairs = { new FolderPair { Name = "p", Source = @"S:\a", Target = @"D:\b" } },
            RecentLimit = limit,
        };

        Assert.DoesNotContain(cfg.Validate(), p => p.Contains("RecentLimit", StringComparison.Ordinal));
    }
}
