using WorkFlowSync.Core;
using WorkFlowSync.Core.Config;
using WorkFlowSync.Core.Logging;

namespace WorkFlowSync.Tests;

[Trait("Category", "E2E")]
public sealed class ResidentModeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wfs-loop-" + Guid.NewGuid().ToString("N"));

    public ResidentModeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Pass_lock_is_exclusive_per_config_and_released_on_dispose()
    {
        var cfg = Path.Combine(_root, "config.json");
        using (var first = PassLock.TryAcquire(cfg))
        {
            Assert.True(first.Acquired);
            using var second = PassLock.TryAcquire(cfg);
            Assert.False(second.Acquired);
            using var other = PassLock.TryAcquire(Path.Combine(_root, "other.json"));
            Assert.True(other.Acquired);
        }
        using var again = PassLock.TryAcquire(cfg);
        Assert.True(again.Acquired);
    }

    [Fact]
    public async Task Loop_runs_passes_on_the_interval_and_picks_up_config_edits()
    {
        var src = Path.Combine(_root, "src");
        var dst = Path.Combine(_root, "dst");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "a.txt"), "a");
        var configPath = Path.Combine(_root, "config.json");
        ConfigFile.Save(new SyncConfig { Pairs = { new FolderPair { Name = "t", Source = src, Target = dst } } }, configPath);

        var log = new MemorySyncLog();
        var loop = new LoopRunner(configPath, log) { IntervalOverride = TimeSpan.FromMilliseconds(300), MaxPasses = 3 };
        var passes = 0;
        loop.PassCompleted += r =>
        {
            passes++;
            if (passes == 1)
            {
                // Edit the config between passes: the loop must re-read it.
                var src2 = Path.Combine(_root, "src2");
                Directory.CreateDirectory(src2);
                File.WriteAllText(Path.Combine(src2, "b.txt"), "b");
                var cfg = SyncConfig.Load(configPath);
                cfg.Pairs.Add(new FolderPair { Name = "t2", Source = src2, Target = Path.Combine(_root, "dst2") });
                ConfigFile.Save(cfg, configPath);
            }
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await loop.RunAsync(cts.Token);

        Assert.Equal(3, loop.PassesRun);
        Assert.Equal("a", File.ReadAllText(Path.Combine(dst, "a.txt")));
        Assert.Equal("b", File.ReadAllText(Path.Combine(_root, "dst2", "b.txt")));
        Assert.Contains(log.Lines, l => l.Message.Contains("loop end  passes=3"));
    }

    [Fact]
    public async Task Loop_skips_a_pass_while_another_process_holds_the_lock_and_stops_on_cancel()
    {
        var configPath = Path.Combine(_root, "config.json");
        ConfigFile.Save(new SyncConfig { Pairs = { new FolderPair { Name = "t", Source = _root, Target = Path.Combine(_root, "x") } } }, configPath);
        var log = new MemorySyncLog();
        var loop = new LoopRunner(configPath, log) { IntervalOverride = TimeSpan.FromMilliseconds(200) };

        using var held = PassLock.TryAcquire(configPath);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(700));
        await loop.RunAsync(cts.Token);

        Assert.Equal(0, loop.PassesRun);
        Assert.Contains(log.Lines, l => l.Level == LogLevel.Warn && l.Message.Contains("another pass is running"));
        Assert.Contains(log.Lines, l => l.Message.StartsWith("loop end"));
    }

    [Fact]
    public void Log_files_older_than_the_retention_are_pruned()
    {
        var dir = Path.Combine(_root, "logs");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "wfs-2026-01-01.log"), "old");
        File.WriteAllText(Path.Combine(dir, "wfs-2026-09-10.log"), "recent");
        File.WriteAllText(Path.Combine(dir, "crash-2026-01-01.log"), "keep");   // not a daily log

        var removed = FileSyncLog.Prune(dir, keepDays: 30, today: new DateTime(2026, 9, 14));

        Assert.Equal(1, removed);
        Assert.False(File.Exists(Path.Combine(dir, "wfs-2026-01-01.log")));
        Assert.True(File.Exists(Path.Combine(dir, "wfs-2026-09-10.log")));
        Assert.True(File.Exists(Path.Combine(dir, "crash-2026-01-01.log")));
    }

    [Fact]
    public void Startup_shortcut_is_created_in_the_given_folder_and_removed()
    {
        var startup = Path.Combine(_root, "Startup");
        var exe = Path.Combine(_root, "wfs.exe");
        File.WriteAllText(exe, "stub");
        var configPath = Path.Combine(_root, "config.json");

        Autostart.EnableStartupShortcut(configPath, startup, exe);
        var lnk = Autostart.ShortcutPath(startup);
        Assert.True(File.Exists(lnk));
        Assert.True(Autostart.IsStartupShortcutEnabled(startup));
        Assert.True(new FileInfo(lnk).Length > 100);

        Autostart.DisableStartupShortcut(startup);
        Assert.False(Autostart.IsStartupShortcutEnabled(startup));
    }

    [Fact]
    public void Schtasks_arguments_run_as_current_user_only_when_logged_on()
    {
        var args = Autostart.BuildSchtasksCreateArgs(@"C:\Tools\WorkFlowSync\config.json", 30, @"C:\Tools\WorkFlowSync\wfs.exe");
        Assert.Contains("/SC MINUTE /MO 30", args);
        Assert.Contains("sync --once", args);
        Assert.DoesNotContain("/RU", args);
        Assert.DoesNotContain("/RL", args);
        Assert.Contains("/MO 1 ", Autostart.BuildSchtasksCreateArgs("c.json", 0, "x.exe"));
    }
}
