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

    public PassResult Run(bool dryRun, bool forceBackfill = false, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var pass = new PassResult { DryRun = dryRun };
        _log.Info($"pass start  pairs={_config.Pairs.Count}{(dryRun ? "  mode=dry-run" : "")}");

        using var store = new StateStore(StatePath);
        foreach (var pair in _config.Pairs)
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
        var sw = Stopwatch.StartNew();
        var result = new PairRunResult { Pair = pair.Name };
        var now = DateTimeOffset.UtcNow;

        // 1. State (in memory).
        var state = store.Load(pair.Name);
        var backfill = forceBackfill || state.Count == 0;
        if (backfill) _log.Info($"{tag} first pass for this pair: first_seen backfilled from min(ctime, mtime)");

        // 2. Source (read-only). Unavailable root = skip, never a state change (rule: never mistake offline for deleted).
        var excludes = new ExcludeMatcher(pair.Exclude);
        bool IsTombstoneDir(string rel) => state.TryGetValue(rel, out var e) && e.Kind == EntryKind.Directory && e.Status == EntryStatus.Tombstone;
        ScanResult source;
        try
        {
            var scanner = new TreeScanner(_config.ScanBufferSize, _config.ScanParallelism, pair.Links, excludes, IsTombstoneDir);
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
            var targetScanner = new TreeScanner(_config.ScanBufferSize, _config.ScanParallelism, LinkMode.Skip);
            target = targetScanner.Scan(pair.Target, ct);
            foreach (var w in target.Warnings) _log.Warn($"{tag} target: {w}");
        }
        result.TargetScan = target;
        _log.Info($"{tag} scan target  dirs={target.DirectoryCount} files={target.FileCount} took={target.Elapsed.TotalSeconds:0.0}s");

        // 4. Plan (pure).
        var plan = new SyncPlanner(pair.Name, now, backfill).Plan(source.Entries, target.Entries, state);
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
            if (rows.Count > 0) store.Upsert(rows);
            _log.Info($"{tag} state  rows_written={rows.Count}");
        }

        result.Elapsed = sw.Elapsed;
        _log.Info($"{tag} done  copied={exec.FilesCopied} updated={exec.FilesUpdated} mkdir={exec.DirectoriesCreated} bytes={SyncExecutor.FormatSize(exec.BytesCopied)} errors={exec.Errors} took={result.Elapsed.TotalSeconds:0.0}s");
        return result;
    }
}
