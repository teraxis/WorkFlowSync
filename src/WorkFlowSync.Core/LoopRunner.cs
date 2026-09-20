using WorkFlowSync.Core.Config;
using WorkFlowSync.Core.Logging;
using WorkFlowSync.Core.Watching;

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

    /// <summary>How often the loop looks at the watchers while it waits for the next scheduled pass.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Tuning for the change queues; tests shorten the quiet period.</summary>
    public ChangeQueue.Options? QueueOptions { get; init; }

    /// <summary>Off in tests that only care about the scheduled pass.</summary>
    public bool WatchEnabled { get; init; } = true;

    /// <summary>Partial passes run because a watcher reported a change; diagnostics and tests.</summary>
    public int ScopedPassesRun { get; private set; }

    /// <summary>Roots currently being watched. 0 until the first pass has run and the watchers are up.</summary>
    public int ActiveWatchers { get; private set; }

    /// <summary>Notes what this loop writes, so a watcher does not react to our own copying.</summary>
    private readonly SelfWriteLog _selfWrites = new();

    /// <summary>
    /// When each pair is next due. Pairs no longer share one clock: a folder that changes every few
    /// minutes and a 50 000-folder archive that takes five minutes to scan should not be checked together.
    /// </summary>
    private readonly Dictionary<string, DateTimeOffset> _nextDue = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Interval for one pair: the test override, then the pair's own, then the shared default.</summary>
    private TimeSpan IntervalFor(SyncConfig cfg, FolderPair pair) => IntervalOverride ?? cfg.IntervalFor(pair);

    public LoopRunner(string configPath, ISyncLog log, bool dryRun = false)
    {
        _configPath = configPath;
        _log = log;
        _dryRun = dryRun;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _log.Info($"loop start  config={_configPath}");
        using var watchers = WatchEnabled ? new WatchSet(_log, _selfWrites, QueueOptions) : null;
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
                    var now = DateTimeOffset.UtcNow;
                    var due = DuePairs(cfg, now);
                    if (due.Count == 0)
                    {
                        // Nothing is due yet — the wait below is recomputed from the earliest deadline.
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
                            var runner = new SyncRunner(cfg, _configPath, _log) { SelfWrites = _selfWrites };
                            var enabled = cfg.Pairs.Count(p => p.Enabled);

                            // All of them due at once is the common case (and the usual one right after a
                            // restart): one pass opens the state database once instead of once per pair.
                            if (due.Count == enabled)
                            {
                                var result = runner.Run(_dryRun, forceBackfill: false, ct);
                                PassesRun++;
                                PassCompleted?.Invoke(result);
                            }
                            else
                            {
                                _log.Info($"loop: due now — {string.Join(", ", due.Select(p => p.Name))}");
                                foreach (var pair in due)
                                {
                                    ct.ThrowIfCancellationRequested();
                                    var result = runner.Run(_dryRun, forceBackfill: false, ct, pair.Name);
                                    PassesRun++;
                                    PassCompleted?.Invoke(result);
                                }
                            }

                            var completed = DateTimeOffset.UtcNow;
                            foreach (var pair in due) _nextDue[pair.Name] = completed + IntervalFor(cfg, pair);
                        }
                    }

                    // Watchers follow the config, which is re-read above: a pair paused, added or switched
                    // to two-way between passes takes effect here.
                    if (watchers is not null)
                    {
                        var before = watchers.ActiveWatchers;
                        watchers.Follow(cfg);
                        ActiveWatchers = watchers.ActiveWatchers;
                        if (watchers.ActiveWatchers != before)
                            _log.Info($"loop: watching {watchers.ActiveWatchers} folder(s) for changes");
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

            var delay = NextDelay(cfg);
            _log.Info($"loop: next pass in {delay.TotalMinutes:0.#} min" +
                      (watchers is { ActiveWatchers: > 0 } ? ", or sooner if something changes" : ""));
            try
            {
                if (!await WaitOrReactAsync(delay, watchers, cfg, ct).ConfigureAwait(false)) break;
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
        _log.Info($"loop end  passes={PassesRun}");
    }

    /// <summary>Pairs whose turn has come: never checked yet, or past their own deadline.</summary>
    private List<FolderPair> DuePairs(SyncConfig cfg, DateTimeOffset now)
    {
        // Forget pairs that were removed or renamed, so the dictionary cannot grow for ever.
        var known = new HashSet<string>(cfg.Pairs.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var gone in _nextDue.Keys.Where(k => !known.Contains(k)).ToList()) _nextDue.Remove(gone);

        return cfg.Pairs
            .Where(p => p.Enabled && (!_nextDue.TryGetValue(p.Name, out var at) || at <= now))
            .ToList();
    }

    /// <summary>How long to wait: until the earliest deadline, never longer than the shortest interval.</summary>
    private TimeSpan NextDelay(SyncConfig? cfg)
    {
        if (cfg is null) return IntervalOverride ?? TimeSpan.FromMinutes(30);

        var now = DateTimeOffset.UtcNow;
        var deadlines = cfg.Pairs.Where(p => p.Enabled)
            .Select(p => _nextDue.TryGetValue(p.Name, out var at) ? at : now)
            .ToList();
        if (deadlines.Count == 0) return IntervalOverride ?? cfg.Interval;   // nothing enabled: just idle

        var wait = deadlines.Min() - now;
        // A floor keeps a misconfigured or already-overdue pair from spinning the loop.
        return wait < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : wait;
    }

    /// <summary>
    /// Waits for the next scheduled pass while serving the watchers. Returns false when the loop should stop.
    /// Returns early — i.e. brings the scheduled pass forward — as soon as a watcher asks for a full one.
    /// </summary>
    private async Task<bool> WaitOrReactAsync(TimeSpan delay, WatchSet? watchers, SyncConfig? cfg, CancellationToken ct)
    {
        var until = DateTimeOffset.UtcNow + delay;
        while (!ct.IsCancellationRequested)
        {
            var remaining = until - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) return true;
            if (watchers is null || cfg is null)
            {
                await Task.Delay(remaining, ct).ConfigureAwait(false);
                return true;
            }

            await Task.Delay(remaining < PollInterval ? remaining : PollInterval, ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested) return false;

            foreach (var ready in watchers.TakeReady())
            {
                if (ready.Batch.NeedsFullPass)
                {
                    _log.Info($"[{ready.PairName}] {ready.Batch.Reason} — running a full pass now");
                    return true;    // the outer loop does exactly that
                }
                if (!RunScoped(ready, cfg, ct)) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Runs one partial pass per changed folder. Returns false when a full pass is needed instead — a change
    /// at the very top of a pair has no folder to narrow to, and the whole pair is the honest answer.
    /// </summary>
    private bool RunScoped(WatchSet.Ready ready, SyncConfig cfg, CancellationToken ct)
    {
        var pair = cfg.Pairs.FirstOrDefault(p => p.Name.Equals(ready.PairName, StringComparison.OrdinalIgnoreCase));
        if (pair is null || !pair.Enabled) return true;   // paused or removed while we were waiting

        foreach (var dir in ready.Batch.Directories)
        {
            if (ct.IsCancellationRequested) return false;
            if (string.IsNullOrEmpty(dir)) return false;

            // A top-level name is a candidate, not a fact: a folder can be scoped to, a file in the pair
            // root cannot, and something that no longer exists is a removal — all of which mean a full pass.
            if (!Directory.Exists(Path.Combine(ready.Root, dir))) return false;

            try
            {
                // The same lock as every other pass: a partial pass must never overlap a scheduled one.
                using var gate = PassLock.TryAcquire(_configPath);
                if (!gate.Acquired)
                {
                    _log.Debug($"[{pair.Name}] a pass is already running; \\{dir} will be picked up by it");
                    continue;
                }

                CpuBudget.TrySetPriority(Thread.CurrentThread, CpuBudget.For(cfg).Priority);
                var result = new SyncRunner(cfg, _configPath, _log) { SelfWrites = _selfWrites }
                    .Run(_dryRun, forceBackfill: false, ct, pair.Name, dir);
                ScopedPassesRun++;
                PassCompleted?.Invoke(result);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception ex)
            {
                // A failed partial pass is not worth stopping for: the scheduled pass covers the same ground.
                _log.Error($"[{pair.Name}] partial pass for \\{dir} failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
        return true;
    }
}
