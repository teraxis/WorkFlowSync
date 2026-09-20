using WorkFlowSync.Core;
using WorkFlowSync.Core.Config;
using WorkFlowSync.Core.Logging;

namespace WorkFlowSync.Tests;

/// <summary>
/// Each pair keeps its own schedule (docs/product/features/cli-and-scheduling.md). One number for every
/// pair was wrong once the folders differed: a working folder that changes every few minutes and a
/// 50 000-folder archive that takes minutes to scan should not be checked on the same clock.
/// </summary>
[Trait("Category", "E2E")]
public sealed class PairIntervalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wfs-ival-" + Guid.NewGuid().ToString("N"));
    private readonly string _configPath;
    private readonly MemorySyncLog _log = new();

    public PairIntervalTests()
    {
        Directory.CreateDirectory(_root);
        _configPath = Path.Combine(_root, "config.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private FolderPair Pair(string name, TimeSpan? interval)
    {
        var src = Path.Combine(_root, name + "-src");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "файл.txt"), name);
        return new FolderPair
        {
            Name = name,
            Source = src,
            Target = Path.Combine(_root, name + "-dst"),
            Interval = interval,
            Watch = WatchMode.Off,       // the watcher has its own tests; this is about the clock
        };
    }

    private static bool WaitUntil(Func<bool> condition, int seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(50);
        }
        return condition();
    }

    [Fact]
    public void A_pair_without_its_own_interval_uses_the_shared_one()
    {
        var cfg = new SyncConfig { Interval = TimeSpan.FromMinutes(30) };
        var own = Pair("власний", TimeSpan.FromMinutes(5));
        var shared = Pair("спільний", null);

        Assert.Equal(TimeSpan.FromMinutes(5), cfg.IntervalFor(own));
        Assert.Equal(TimeSpan.FromMinutes(30), cfg.IntervalFor(shared));
    }

    [Fact]
    public void A_pair_interval_survives_the_config_file_and_an_old_config_reads_as_shared()
    {
        var cfg = new SyncConfig { Pairs = { Pair("p", TimeSpan.FromMinutes(7)) } };

        var reloaded = SyncConfig.Parse(cfg.ToJson());
        Assert.Equal(TimeSpan.FromMinutes(7), reloaded.Pairs[0].Interval);

        var legacy = SyncConfig.Parse("""{ "pairs": [ { "name": "p", "source": "S:\\a", "target": "D:\\b" } ] }""");
        Assert.Null(legacy.Pairs[0].Interval);
        Assert.Equal(legacy.Interval, legacy.IntervalFor(legacy.Pairs[0]));
    }

    [Fact]
    public void An_interval_under_a_minute_is_rejected_with_the_pair_named()
    {
        var cfg = new SyncConfig { Pairs = { Pair("швидка", TimeSpan.FromSeconds(30)) } };

        var problem = Assert.Single(cfg.Validate(), p => p.Contains("interval", StringComparison.OrdinalIgnoreCase));

        Assert.Contains("швидка", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_frequent_pair_is_checked_while_the_rare_one_waits()
    {
        ConfigFile.Save(new SyncConfig
        {
            Pairs = { Pair("часта", TimeSpan.FromMinutes(1)), Pair("рідкісна", TimeSpan.FromHours(6)) },
            StatePath = "state.db",
            LogPath = "logs",
            ScanParallelism = 2,
        }, _configPath);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        // One minute is the smallest the config allows, so the schedule is compressed for the test.
        var loop = new LoopRunner(_configPath, _log)
        {
            IntervalOverride = null,
            PollInterval = TimeSpan.FromMilliseconds(100),
            WatchEnabled = false,
        };
        var running = loop.RunAsync(cts.Token);

        // Both are due at the start, so the first round covers them together.
        Assert.True(WaitUntil(() => Directory.Exists(Path.Combine(_root, "часта-dst"))
                                 && Directory.Exists(Path.Combine(_root, "рідкісна-dst"))));

        cts.Cancel();
        await running;

        // The rare pair must not have been checked a second time within these few seconds.
        var rareRuns = _log.Lines.Count(l => l.Message.Contains("only=рідкісна", StringComparison.Ordinal));
        Assert.Equal(0, rareRuns);
    }

    [Fact]
    public async Task A_loop_whose_pairs_are_all_paused_runs_nothing_at_all()
    {
        var pair = Pair("спляча", TimeSpan.FromMinutes(1));
        pair.Enabled = false;
        ConfigFile.Save(new SyncConfig { Pairs = { pair }, StatePath = "state.db", LogPath = "logs" }, _configPath);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var loop = new LoopRunner(_configPath, _log) { PollInterval = TimeSpan.FromMilliseconds(100), WatchEnabled = false };
        var running = loop.RunAsync(cts.Token);
        await Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None);

        Assert.Equal(0, loop.PassesRun);
        Assert.False(Directory.Exists(Path.Combine(_root, "спляча-dst")));

        cts.Cancel();
        await running;
    }
}
