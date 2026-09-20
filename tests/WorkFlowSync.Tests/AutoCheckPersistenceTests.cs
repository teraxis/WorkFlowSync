using WorkFlowSync.App.Services;
using WorkFlowSync.App.ViewModels;
using WorkFlowSync.Core.Config;

namespace WorkFlowSync.Tests;

/// <summary>
/// The pair's own play/pause state drives automatic checking — there is no separate switch
/// (docs/product/features/gui.md, «Папки»).
/// </summary>
[Trait("Category", "E2E")]
public sealed class AutoCheckPersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wfs-auto-" + Guid.NewGuid().ToString("N"));
    private readonly string _configPath;

    public AutoCheckPersistenceTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        Directory.CreateDirectory(Path.Combine(_root, "src2"));
        _configPath = Path.Combine(_root, "config.json");
        ConfigFile.Save(new SyncConfig
        {
            Pairs =
            {
                new FolderPair { Name = "a", Source = Path.Combine(_root, "src"), Target = Path.Combine(_root, "dst") },
                new FolderPair { Name = "b", Source = Path.Combine(_root, "src2"), Target = Path.Combine(_root, "dst2"), Enabled = false },
            },
            Interval = TimeSpan.FromMinutes(30),
        }, _configPath);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Checker_runs_while_at_least_one_pair_plays_and_stops_when_all_are_paused()
    {
        var vm = new MainViewModel(_configPath);
        Assert.True(vm.Background.IsActive);                       // pair "a" plays → checking is on by itself
        Assert.Contains("завдань: 1", vm.Run.BackgroundStatus);

        vm.Pairs[0].Enabled = false;
        vm.FollowPairStates();
        Assert.False(vm.Background.IsActive);
        Assert.Contains("Усі завдання на паузі", vm.Run.BackgroundStatus);

        vm.Pairs[1].Enabled = true;
        vm.FollowPairStates();
        Assert.True(vm.Background.IsActive);
        vm.Background.Stop();
    }

    [Fact]
    public async Task Toggling_a_pair_saves_the_state_and_it_survives_a_restart()
    {
        var vm = new MainViewModel(_configPath);
        Assert.Equal("⏸", vm.Pairs[0].ToggleGlyph);
        Assert.Equal("▶", vm.Pairs[1].ToggleGlyph);
        Assert.Equal("активне", vm.Pairs[0].StateSummary);
        Assert.Equal("на паузі", vm.Pairs[1].StateSummary);

        await vm.TogglePairAsync(vm.Pairs[0]);                     // pause "a"
        Assert.False(vm.Pairs[0].Enabled);
        Assert.False(SyncConfig.Load(_configPath).Pairs[0].Enabled);   // written immediately
        vm.Background.Stop();

        var restarted = new MainViewModel(_configPath);
        Assert.False(restarted.Pairs[0].Enabled);
        Assert.False(restarted.Background.IsActive);                   // both paused → nothing runs

        await restarted.TogglePairAsync(restarted.Pairs[1]);           // play "b" → runs at once
        Assert.True(SyncConfig.Load(_configPath).Pairs[1].Enabled);
        Assert.True(restarted.Background.IsActive);
        Assert.Equal("активне", restarted.Pairs[1].StateSummary);
        Assert.Equal("⏸", restarted.Pairs[1].ToggleGlyph);
        restarted.Background.Stop();
    }

    [Fact]
    public async Task Every_edit_is_written_immediately_there_is_no_save_button()
    {
        var vm = new MainViewModel(_configPath);
        vm.Background.Stop();

        vm.Pairs[0].IntervalMinutes = 2;   // the schedule lives on the pair now
        vm.SaveNow();
        Assert.Equal(2, SyncConfig.Load(_configPath).Pairs[0].Interval!.Value.TotalMinutes);   // on disk already

        vm.Theme = AppTheme.Dark;
        Assert.Equal(AppTheme.Dark, SyncConfig.Load(_configPath).Theme);

        await vm.TogglePairAsync(vm.Pairs[1]);                      // play "b"
        var saved = SyncConfig.Load(_configPath);
        Assert.True(saved.Pairs[1].Enabled);
        Assert.Equal(2, saved.Pairs[0].Interval!.Value.TotalMinutes);   // earlier edit survived the later write
        Assert.DoesNotContain("Зберегти", vm.StatusText);           // nothing to nag about
        vm.Background.Stop();
    }

    [Fact]
    public void Old_configs_without_the_field_keep_pairs_playing()
    {
        var cfg = SyncConfig.Parse("""{ "pairs": [ { "name": "x", "source": "s", "target": "t" } ] }""");
        Assert.True(cfg.Pairs[0].Enabled);
    }
}
