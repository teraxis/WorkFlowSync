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
    public bool Skipped { get; set; }
    public string? SkipReason { get; set; }
    public ScanResult? SourceScan { get; set; }
    public ScanResult? TargetScan { get; set; }
    public PlanStats? Plan { get; set; }
    public ExecutionResult? Execution { get; set; }
    public TimeSpan Elapsed { get; set; }
}

public sealed class PassResult
{
    public List<PairRunResult> Pairs { get; } = new();
    public bool DryRun { get; init; }
    public TimeSpan Elapsed { get; set; }
    public int Errors => Pairs.Sum(p => p.Execution?.Errors ?? 0);
    public bool AllSkipped => Pairs.Count > 0 && Pairs.All(p => p.Skipped);
}

/// <summary>One pass over all folder pairs: scan → plan → execute → persist (docs/product/README.md, «Наскрізний потік»).</summary>
public sealed class SyncRunner
{
    private readonly SyncConfig _config;
    private readonly string _configDir;
    private readonly ISyncLog _log;

    public SyncRunner(SyncConfig config, string configPath, ISyncLog log)
    {
        _config = config;
        _configDir = Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? AppContext.BaseDirectory;
        _log = log;
    }

    public string StatePath => Path.IsPathRooted(_config.StatePath) ? _config.StatePath : Path.Combine(_configDir, _config.StatePath);

    /// <param name="onlyPair">Run just this pair (by name), paused or not; null = every enabled pair.</param>
    public PassResult Run(bool dryRun, bool forceBackfill = false, CancellationToken ct = default, string? onlyPair = null)
    {
        var sw = Stopwatch.StartNew();
        var pass = new PassResult { DryRun = dryRun };
        var selected = onlyPair is null
            ? _config.Pairs.Where(p => p.Enabled).ToList()
            : _config.Pairs.Where(p => p.Name.Equals(onlyPair, StringComparison.OrdinalIgnoreCase)).ToList();
        var paused = _config.Pairs.Count(p => !p.Enabled);
        var passBudget = CpuBudget.For(_config);
        _log.Debug($"cpu load={_config.CpuLoad} listings={passBudget.Parallelism} priority={passBudget.Priority}");
        _log.Info($"pass start  pairs={selected.Count}{(paused > 0 && onlyPair is null ? $" (paused: {paused})" : "")}{(onlyPair is null ? "" : $"  only={onlyPair}")}{(dryRun ? "  mode=dry-run" : "")}");
        if (onlyPair is not null && selected.Count == 0) _log.Error($"unknown pair: {onlyPair}");

        using var store = new StateStore(StatePath);
        foreach (var pair in selected)
        {
            ct.ThrowIfCancellationRequested();
            var r = RunPair(pair, store, dryRun, forceBackfill, ct);
            pass.Pairs.Add(r);
        }

        pass.Elapsed = sw.Elapsed;
        if (!dryRun)
        {
            store.SetMeta("last_run", StateStore.Fmt(DateTimeOffset.UtcNow));
            store.SetMeta("last_run_result", pass.Errors == 0 ? (pass.AllSkipped ? "skipped" : "ok") : $"errors={pass.Errors}");
        }
        _log.Info($"pass end  result={(pass.Errors == 0 ? "ok" : $"errors={pass.Errors}")}  took={pass.Elapsed.TotalSeconds:0.0}s");
        return pass;
    }

    private PairRunResult RunPair(FolderPair pair, StateStore store, bool dryRun, bool forceBackfill, CancellationToken ct)
    {
        var tag = $"[{pair.Name}]";
        var budget = CpuBudget.For(_config);
        var sw = Stopwatch.StartNew();
        var result = new PairRunResult { Pair = pair.Name };
        var now = DateTimeOffset.UtcNow;

        // 1. State (in memory).
        var state = store.Load(pair.Name);
        var twoWay = pair.Mode == SyncMode.TwoWay;
        var backfill = !twoWay && (forceBackfill || state.Count == 0);
        if (twoWay) _log.Info($"{tag} mode=two-way: additions, edits and deletions travel both ways");
        if (backfill) _log.Info($"{tag} first pass for this pair: first_seen backfilled from min(ctime, mtime)");
        if (twoWay && pair.Retention is not null) _log.Warn($"{tag} retention is ignored in two-way mode");

        // 2. Source. Mirror opens it read-only; two-way may also write back. Unavailable root = skip,
        //    never a state change (rule: never mistake offline for deleted).
        var excludes = new ExcludeMatcher(pair.Exclude);
        bool IsTombstoneDir(string rel) => state.TryGetValue(rel, out var e) && e.Kind == EntryKind.Directory && e.Status == EntryStatus.Tombstone;
        ScanResult source;
        try
        {
            var scanner = new TreeScanner(_config.ScanBufferSize, budget.Parallelism, pair.Links, excludes, IsTombstoneDir, budget.Priority);
            source = scanner.Scan(pair.Source, ct);
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
            target = targetScanner.Scan(pair.Target, ct);
            foreach (var w in target.Warnings) _log.Warn($"{tag} target: {w}");
        }
        result.TargetScan = target;
        _log.Info($"{tag} scan target  dirs={target.DirectoryCount} files={target.FileCount} took={target.Elapsed.TotalSeconds:0.0}s");

        // 4. Plan (pure).
        var plan = twoWay
            ? new TwoWayPlanner(pair.Name, now).Plan(source.Entries, target.Entries, state)
            : new SyncPlanner(pair.Name, now, backfill, pair.Retention).Plan(source.Entries, target.Entries, state);
        foreach (var w in plan.Warnings) _log.Warn($"{tag} plan: {w}");
        result.Plan = plan.Stats;
        _log.Info($"{tag} plan  {plan.Stats}");
        foreach (var u in plan.StateUpdates)
            if (u.Status != EntryStatus.Active)
                _log.Info($"{tag} {(dryRun ? "[dry-run] " : "")}{u.Status.ToString().ToLowerInvariant()}  \\{u.RelativePath}");

        // 5. Execute (target only).
        var exec = new SyncExecutor(pair.Source, pair.Target, _log, dryRun).Execute(plan, tag, ct);
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
            _log.Info($"{tag} state  rows_written={rows.Count} rows_deleted={forgotten.Count}");
        }

        result.Elapsed = sw.Elapsed;
        _log.Info($"{tag} done  copied={exec.FilesCopied} updated={exec.FilesUpdated} mkdir={exec.DirectoriesCreated} recycled={exec.FilesRecycled}+{exec.DirectoriesRecycled}dirs bytes={SyncExecutor.FormatSize(exec.BytesCopied)} errors={exec.Errors} took={result.Elapsed.TotalSeconds:0.0}s");
        return result;
    }
}
