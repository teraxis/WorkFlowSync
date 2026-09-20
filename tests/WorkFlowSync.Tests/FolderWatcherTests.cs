using WorkFlowSync.Core.Logging;
using WorkFlowSync.Core.Watching;

namespace WorkFlowSync.Tests;

/// <summary>
/// The watcher against a real folder (docs/plan-etap5.md §4.3). The queue's rules are covered separately
/// with an injected clock; what these prove is the wiring — that events arrive, that our own writes are
/// filtered out, and that an unwatchable root is reported rather than thrown.
/// </summary>
[Trait("Category", "E2E")]
public sealed class FolderWatcherTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wfs-watch-" + Guid.NewGuid().ToString("N"));
    private readonly MemorySyncLog _log = new();

    public FolderWatcherTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>A queue that hands work over as soon as it arrives, so a test does not have to wait.</summary>
    private static ChangeQueue Impatient() => new(new ChangeQueue.Options
    {
        Quiet = TimeSpan.Zero,
        MaxDelay = TimeSpan.Zero,
        MaxDirectories = 200,
    });

    private static bool WaitUntil(Func<bool> condition, int seconds = 10)
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
    public void A_new_file_reaches_the_queue_as_its_folder()
    {
        var queue = Impatient();
        using var watcher = new FolderWatcher(_root, queue, _log, "[t]");
        Assert.True(watcher.Start());

        Directory.CreateDirectory(Path.Combine(_root, "Тека"));
        File.WriteAllText(Path.Combine(_root, "Тека", "новий.txt"), "x");

        Assert.True(WaitUntil(() => queue.PendingDirectories > 0 || queue.FullPassWanted),
            "a local watcher must see a file created under the root");
        var batch = queue.TakeReady()!;
        Assert.True(batch.NeedsFullPass || batch.Directories.Contains("Тека"));
    }

    [Fact]
    public void Starting_does_not_by_itself_ask_for_a_full_pass()
    {
        // Deciding that is the caller's job. The resident loop starts watching right after a full pass, so
        // there is no gap to cover; asking here made it run a second full pass immediately, which then did
        // the work the watcher existed to do. Found by the integration tests, not by review.
        var queue = Impatient();
        using var watcher = new FolderWatcher(_root, queue, _log, "[t]");

        watcher.Start();

        Assert.False(queue.FullPassWanted);
        Assert.Null(queue.TakeReady());
    }

    [Fact]
    public void Our_own_writes_do_not_start_another_pass()
    {
        var queue = Impatient();
        var selfWrites = new SelfWriteLog();
        using var watcher = new FolderWatcher(_root, queue, _log, "[t]", selfWrites);
        watcher.Start();
        queue.Clear();

        var path = Path.Combine(_root, "нами-записаний.txt");
        selfWrites.Record(path);
        File.WriteAllText(path, "written by the pass");

        // Give the notification time to arrive and be discarded.
        Assert.True(WaitUntil(() => watcher.EventsSeen > 0), "the event itself must still arrive");
        Thread.Sleep(300);
        Assert.Equal(0, queue.PendingDirectories);
        Assert.False(queue.FullPassWanted);
    }

    [Fact]
    public void A_root_that_cannot_be_watched_is_reported_not_thrown()
    {
        var missing = Path.Combine(_root, "не-існує");
        var queue = Impatient();
        using var watcher = new FolderWatcher(missing, queue, _log, "[t]");

        Assert.False(watcher.Start());
        Assert.False(watcher.Active);
        Assert.Contains(_log.Lines, l => l.Level == LogLevel.Warn && l.Message.Contains("cannot watch", StringComparison.Ordinal));
    }

    [Fact]
    public void Stopping_is_idempotent_and_leaves_nothing_running()
    {
        var queue = Impatient();
        var watcher = new FolderWatcher(_root, queue, _log, "[t]");
        watcher.Start();

        watcher.Stop();
        watcher.Stop();
        watcher.Dispose();

        Assert.False(watcher.Active);
    }
}
