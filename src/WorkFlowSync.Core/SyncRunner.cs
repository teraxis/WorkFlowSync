using System.Diagnostics;
using WorkFlowSync.Core.Config;
using WorkFlowSync.Core.Execution;
using WorkFlowSync.Core.Logging;
using WorkFlowSync.Core.Model;
using WorkFlowSync.Core.Planning;
using WorkFlowSync.Core.Scanning;
using WorkFlowSync.Core.State;

namespace WorkFlowSync.Core;

public sealed class PairRunResult
{
    public required string Pair { get; init; }

    /// <summary>Set when only part of the pair was looked at; null for an ordinary full pass.</summary>
    public string? Scope { get; init; }

    public bool Skipped { get; set; }
    public string? SkipReason { get; set; }
    public ScanResult? SourceScan { get; set; }
    public ScanResult? TargetScan { get; set; }
    public PlanStats? Plan { get; set; }
    public ExecutionResult? Execution { get; set; }

    /// <summary>Removals the safety net held back this pass; 0 when nothing looked suspicious.</summary>
    public int HeldBackRemovals { get; set; }
    public int RotatedFiles { get; set; }
    public long RotatedBytes { get; set; }
    public TimeSpan Elapsed { get; set; }
}

public sealed class PassResult
{
    public List<PairRunResult> Pairs { get; } = new();
    public bool DryRun { get; init; }
    public TimeSpan Elapsed { get; set; }
    public int Errors => Pairs.Sum(p => p.Execution?.Errors ?? 0);
    public bool AllSkipped => Pairs.Count > 0 && Pairs.All(p => p.Skipped);

    /// <summary>Processor time the pass itself consumed. Compare with <see cref="Elapsed"/>: a pass that
    /// waits on the network spends almost none (docs/plan-etap5.md §1.2, criterion for revisiting the language).</summary>
    public TimeSpan Cpu { get; set; }

    /// <summary>Process working set right after the pass — the practical memory ceiling on big trees.</summary>
    public long WorkingSet { get; set; }

    /// <summary>Bytes allocated during the pass; measures the GC pressure the scanner and state create.</summary>
    public long Allocated { get; set; }

    /// <summary>Processor time as a share of wall-clock time; may exceed 100% because the scanner is parallel.</summary>
    public double CpuShare => Elapsed > TimeSpan.Zero ? Cpu.TotalSeconds / Elapsed.TotalSeconds : 0;
}

/// <summary>One pass over all folder pairs: scan → plan → execute → persist (docs/product/README.md, «Наскрізний потік»).</summary>
public sealed class SyncRunner
{
    private readonly SyncConfig _config;
    private readonly string _configDir;
    private readonly ISyncLog _log;
    private readonly ICloudFilePlatform _cloudPlatform;

    public SyncRunner(SyncConfig config, string configPath, ISyncLog log, ICloudFilePlatform? cloudPlatform = null)
    {
        _config = config;
        _configDir = Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? AppContext.BaseDirectory;
        _log = log;
        _cloudPlatform = cloudPlatform ?? new WindowsCloudFilePlatform();
    }

    public string StatePath => Path.IsPathRooted(_config.StatePath) ? _config.StatePath : Path.Combine(_configDir, _config.StatePath);

    /// <summary>Set by the resident loop so the watcher can tell our writes from the user's.</summary>
    public Watching.SelfWriteLog? SelfWrites { get; init; }

