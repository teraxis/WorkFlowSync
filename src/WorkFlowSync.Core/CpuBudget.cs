using System.Diagnostics;
using WorkFlowSync.Core.Config;

namespace WorkFlowSync.Core;

/// <summary>
/// Turns <see cref="CpuLoad"/> into the two knobs a pass actually uses: how many directory listings
/// run at once and at what thread priority. A pass is deliberately run on dedicated threads rather
/// than the thread pool, so the priority really applies (pool threads reset it between work items).
/// </summary>
public readonly record struct CpuBudget(int Parallelism, ThreadPriority Priority)
{
    public static CpuBudget For(CpuLoad load, int configuredParallelism)
    {
        var cores = Math.Max(1, Environment.ProcessorCount);
        var wanted = Math.Clamp(configuredParallelism, 1, 64);
        return load switch
        {
            CpuLoad.Full => new CpuBudget(wanted, ThreadPriority.Normal),
            CpuLoad.Low => new CpuBudget(Math.Min(wanted, 2), ThreadPriority.Lowest),
            _ => new CpuBudget(Math.Min(wanted, Math.Max(2, cores / 2)), ThreadPriority.BelowNormal),
        };
    }

    public static CpuBudget For(SyncConfig config) => For(config.CpuLoad, config.ScanParallelism);

    /// <summary>Process priority for a windowless run (wfs.exe): a background pass should never outrank the desktop.</summary>
    public static ProcessPriorityClass ProcessPriority(CpuLoad load) => load switch
    {
        CpuLoad.Full => ProcessPriorityClass.Normal,
        CpuLoad.Low => ProcessPriorityClass.Idle,
        _ => ProcessPriorityClass.BelowNormal,
    };

    /// <summary>Best-effort: a machine that refuses the change is not a reason to skip the pass.</summary>
    public static void ApplyToCurrentProcess(CpuLoad load)
    {
        try { Process.GetCurrentProcess().PriorityClass = ProcessPriority(load); }
        catch { /* not permitted (rare) */ }
    }

    /// <summary>Runs <paramref name="work"/> on one dedicated thread of this budget's priority.</summary>
    public Task<T> RunAsync<T>(Func<T> work, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                tcs.SetResult(work());
            }
            catch (OperationCanceledException) { tcs.TrySetCanceled(ct); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        })
        {
            IsBackground = true,
            Name = "wfs-pass",
        };
        TrySetPriority(thread, Priority);
        thread.Start();
        return tcs.Task;
    }

    /// <summary>Priority is advisory: some hosts refuse it, and that must not fail a pass.</summary>
    public static void TrySetPriority(Thread thread, ThreadPriority priority)
    {
        try { thread.Priority = priority; }
        catch { /* ignored */ }
    }
}
