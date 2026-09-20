using WorkFlowSync.Core;
using WorkFlowSync.Core.Config;
using WorkFlowSync.Core.Logging;
using WorkFlowSync.Core.Model;
using WorkFlowSync.Core.State;
using WorkFlowSync.Core.Watching;

namespace WorkFlowSync.Tests;

/// <summary>
/// The whole point of stage 5: a file dropped into a watched folder is mirrored in seconds instead of on
/// the interval (docs/plan-etap5.md §5.5b). These run the real resident loop against real folders with a
/// deliberately long interval, so anything that arrives can only have come from the watcher.
/// </summary>
[Trait("Category", "E2E")]
public sealed class InstantSyncTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wfs-instant-" + Guid.NewGuid().ToString("N"));
    private readonly string _src;
    private readonly string _dst;
    private readonly string _configPath;
    private readonly MemorySyncLog _log = new();

    public InstantSyncTests()
    {
        _src = Path.Combine(_root, "src");
        _dst = Path.Combine(_root, "dst");
        Directory.CreateDirectory(Path.Combine(_src, "Тека"));
        _configPath = Path.Combine(_root, "config.json");
        ConfigFile.Save(new SyncConfig
        {
            Pairs = { new FolderPair { Name = "t", Source = _src, Target = _dst } },
            StatePath = "state.db",
            LogPath = "logs",
            ScanParallelism = 2,
        }, _configPath);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>A loop that reacts almost immediately, so a test does not have to wait out real debounce.</summary>
    private LoopRunner Loop() => new(_configPath, _log)
    {
        // An hour: nothing here can be explained by the scheduled pass coming round again.
        IntervalOverride = TimeSpan.FromHours(1),
        PollInterval = TimeSpan.FromMilliseconds(100),
        QueueOptions = new ChangeQueue.Options
        {
            Quiet = TimeSpan.FromMilliseconds(200),
            MaxDelay = TimeSpan.FromSeconds(2),
            MaxDirectories = 200,
        },
    };

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

    private string Mirrored(string rel) => Path.Combine(_dst, rel);

    [Fact]
    public async Task A_file_dropped_into_a_watched_folder_is_mirrored_without_waiting_for_the_interval()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var loop = Loop();
        var running = loop.RunAsync(cts.Token);

        // Wait for the first scheduled pass to establish the mirror and start watching.
        Assert.True(WaitUntil(() => loop.ActiveWatchers >= 1 && Directory.Exists(_dst)), "the watcher must be up before the file is created, or the event is simply missed");

        File.WriteAllText(Path.Combine(_src, "Тека", "терміновий.docx"), "urgent");

        Assert.True(WaitUntil(() => File.Exists(Mirrored(@"Тека\терміновий.docx"))),
            "the watcher should have carried the file across long before the hour is up");
        Assert.Equal("urgent", File.ReadAllText(Mirrored(@"Тека\терміновий.docx")));

        // The file lands on disk DURING the pass, while the counter is only raised once it returns — so this
        // waits rather than asserts outright. The counter is what proves it was a partial pass, not a full one.
        Assert.True(WaitUntil(() => loop.ScopedPassesRun >= 1),
            "it must have arrived through a partial pass, not a full one. Loop log:\n" +
            string.Join("\n", _log.Lines.Select(l => $"  {l.Level} {l.Message}")));

        cts.Cancel();
        await running;
    }

    [Fact]
    public async Task A_file_dropped_into_the_root_of_a_watched_pair_is_mirrored_without_waiting_for_the_interval()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var loop = Loop();
        var running = loop.RunAsync(cts.Token);

        // Wait for the first scheduled pass to establish the mirror and start watching.
        Assert.True(WaitUntil(() => loop.ActiveWatchers >= 1 && Directory.Exists(_dst)), "the watcher must be up before the file is created");

        File.WriteAllText(Path.Combine(_src, "кореневий.docx"), "root content");

        Assert.True(WaitUntil(() => File.Exists(Mirrored("кореневий.docx"))),
            "the watcher should have carried the root file across long before the hour is up");
        Assert.Equal("root content", File.ReadAllText(Mirrored("кореневий.docx")));

        // Root file cannot be scoped, so it triggers a full pass (PassesRun >= 2: first was startup pass).
        Assert.True(WaitUntil(() => loop.PassesRun >= 2),
            "it must have arrived through a full pass triggered by the watcher. Loop log:\n" +
            string.Join("\n", _log.Lines.Select(l => $"  {l.Level} {l.Message}")));

        cts.Cancel();
        await running;
    }

    [Fact]
    public async Task An_updated_file_in_the_root_of_a_watched_pair_is_mirrored_without_waiting_for_the_interval()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        File.WriteAllText(Path.Combine(_src, "кореневий_оновлення.docx"), "v1");
        var loop = Loop();
        var running = loop.RunAsync(cts.Token);

        Assert.True(WaitUntil(() => loop.ActiveWatchers >= 1 && File.Exists(Mirrored("кореневий_оновлення.docx"))));
        Assert.Equal("v1", File.ReadAllText(Mirrored("кореневий_оновлення.docx")));

        Thread.Sleep(100);
        // Update file in source root with different size to ensure mtime/size change
        File.WriteAllText(Path.Combine(_src, "кореневий_оновлення.docx"), "v2 - updated content");

        Assert.True(WaitUntil(() => File.ReadAllText(Mirrored("кореневий_оновлення.docx")) == "v2 - updated content"),
            "the updated root file should have been mirrored long before the hour is up. Loop log:\n" +
            string.Join("\n", _log.Lines.Select(l => $"  {l.Level} {l.Message}")));
        Assert.True(WaitUntil(() => loop.PassesRun >= 2));

        cts.Cancel();
        await running;
    }

    [Fact]
    public async Task Our_own_copying_does_not_set_the_loop_spinning()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var loop = Loop();
        var running = loop.RunAsync(cts.Token);
        Assert.True(WaitUntil(() => loop.ActiveWatchers >= 1 && Directory.Exists(_dst)), "the watcher must be up before the file is created, or the event is simply missed");

        File.WriteAllText(Path.Combine(_src, "Тека", "один.docx"), "x");
        Assert.True(WaitUntil(() => loop.ScopedPassesRun >= 1));

        // Writing into the mirror is itself a change. If it were not suppressed, each pass would trigger
        // the next one and the count would climb without anybody touching a file.
        var after = loop.ScopedPassesRun;
        await Task.Delay(TimeSpan.FromSeconds(3), CancellationToken.None);

        Assert.True(loop.ScopedPassesRun <= after + 1,
            $"the loop kept re-triggering itself: {after} → {loop.ScopedPassesRun}");

        cts.Cancel();
        await running;
    }

    [Fact]
    public async Task A_partial_pass_never_removes_anything()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        File.WriteAllText(Path.Combine(_src, "Тека", "старий.docx"), "old");
        var loop = Loop();
        var running = loop.RunAsync(cts.Token);
        Assert.True(WaitUntil(() => File.Exists(Mirrored(@"Тека\старий.docx"))));

        // The user deletes their local copy, then something else changes in the same folder.
        File.Delete(Mirrored(@"Тека\старий.docx"));
        File.WriteAllText(Path.Combine(_src, "Тека", "новий.docx"), "new");
        Assert.True(WaitUntil(() => File.Exists(Mirrored(@"Тека\новий.docx"))));

        cts.Cancel();
        await running;

        // The deletion is a decision for the full pass; the partial one must not have tombstoned it.
        using var store = new StateStore(Path.Combine(_root, "state.db"));
        Assert.Equal(EntryStatus.Active, store.Load("t")[@"Тека\старий.docx"].Status);
    }

    [Fact]
    public async Task A_paused_pair_is_not_watched()
    {
        var cfg = SyncConfig.Load(_configPath);
        cfg.Pairs[0].Enabled = false;
        ConfigFile.Save(cfg, _configPath);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var loop = Loop();
        var running = loop.RunAsync(cts.Token);

        File.WriteAllText(Path.Combine(_src, "Тека", "ігнорований.docx"), "x");
        await Task.Delay(TimeSpan.FromSeconds(3), CancellationToken.None);

        // Since pairs are scheduled individually, a loop whose every pair is paused runs nothing at all —
        // no watchers, no partial passes, and no empty scheduled passes either.
        Assert.Equal(0, loop.ActiveWatchers);
        Assert.Equal(0, loop.ScopedPassesRun);
        Assert.Equal(0, loop.PassesRun);
        Assert.False(File.Exists(Mirrored(@"Тека\ігнорований.docx")));

        cts.Cancel();
        await running;
    }

    [Fact]
    public async Task Watching_switched_off_for_a_pair_means_the_interval_only()
    {
        var cfg = SyncConfig.Load(_configPath);
        cfg.Pairs[0].Watch = WatchMode.Off;
        ConfigFile.Save(cfg, _configPath);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var loop = Loop();
        var running = loop.RunAsync(cts.Token);
        Assert.True(WaitUntil(() => loop.PassesRun >= 1 && Directory.Exists(_dst)));
        Assert.Equal(0, loop.ActiveWatchers);   // switched off means nothing is watched at all

        File.WriteAllText(Path.Combine(_src, "Тека", "пізніше.docx"), "x");
        await Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None);

        Assert.Equal(0, loop.ScopedPassesRun);

        cts.Cancel();
        await running;
    }
}
