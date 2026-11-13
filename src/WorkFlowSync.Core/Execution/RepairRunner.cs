using WorkFlowSync.Core.Config;
using WorkFlowSync.Core.Logging;
using WorkFlowSync.Core.State;

namespace WorkFlowSync.Core.Execution;

/// <summary>Runs repair phases without any UI dependency and records every attempted phase.</summary>
public sealed class RepairRunner
{
    private readonly FolderPair _pair;
    private readonly SyncConfig _config;
    private readonly string _configPath;
    private readonly string _statePath;
    private readonly ISyncLog _log;

    public RepairRunner(FolderPair pair, SyncConfig config, string configPath, string statePath, ISyncLog log)
    {
        _pair = pair;
        _config = config;
        _configPath = configPath;
        _statePath = statePath;
        _log = log;
    }

    public TargetRepair.Plan Prepare(IProgress<RepairProgress>? progress = null)
    {
        _log.Info($"[{_pair.Name}] repair start phase=plan");
        try
        {
            using var gate = PassLock.TryAcquire(_configPath, _pair.Name);
            if (!gate.Acquired) throw new IOException("Another pass is running for this pair");
            using var store = new StateStore(_statePath, readOnly: true);
            var plan = TargetRepair.Prepare(_pair, _config, store, progress);
            _log.Info($"[{_pair.Name}] repair check paths={plan.CheckedPaths} too_long={plan.TooLongPaths} duplicates={plan.DuplicatePaths} short_hash={plan.ShortHashes}");
            foreach (var row in plan.Rows.Where(r => r.Action == "conflict"))
                _log.Warn($"[{_pair.Name}] repair conflict source={row.OldPath} target={row.NewPath} detail={row.Detail}");
            _log.Info($"[{_pair.Name}] repair plan keep={plan.KeepFiles} rename={plan.RenameFiles} recycle={plan.RecycleFiles} conflicts={plan.Conflicts}");
            return plan;
        }
        catch (Exception ex)
        {
            _log.Error($"[{_pair.Name}] repair ERROR {ex}");
            throw;
        }
    }

    public void Apply(TargetRepair.Plan plan, IProgress<RepairProgress>? progress = null)
    {
        _log.Info($"[{_pair.Name}] repair start phase=apply actions={plan.ActionCount}");
        try
        {
            if (_pair.Enabled) throw new InvalidOperationException("Pause the pair before applying repair");
            if (!string.Equals(plan.Pair, _pair.Name, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Repair plan belongs to another pair");
            using var gate = PassLock.TryAcquire(_configPath, _pair.Name);
            if (!gate.Acquired) throw new IOException("Another pass is running for this pair");
            using var store = new StateStore(_statePath);
            TargetRepair.Execute(plan, store, progress,
                row => _log.Info($"[{_pair.Name}] repair action={row.Action} old={row.OldPath} new={row.NewPath}"));
            _log.Info($"[{_pair.Name}] repair done actions={plan.ActionCount} conflicts={plan.Conflicts}");
        }
        catch (Exception ex)
        {
            _log.Error($"[{_pair.Name}] repair ERROR {ex}");
            throw;
        }
    }

    public MissingFilePlan PrepareMissing(IProgress<RepairProgress>? progress = null)
    {
        _log.Info($"[{_pair.Name}] recovery audit start");
        try
        {
            using var gate = PassLock.TryAcquire(_configPath, _pair.Name);
            if (!gate.Acquired) throw new IOException("Another pass is running for this pair");
            using var store = new StateStore(_statePath, readOnly: true);
            var audit = MissingFileRecovery.Prepare(_pair, _config, store, progress);
            _log.Info($"[{_pair.Name}] recovery audit restorable={audit.Restorable} deleted={audit.Deleted} excluded={audit.Excluded} conflicts={audit.Conflicts} target_only={audit.TargetOnlyFiles}");
            return audit;
        }
        catch (Exception ex)
        {
            _log.Error($"[{_pair.Name}] recovery audit ERROR {ex}");
            throw;
        }
    }

    public ExecutionResult ApplyMissing(MissingFilePlan audit, bool restoreDeleted,
        IProgress<RepairProgress>? progress = null)
    {
        _log.Info($"[{_pair.Name}] recovery apply start restore_deleted={restoreDeleted}");
        try
        {
            if (_pair.Enabled) throw new InvalidOperationException("Pause the pair before recovery");
            using var gate = PassLock.TryAcquire(_configPath, _pair.Name);
            if (!gate.Acquired) throw new IOException("Another pass is running for this pair");
            using var store = new StateStore(_statePath);
            var result = MissingFileRecovery.Apply(_pair, audit, store, _log, restoreDeleted, progress);
            _log.Info($"[{_pair.Name}] recovery done copied={result.FilesCopied} skipped={result.Skipped} errors={result.Errors}");
            return result;
        }
        catch (Exception ex)
        {
            _log.Error($"[{_pair.Name}] recovery ERROR {ex}");
            throw;
        }
    }
}
