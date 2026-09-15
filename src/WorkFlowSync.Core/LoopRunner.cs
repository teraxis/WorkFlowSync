using WorkFlowSync.Core.Config;
using WorkFlowSync.Core.Logging;

namespace WorkFlowSync.Core;

/// <summary>
/// Resident mode: run a pass, sleep the configured interval, repeat until cancelled.
/// The config file is re-read before every pass so edits made in the GUI take effect without a restart.
/// A pass that throws is logged and the loop continues; a pass that cannot get the <see cref="PassLock"/> is skipped.
/// </summary>
public sealed class LoopRunner
{
    private readonly string _configPath;
    private readonly ISyncLog _log;
    private readonly bool _dryRun;

    /// <summary>Overrides the config interval (tests); null = use <see cref="SyncConfig.Interval"/>.</summary>
    public TimeSpan? IntervalOverride { get; init; }

    /// <summary>Stop after this many passes (tests); null = forever.</summary>
    public int? MaxPasses { get; init; }

    public int PassesRun { get; private set; }

    /// <summary>The config as last read by the loop (for callers that want e.g. the log rotation setting).</summary>
    public SyncConfig? LastConfig { get; private set; }

    public event Action? PassStarting;
    public event Action<PassResult>? PassCompleted;

    public LoopRunner(string configPath, ISyncLog log, bool dryRun = false)
    {
        _configPath = configPath;
        _log = log;
        _dryRun = dryRun;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _log.Info($"loop start  config={_configPath}");
        while (!ct.IsCancellationRequested)
        {
            SyncConfig? cfg = null;
            try
            {
                cfg = SyncConfig.Load(_configPath);
                LastConfig = cfg;
                var problems = cfg.Validate();
                if (problems.Count > 0)
                {
                    _log.Error("loop: config invalid, pass skipped: " + string.Join("; ", problems));
                }
                else
                {
                    using var gate = PassLock.TryAcquire(_configPath);
                    if (!gate.Acquired)
                    {
                        _log.Warn("loop: another pass is running for this config, skipped");
                    }
                    else
                    {
                        PassStarting?.Invoke();
                        // Re-applied every pass: the setting can change between passes (the config is re-read above).
                        CpuBudget.TrySetPriority(Thread.CurrentThread, CpuBudget.For(cfg).Priority);
                        var result = new SyncRunner(cfg, _configPath, _log).Run(_dryRun, forceBackfill: false, ct);
                        PassesRun++;
                        PassCompleted?.Invoke(result);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.Error($"loop: pass failed: {ex.GetType().Name}: {ex.Message}");
            }

            if (MaxPasses is { } max && PassesRun >= max) break;

            var delay = IntervalOverride ?? cfg?.Interval ?? TimeSpan.FromMinutes(30);
            _log.Info($"loop: next pass in {delay.TotalMinutes:0.#} min");
            try
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
        _log.Info($"loop end  passes={PassesRun}");
    }
}
