using WorkFlowSync.Core;
using WorkFlowSync.Core.Config;

namespace WorkFlowSync.Tests;

public sealed class PassLockTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wfs-passlock-" + Guid.NewGuid().ToString("N"));

    public PassLockTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Different_pairs_can_acquire_locks_concurrently()
    {
        var cfg = Path.Combine(_root, "config.json");
        using var pair1 = PassLock.TryAcquire(cfg, "PairA");
        Assert.True(pair1.Acquired);

        using var pair2 = PassLock.TryAcquire(cfg, "PairB");
        Assert.True(pair2.Acquired);
    }

    [Fact]
    public void Same_pair_cannot_acquire_lock_concurrently()
    {
        var cfg = Path.Combine(_root, "config.json");
        using var first = PassLock.TryAcquire(cfg, "PairA");
        Assert.True(first.Acquired);

        using var second = PassLock.TryAcquire(cfg, "PairA");
        Assert.False(second.Acquired);
    }

    [Fact]
    public void Global_lock_blocks_pair_lock_and_pair_lock_blocks_global_lock()
    {
        var cfg = Path.Combine(_root, "config.json");
        ConfigFile.Save(new SyncConfig
        {
            Pairs = { new FolderPair { Name = "PairA", Source = "src", Target = "tgt" } }
        }, cfg);

        // Pair lock blocks global lock
        using (var pairLock = PassLock.TryAcquire(cfg, "PairA"))
        {
            Assert.True(pairLock.Acquired);
            using var globalLock = PassLock.TryAcquire(cfg);
            Assert.False(globalLock.Acquired);
        }

        // Global lock blocks pair lock
        using (var globalLock = PassLock.TryAcquire(cfg))
        {
            Assert.True(globalLock.Acquired);
            using var pairLock = PassLock.TryAcquire(cfg, "PairA");
            Assert.False(pairLock.Acquired);
        }
    }

    [Fact]
    public void IsGlobalHeld_detects_global_lock()
    {
        var cfg = Path.Combine(_root, "config.json");
        Assert.False(PassLock.IsGlobalHeld(cfg));

        using (var held = PassLock.TryAcquire(cfg))
        {
            Assert.True(held.Acquired);
            Assert.True(PassLock.IsGlobalHeld(cfg));
        }

        Assert.False(PassLock.IsGlobalHeld(cfg));
    }
}