    /// <param name="onlyPair">Run just this pair (by name), paused or not; null = every enabled pair.</param>
    /// <param name="scope">
    /// Limit the pass to one directory inside the pair (relative to its root). Such a pass only adds and
    /// updates — see <see cref="PlanScope"/>. Meaningless across several pairs, so exactly one must be selected.
    /// </param>
    public PassResult Run(bool dryRun, bool forceBackfill = false, CancellationToken ct = default, string? onlyPair = null, string? scope = null)
    {
        var sw = Stopwatch.StartNew();
        // Cost of the pass itself, so «is the language the bottleneck?» is answered by numbers, not by feel.
        using var proc = Process.GetCurrentProcess();
        var cpuBefore = proc.TotalProcessorTime;
        var allocBefore = GC.GetTotalAllocatedBytes();
        var pass = new PassResult { DryRun = dryRun };
        var selected = onlyPair is null
            ? _config.Pairs.Where(p => p.Enabled).ToList()
            : _config.Pairs.Where(p => p.Name.Equals(onlyPair, StringComparison.OrdinalIgnoreCase)).ToList();
        var paused = _config.Pairs.Count(p => !p.Enabled);
        var passBudget = CpuBudget.For(_config);
        _log.Debug($"cpu load={_config.CpuLoad} listings={passBudget.Parallelism} priority={passBudget.Priority}");
        _log.Info($"pass start  pairs={selected.Count}{(paused > 0 && onlyPair is null ? $" (paused: {paused})" : "")}{(onlyPair is null ? "" : $"  only={onlyPair}")}{(dryRun ? "  mode=dry-run" : "")}");
        if (onlyPair is not null && selected.Count == 0) _log.Error($"unknown pair: {onlyPair}");

        var planScope = PlanScope.For(scope);
        if (planScope is not null && selected.Count != 1)
        {
            // A relative path only means something inside one pair; applying it to several would be guesswork.
            _log.Error($"--scope needs exactly one pair (selected: {selected.Count}); add --pair <name>");
            pass.Elapsed = sw.Elapsed;
            return pass;
        }
        if (planScope is not null) _log.Info($"pass is limited to \\{planScope.RelativeDir}: additions and updates only");

        using var store = new StateStore(StatePath);
        foreach (var pair in selected)
        {
            ct.ThrowIfCancellationRequested();
            var r = RunPair(pair, store, dryRun, forceBackfill, ct, planScope);
            pass.Pairs.Add(r);
        }

        pass.Elapsed = sw.Elapsed;
        proc.Refresh();                       // TotalProcessorTime is cached until we ask for fresh values
        pass.Cpu = proc.TotalProcessorTime - cpuBefore;
        pass.Allocated = GC.GetTotalAllocatedBytes() - allocBefore;
        pass.WorkingSet = proc.WorkingSet64;
        if (!dryRun)
        {
            store.SetMeta("last_run", StateStore.Fmt(DateTimeOffset.UtcNow));
            store.SetMeta("last_run_result", pass.Errors == 0 ? (pass.AllSkipped ? "skipped" : "ok") : $"errors={pass.Errors}");
        }
        _log.Info($"pass end  result={(pass.Errors == 0 ? "ok" : $"errors={pass.Errors}")}  took={pass.Elapsed.TotalSeconds:0.0}s" +
                  $"  cpu={pass.Cpu.TotalSeconds:0.0}s ({pass.CpuShare * 100:0}%)  rss={SyncExecutor.FormatSize(pass.WorkingSet)}  alloc={SyncExecutor.FormatSize(pass.Allocated)}");
        return pass;
    }

