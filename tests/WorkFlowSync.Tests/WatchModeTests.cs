using WorkFlowSync.App.ViewModels;
using WorkFlowSync.Core;
using WorkFlowSync.Core.Config;

namespace WorkFlowSync.Tests;

/// <summary>
/// Stage 5.2 (docs/plan-etap5.md §4.1): a pair knows what kind of storage it sits on and whether it may
/// watch for changes. Nothing about synchronisation changes yet — only classification and what is shown.
/// </summary>
public class WatchModeTests
{
    [Fact]
    public void A_config_written_before_this_feature_reads_as_auto()
    {
        const string json = """
        { "pairs": [ { "name": "vrp", "source": "S:\\src", "target": "D:\\dst" } ] }
        """;

        var cfg = SyncConfig.Parse(json);

        Assert.Equal(WatchMode.Auto, cfg.Pairs[0].Watch);
    }

    [Fact]
    public void Watch_mode_survives_a_round_trip_through_the_config_file()
    {
        var cfg = new SyncConfig { Pairs = { new FolderPair { Name = "p", Source = @"S:\a", Target = @"D:\b", Watch = WatchMode.Off } } };

        var reloaded = SyncConfig.Parse(cfg.ToJson());

        Assert.Equal(WatchMode.Off, reloaded.Pairs[0].Watch);
        // Property names keep their C# spelling; only enum VALUES are camel-cased. Reading is case-insensitive,
        // so a hand-edited config.json may use "watch" just as well.
        Assert.Contains("\"Watch\": \"off\"", cfg.ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public void Watch_mode_travels_through_the_view_model_unchanged()
    {
        var pair = new FolderPair { Name = "p", Source = @"S:\a", Target = @"D:\b", Watch = WatchMode.On };

        var back = PairViewModel.FromModel(pair).ToModel();

        Assert.Equal(WatchMode.On, back.Watch);
        Assert.Equal(WatchMode.On, PairViewModel.FromModel(pair).Clone().Watch);
    }

    [Fact]
    public void A_fixed_drive_is_recognised_without_touching_the_network()
    {
        // The system drive is the one path every machine running these tests is guaranteed to have.
        var system = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))!;

        Assert.Equal(RootKind.Local, RootProbe.QuickKind(system));
    }

    [Theory]
    [InlineData(@"\\server\share\folder")]   // a UNC share needs a real probe to tell LAN from internet
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData(@"Z:\nothing\here")]         // an unmapped letter: nothing to classify
    public void What_cannot_be_told_from_the_path_alone_is_reported_as_unknown(string? path) =>
        Assert.Null(RootProbe.QuickKind(path));

    [Fact]
    public void The_pair_card_says_what_kind_of_storage_each_side_is()
    {
        var system = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))!;
        var vm = new PairViewModel { Source = @"\\server\share", Target = system };

        Assert.Equal("мережева", vm.SourceKindSummary);
        Assert.Equal("локальна", vm.TargetKindSummary);
        Assert.Contains("вихідна: мережева", vm.Facts, StringComparison.Ordinal);
    }

    [Fact]
    public void The_cloud_files_setting_is_offered_only_where_it_can_matter()
    {
        var local = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))!;

        // Placeholders live on a local disk, and only the side that gets READ matters. A mirror reads its
        // source and never its target, so a network source means the setting can never apply.
        Assert.False(new PairViewModel { Source = @"\\server\share", Target = local }.CloudFilesApplies);

        // …but a mirror FROM a local folder can meet one, which the earlier "two-way only" rule got wrong.
        Assert.True(new PairViewModel { Source = local, Target = @"\\server\share" }.CloudFilesApplies);

        // Two-way reads both sides, so one local side is enough.
        Assert.True(new PairViewModel { Source = @"\\server\share", Target = local, Mode = SyncMode.TwoWay }.CloudFilesApplies);
        Assert.False(new PairViewModel { Source = @"\\a\b", Target = @"\\c\d", Mode = SyncMode.TwoWay }.CloudFilesApplies);
    }

    [Fact]
    public void When_the_setting_cannot_apply_the_hint_says_why()
    {
        var vm = new PairViewModel { Source = @"\\server\share", Target = @"D:\Дзеркало" };

        Assert.Contains("не застосовується", vm.CloudFilesHint, StringComparison.Ordinal);
        Assert.Contains("мережеву теку", vm.CloudFilesHint, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_path_shows_a_dash_rather_than_guessing()
    {
        var vm = new PairViewModel();

        Assert.Equal("—", vm.SourceKindSummary);
        Assert.Equal("—", vm.TargetKindSummary);
    }

    [Fact]
    public void Only_a_deliberate_watch_choice_is_spelled_out_on_the_card()
    {
        var vm = new PairViewModel { Source = @"S:\a", Target = @"D:\b" };

        Assert.DoesNotContain("стежити:", vm.Facts, StringComparison.Ordinal);

        vm.Watch = WatchMode.Off;
        Assert.Contains("стежити: ні", vm.Facts, StringComparison.Ordinal);

        vm.Watch = WatchMode.On;
        Assert.Contains("стежити: завжди", vm.Facts, StringComparison.Ordinal);
    }

    [Fact]
    public void The_hint_explains_that_watching_only_shortens_the_wait()
    {
        var vm = new PairViewModel();
        Assert.True(string.IsNullOrEmpty(vm.WatchHint));
    }
}
