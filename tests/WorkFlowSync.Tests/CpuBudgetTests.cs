using System.Diagnostics;
using WorkFlowSync.Core;
using WorkFlowSync.Core.Config;
using WorkFlowSync.Core.Logging;
using WorkFlowSync.Core.Scanning;

namespace WorkFlowSync.Tests;

/// <summary>«Навантаження на процесор» (docs/product/features/scanner-performance.md).</summary>
public sealed class CpuBudgetTests
{
    [Fact]
    public void Full_keeps_the_configured_parallelism_and_normal_priority()
    {
        var b = CpuBudget.For(CpuLoad.Full, 16);
        Assert.Equal(16, b.Parallelism);
        Assert.Equal(ThreadPriority.Normal, b.Priority);
        Assert.Equal(ProcessPriorityClass.Normal, CpuBudget.ProcessPriority(CpuLoad.Full));
    }

    [Fact]
    public void Balanced_uses_at_most_half_the_cores_and_never_raises_the_setting()
    {
        var half = Math.Max(2, Environment.ProcessorCount / 2);

        var many = CpuBudget.For(CpuLoad.Balanced, 64);
        Assert.Equal(half, many.Parallelism);
        Assert.Equal(ThreadPriority.BelowNormal, many.Priority);

        // A deliberately small setting stays small.
        Assert.Equal(1, CpuBudget.For(CpuLoad.Balanced, 1).Parallelism);
    }

    [Fact]
    public void Low_caps_at_two_listings_with_the_lowest_priority()
    {
        var b = CpuBudget.For(CpuLoad.Low, 32);
        Assert.Equal(2, b.Parallelism);
        Assert.Equal(ThreadPriority.Lowest, b.Priority);
        Assert.Equal(ProcessPriorityClass.Idle, CpuBudget.ProcessPriority(CpuLoad.Low));
    }

    [Fact]
    public void Default_config_is_balanced_and_round_trips()
    {
        var cfg = new SyncConfig();
        Assert.Equal(CpuLoad.Balanced, cfg.CpuLoad);

        cfg.CpuLoad = CpuLoad.Low;
        Assert.Equal(CpuLoad.Low, SyncConfig.Parse(cfg.ToJson()).CpuLoad);
    }

    [Fact]
    public async Task RunAsync_runs_the_work_on_its_own_thread_at_the_chosen_priority()
    {
        var budget = CpuBudget.For(CpuLoad.Low, 8);
        var seen = await budget.RunAsync(() => (Thread.CurrentThread.Priority, Thread.CurrentThread.IsThreadPoolThread));

        Assert.Equal(ThreadPriority.Lowest, seen.Priority);
        Assert.False(seen.IsThreadPoolThread);
        // The pool the UI uses is untouched.
        Assert.Equal(ThreadPriority.Normal, Thread.CurrentThread.Priority);
    }

    [Fact]
    public void Scanner_still_walks_the_whole_tree_at_the_lowest_priority()
    {
        var root = Path.Combine(Path.GetTempPath(), "wfs-cpu-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "a", "b"));
            File.WriteAllText(Path.Combine(root, "a", "one.txt"), "1");
            File.WriteAllText(Path.Combine(root, "a", "b", "two.txt"), "2");

            var budget = CpuBudget.For(CpuLoad.Low, 8);
            var scan = new TreeScanner(64 * 1024, budget.Parallelism, LinkMode.Skip, null, null, budget.Priority).Scan(root);

            Assert.Equal(2, scan.FileCount);
            Assert.Equal(2, scan.DirectoryCount);
            Assert.True(scan.Entries.ContainsKey(@"a\b\two.txt"));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void A_pass_honours_the_configured_load()
    {
        var root = Path.Combine(Path.GetTempPath(), "wfs-cpu2-" + Guid.NewGuid().ToString("N"));
        try
        {
            var src = Path.Combine(root, "src");
            Directory.CreateDirectory(src);
            File.WriteAllText(Path.Combine(src, "x.txt"), "x");
            var configPath = Path.Combine(root, "config.json");
            var cfg = new SyncConfig
            {
                Pairs = { new FolderPair { Name = "t", Source = src, Target = Path.Combine(root, "dst") } },
                CpuLoad = CpuLoad.Low,
                StatePath = Path.Combine(root, "state.db"),
                LogPath = Path.Combine(root, "logs"),
            };
            ConfigFile.Save(cfg, configPath);

            var log = new MemorySyncLog();
            new SyncRunner(cfg, configPath, log).Run(dryRun: false);

            Assert.Equal("x", File.ReadAllText(Path.Combine(root, "dst", "x.txt")));
            Assert.Equal(ThreadPriority.Normal, Thread.CurrentThread.Priority);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }
}