    private PairRunResult RunPair(FolderPair pair, StateStore store, bool dryRun, bool forceBackfill, CancellationToken ct, PlanScope? scope = null)
    {
        var tag = $"[{pair.Name}]";
        var budget = CpuBudget.For(_config);
        var sw = Stopwatch.StartNew();
        var result = new PairRunResult { Pair = pair.Name, Scope = scope?.RelativeDir };
        var now = DateTimeOffset.UtcNow;

        // 1. State (in memory) — the whole pair, or just the scope's subtree.
        var state = scope is null ? store.Load(pair.Name) : store.LoadScope(pair.Name, scope.RelativeDir);
        var twoWay = pair.Mode == SyncMode.TwoWay;
        // Never on a scoped pass: an empty scope would look like an empty pair and backdate first_seen for
        // the whole subtree, which would then take the age windows down with it (docs F3).
        var backfill = !twoWay && scope is null && (forceBackfill || state.Count == 0);
        if (twoWay) _log.Info($"{tag} mode=two-way: additions, edits and deletions travel both ways");
        if (backfill) _log.Info($"{tag} first pass for this pair: first_seen backfilled from min(ctime, mtime)");
        if (twoWay && (pair.MaxAge is not null || pair.AutoClean is not null))
            _log.Warn($"{tag} max_age/auto_clean are ignored in two-way mode");

        // 2. Source. Mirror opens it read-only; two-way may also write back. Unavailable root = skip,
        //    never a state change (rule: never mistake offline for deleted).
        var excludes = new ExcludeMatcher(pair.Exclude);
        bool IsTombstoneDir(string rel) => state.TryGetValue(rel, out var e) && e.Kind == EntryKind.Directory && e.Status == EntryStatus.Tombstone;
        ScanResult source;
        try
        {
            var scanner = new TreeScanner(_config.ScanBufferSize, budget.Parallelism, pair.Links, excludes, IsTombstoneDir, budget.Priority);
            source = scanner.Scan(pair.Source, ct, scope?.RelativeDir);
        }
        catch (RootUnavailableException) when (scope is not null && Directory.Exists(pair.Source))
        {
            // The pair root is fine — it is the watched folder that is gone. That is a deletion, and deletions
            // belong to the full pass; here it simply means there is nothing to add.
            _log.Info($"{tag} \\{scope.RelativeDir} is no longer in the source; removals are left to the full pass");
            source = new ScanResult();
        }
        catch (RootUnavailableException ex)
        {
            _log.Warn($"{tag} source unavailable, pair skipped: {ex.Root}");
            result.Skipped = true;
            result.SkipReason = "source unavailable";
            result.Elapsed = sw.Elapsed;
            return result;
        }
        result.SourceScan = source;
        foreach (var w in source.Warnings) _log.Warn($"{tag} source: {w}");
        _log.Info($"{tag} scan source  dirs={source.DirectoryCount} files={source.FileCount} links={source.LinkCount} warnings={source.Warnings.Count} took={source.Elapsed.TotalSeconds:0.0}s");

        // Disk quota is independent of cloud offloading. When a full pass starts below the reserve, an
        // explicitly enabled rotation may permanently remove oldest verified copies from any target
        // storage kind. Otherwise the pass stops before adding more bytes.
        if (!dryRun && pair.DiskQuotaEnabled)
        {
            Directory.CreateDirectory(pair.Target);
            var reserve = checked((long)pair.MinFreeSpaceGb * 1024 * 1024 * 1024);
            var disk = _cloudPlatform.GetDiskSpace(pair.Target);
            if (disk.AvailableBytes < reserve)
            {
                DiskRotationResult? rotation = null;
                if (pair.RotateOnLowSpace && scope is null)
                {
                    rotation = new DiskQuotaRotator(_cloudPlatform, _log) { SelfWrites = SelfWrites }
                        .Rotate(pair.Name, pair.Target, reserve, state, store, ct);
                    result.RotatedFiles = rotation.DeletedFiles;
                    result.RotatedBytes = rotation.FreedBytes;
                }

                if (rotation?.ReserveSatisfied != true)
                {
                    var suffix = pair.FreeUpSpaceAfterCopy && _cloudPlatform.IsSyncRoot(pair.Target)
                        ? "; waiting for the cloud provider to release space"
                        : "; no further target files will be written";
                    _log.Warn($"{tag} pass skipped: free space {SyncExecutor.FormatSize(disk.AvailableBytes)} is below " +
                              $"the configured reserve {SyncExecutor.FormatSize(reserve)}{suffix}");
                    result.Skipped = true;
                    result.SkipReason = "target free-space reserve";
                    result.Elapsed = sw.Elapsed;
                    return result;
                }

                _log.Info($"{tag} rotation restored the disk reserve: deleted={rotation.DeletedFiles} " +
                          $"freed={SyncExecutor.FormatSize(rotation.FreedBytes)}");
            }
        }

        // 3. Target (local; attributes only). Missing target root = create it (first run) unless dry-run.
        ScanResult target;
        if (!Directory.Exists(pair.Target))
        {
            _log.Info($"{tag} target root does not exist{(dryRun ? " (would be created)" : ", creating")}: {pair.Target}");
            if (!dryRun) Directory.CreateDirectory(pair.Target);
            target = new ScanResult();
        }
        else
        {
            // Two-way compares two equal sides, so the target is listed with the same rules as the source.
            var targetScanner = twoWay
                ? new TreeScanner(_config.ScanBufferSize, budget.Parallelism, pair.Links, excludes, null, budget.Priority)
                : new TreeScanner(_config.ScanBufferSize, budget.Parallelism, LinkMode.Skip, null, null, budget.Priority);
            try
            {
                target = targetScanner.Scan(pair.Target, ct, scope?.RelativeDir);
            }
            catch (RootUnavailableException) when (scope is not null)
            {
                target = new ScanResult();   // the mirror has not got this folder yet; it will be created
            }
            foreach (var w in target.Warnings) _log.Warn($"{tag} target: {w}");
        }
        result.TargetScan = target;
        _log.Info($"{tag} scan target  dirs={target.DirectoryCount} files={target.FileCount} took={target.Elapsed.TotalSeconds:0.0}s");

        // 4. Plan (pure).
        FrozenPaths? frozen = null;
        if (twoWay)
        {
            var openPending = store.Pending.GetOpen(pair.Name);
            if (openPending.Count > 0)
                frozen = FrozenPaths.FromPending(openPending);
        }

        var plan = twoWay
            ? new TwoWayPlanner(pair.Name, now, scope, frozen).Plan(source.Entries, target.Entries, state)
            : new SyncPlanner(pair.Name, now, backfill, pair.MaxAge, pair.AutoClean, scope).Plan(source.Entries, target.Entries, state);
        foreach (var w in plan.Warnings) _log.Warn($"{tag} plan: {w}");
        result.Plan = plan.Stats;
        _log.Info($"{tag} plan  {plan.Stats}");
        foreach (var u in plan.StateUpdates)
            // too_old is deliberately not listed: a first pass over an old archive produces one row per file,
            // and a hundred thousand lines saying "left where it was" would bury everything worth reading.
            // The count is in the plan line above, and `--verbose` prints the plan in full.
            if (u.Status is not (EntryStatus.Active or EntryStatus.TooOld))
                _log.Info($"{tag} {(dryRun ? "[dry-run] " : "")}{u.Status.ToString().ToLowerInvariant()}  \\{u.RelativePath}");

        // 4.5. Safety net: a pass that suddenly wants to remove a large share of the pair is a symptom
        //      (half-available source, a folder renamed at the far end, a scan that skipped what it could
        //      not read) — and on a network root a removal cannot be undone (docs/plan-etap5.md §4.2, §4.5).
        var verdict = DeletionGuard.Check(DeletionGuard.CountRemovals(plan), state.Count);
        if (!verdict.Allowed)
        {
            _log.Warn($"{tag} {verdict.Warning}");
            DeletionGuard.StripRemovals(plan);
            result.HeldBackRemovals = verdict.Removals;
        }

        // 5. Execute (target only). Checkpoints persist copied rows as we go, so an interrupted first pass
        //    over a huge tree keeps what it already did — and first_seen stays the real date (F3).
        var exec = new SyncExecutor(pair.Source, pair.Target, _log, dryRun, pair.CloudFiles,
            pair.DiskQuotaEnabled, pair.FreeUpSpaceAfterCopy, pair.MinFreeSpaceGb, _cloudPlatform) { SelfWrites = SelfWrites }
            .Execute(plan, tag, ct, dryRun ? null : rows => store.Upsert(rows));
        result.Execution = exec;

        // 6. Persist: bookkeeping updates + successful actions, one transaction. Nothing on dry-run.
        if (!dryRun)
        {
            var rows = new List<StateEntry>(plan.StateUpdates.Count + exec.Completed.Count);
            rows.AddRange(plan.StateUpdates);
            rows.AddRange(exec.Completed);
            var forgotten = exec.Forgotten.Concat(plan.Forget).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (rows.Count > 0) store.Upsert(rows);
            if (forgotten.Count > 0) store.Delete(pair.Name, forgotten);
            var checkpointed = exec.CheckpointedRows > 0 ? $" (+{exec.CheckpointedRows} at checkpoints)" : "";
            _log.Info($"{tag} state  rows_written={rows.Count}{checkpointed} rows_deleted={forgotten.Count}");
            if (pair.Versioning)
            {
                VersionStore.Prune(pair.Target, pair.VersionsKeepDays, pair.VersionsMaxGb, pair.VersionsPath, _log);
            }
            store.SetMeta($"last_run_{pair.Name}", StateStore.Fmt(DateTimeOffset.UtcNow));
            store.SetMeta($"last_files_{pair.Name}", (exec.FilesCopied + exec.FilesUpdated).ToString(System.Globalization.CultureInfo.InvariantCulture));
            store.SetMeta($"last_errors_{pair.Name}", exec.Errors.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        result.Elapsed = sw.Elapsed;
        _log.Info($"{tag} done  copied={exec.FilesCopied} updated={exec.FilesUpdated} mkdir={exec.DirectoriesCreated} recycled={exec.FilesRecycled}+{exec.DirectoriesRecycled}dirs{(result.RotatedFiles > 0 ? $" rotated={result.RotatedFiles}" : "")}{(exec.OnlineOnlyRequested > 0 ? $" online_only={exec.OnlineOnlyRequested}" : "")}{(exec.CloudSpaceWaits > 0 ? $" cloud_waits={exec.CloudSpaceWaits}" : "")}{(exec.DiskQuotaStops > 0 ? $" quota_stops={exec.DiskQuotaStops}" : "")}{(exec.Skipped > 0 ? $" skipped={exec.Skipped}" : "")}{(result.HeldBackRemovals > 0 ? $" held_back={result.HeldBackRemovals}" : "")} bytes={SyncExecutor.FormatSize(exec.BytesCopied)} errors={exec.Errors} took={result.Elapsed.TotalSeconds:0.0}s");
        return result;
    }
}
