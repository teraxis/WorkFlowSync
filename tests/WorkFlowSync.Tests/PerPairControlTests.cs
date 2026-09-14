using WorkFlowSync.App.ViewModels;
using WorkFlowSync.Core;
using WorkFlowSync.Core.Config;
using WorkFlowSync.Core.Logging;

namespace WorkFlowSync.Tests;

/// <summary>Per-pair pause / run-now (docs/product/features/gui.md, «Папки»).</summary>
[Trait("Category", "E2E")]
public sealed class PerPairControlTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wfs-pair-" + Guid.NewGuid().ToString("N"));
    private readonly string _configPath;

    public PerPairControlTests()
    {
        Directory.CreateDirectory(_root);
        _configPath = Path.Combine(_root, "config.json");
        foreach (var name in new[] { "a", "b" })
        {
            var src = Path.Combine(_root, name, "src");
            Directory.CreateDirectory(src);
            File.WriteAllText(Path.Combine(src, $"{name}.txt"), name);
        }
        ConfigFile.Save(new SyncConfig
        {
            Pairs =
            {
                new FolderPair { Name = "a", Source = Path.Combine(_root, "a", "src"), Target = Path.Combine(_root, "a", "dst") },
                new FolderPair { Name = "b", Source = Path.Combine(_root, "b", "src"), Target = Path.Combine(_root, "b", "dst"), Enabled = false },
            },
        }, _configPath);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private SyncConfig Config() => SyncConfig.Load(_configPath);
    private bool Mirrored(string name) => File.Exists(Path.Combine(_root, name, "dst", $"{name}.txt"));

    [Fact]
    public void Paused_pair_is_skipped_by_a_normal_pass()
    {
        var log = new MemorySyncLog();
        var pass = new SyncRunner(Config(), _configPath, log).Run(dryRun: false);

        Assert.Equal("a", Assert.Single(pass.Pairs).Pair);
        Assert.True(Mirrored("a"));
        Assert.False(Mirrored("b"));
        Assert.Contains(log.Lines, l => l.Message.Contains("paused: 1"));
    }

    [Fact]
    public void Explicit_single_pair_run_works_even_when_paused()
    {
        var pass = new SyncRunner(Config(), _configPath, new MemorySyncLog()).Run(dryRun: false, onlyPair: "B");

        Assert.Equal("b", Assert.Single(pass.Pairs).Pair);
        Assert.True(Mirrored("b"));
        Assert.False(Mirrored("a"));
    }

    [Fact]
    public void Unknown_pair_name_runs_nothing_and_reports_it()
    {
        var log = new MemorySyncLog();
        var pass = new SyncRunner(Config(), _configPath, log).Run(dryRun: false, onlyPair: "nope");

        Assert.Empty(pass.Pairs);
        Assert.Contains(log.Lines, l => l.Level == LogLevel.Error && l.Message.Contains("unknown pair"));
    }

    [Fact]
    public async Task Enabled_survives_the_view_model_round_trip_and_defaults_to_true()
    {
        var vm = new MainViewModel(_configPath);
        Assert.True(vm.Pairs[0].Enabled);
        Assert.False(vm.Pairs[1].Enabled);
        Assert.Equal("на паузі", vm.Pairs[1].StateSummary);
        Assert.Equal("▶", vm.Pairs[1].ToggleGlyph);

        await vm.TogglePairAsync(vm.Pairs[0]);
        Assert.False(vm.Pairs[0].Enabled);
        Assert.Equal("▶", vm.Pairs[0].ToggleGlyph);
        vm.Background.Stop();

        var reloaded = SyncConfig.Load(_configPath);
        Assert.False(reloaded.Pairs[0].Enabled);
        Assert.False(reloaded.Pairs[1].Enabled);

        // A config written without the field keeps pairs enabled.
        var legacy = SyncConfig.Parse("""{ "pairs": [ { "name": "x", "source": "s", "target": "t" } ] }""");
        Assert.True(legacy.Pairs[0].Enabled);
    }

    [Fact]
    public async Task Running_one_pair_from_the_window_mirrors_only_that_pair()
    {
        var vm = new MainViewModel(_configPath);
        await vm.RunPairAsync(vm.Pairs[1]);          // the paused one

        Assert.True(Mirrored("b"));
        Assert.False(Mirrored("a"));
        Assert.False(vm.Pairs[1].IsBusy);
        Assert.Contains("скопійовано/оновлено 1", vm.Run.LastResult);
        vm.Background.Stop();
    }
}
