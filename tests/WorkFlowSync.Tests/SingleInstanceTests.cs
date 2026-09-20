using WorkFlowSync.Core;
using WorkFlowSync.Core.Config;

namespace WorkFlowSync.Tests;

/// <summary>
/// One window per config file. The lock itself is a named mutex, so the interesting cases are: a second
/// claim is refused, releasing lets the next one in, two different configs do not collide, and the running
/// copy is actually woken up instead of the second launch dying silently.
/// </summary>
public class SingleInstanceTests
{
    private static string TempConfig() => Path.Combine(Path.GetTempPath(), $"wfs-si-{Guid.NewGuid():N}", "config.json");

    [Fact]
    public void The_first_launch_takes_the_lock_and_the_second_is_refused()
    {
        var config = TempConfig();

        using var first = SingleInstance.TryAcquire(config);
        using var second = SingleInstance.TryAcquire(config);

        Assert.True(first.IsFirst);
        Assert.False(second.IsFirst);
    }

    [Fact]
    public void Releasing_the_lock_lets_the_next_launch_start_normally()
    {
        var config = TempConfig();

        using (var first = SingleInstance.TryAcquire(config)) Assert.True(first.IsFirst);

        using var afterExit = SingleInstance.TryAcquire(config);

        Assert.True(afterExit.IsFirst);
    }

    [Fact]
    public void Two_portable_copies_in_different_folders_run_side_by_side()
    {
        using var a = SingleInstance.TryAcquire(TempConfig());
        using var b = SingleInstance.TryAcquire(TempConfig());

        Assert.True(a.IsFirst);
        Assert.True(b.IsFirst);
    }

    [Fact]
    public void The_lock_name_follows_the_config_path_and_ignores_its_case()
    {
        var a = SingleInstance.MutexNameFor(@"C:\App\config.json");
        var b = SingleInstance.MutexNameFor(@"c:\app\CONFIG.JSON");
        var other = SingleInstance.MutexNameFor(@"C:\Other\config.json");

        Assert.Equal(a, b);
        Assert.NotEqual(a, other);
        Assert.StartsWith(@"Local\WorkFlowSync.app.", a, StringComparison.Ordinal);
    }

    [Fact]
    public void It_does_not_collide_with_the_pass_lock_for_the_same_config()
    {
        var config = TempConfig();

        // A window being open must not look like a pass in progress, or the scheduler would skip every pass.
        Assert.NotEqual(PassLock.NameFor(config), SingleInstance.MutexNameFor(config));

        using var window = SingleInstance.TryAcquire(config);
        using var pass = PassLock.TryAcquire(config);

        Assert.True(window.IsFirst);
        Assert.True(pass.Acquired);
    }

    [Fact]
    public void A_second_launch_wakes_the_running_copy()
    {
        var config = TempConfig();
        using var first = SingleInstance.TryAcquire(config);
        using var woken = new ManualResetEventSlim(false);
        first.ListenForOtherLaunches(() => woken.Set());

        var delivered = SingleInstance.SignalExisting(config);

        Assert.True(delivered, "the signal must reach the running copy");
        Assert.True(woken.Wait(TimeSpan.FromSeconds(5)), "the running copy must be asked to show its window");
    }

    [Fact]
    public void Signalling_when_nothing_runs_reports_that_nobody_answered()
    {
        Assert.False(SingleInstance.SignalExisting(TempConfig()));
    }

    [Fact]
    public void A_listener_that_throws_does_not_kill_the_running_copy()
    {
        var config = TempConfig();
        using var first = SingleInstance.TryAcquire(config);
        using var secondSignal = new ManualResetEventSlim(false);
        var calls = 0;

        first.ListenForOtherLaunches(() =>
        {
            if (Interlocked.Increment(ref calls) == 1) throw new InvalidOperationException("window is busy");
            secondSignal.Set();
        });

        SingleInstance.SignalExisting(config);
        Thread.Sleep(100);
        SingleInstance.SignalExisting(config);

        Assert.True(secondSignal.Wait(TimeSpan.FromSeconds(5)), "the listener must survive a failed hand-off");
    }

    [Theory]
    [InlineData(new[] { "--config", @"D:\a\config.json" }, @"D:\a\config.json")]
    [InlineData(new[] { "--tray", "--config", @"D:\b\config.json" }, @"D:\b\config.json")]
    [InlineData(new[] { "--tray" }, null)]
    [InlineData(new string[0], null)]
    [InlineData(new[] { "--config" }, null)]          // trailing flag with no value
    public void The_entry_point_and_the_app_agree_on_which_config_this_process_owns(string[] args, string? expected) =>
        Assert.Equal(expected ?? ConfigFile.DefaultPath, ConfigFile.FromArgs(args));
}
