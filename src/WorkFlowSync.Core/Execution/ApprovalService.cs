using WorkFlowSync.Core.Config;
using WorkFlowSync.Core.Logging;
using WorkFlowSync.Core.Model;
using WorkFlowSync.Core.Planning;
using WorkFlowSync.Core.State;

namespace WorkFlowSync.Core.Execution;

public sealed record ApprovalResult
{
    public int ApprovedCount { get; set; }
    public int DismissedCount { get; set; }
    public int RevertedCount { get; set; }
    public int StaleCount { get; set; }
    public int ConflictCount { get; set; }
    public int UnavailableCount { get; set; }
    public List<string> Errors { get; } = new();

    public bool IsSuccess => Errors.Count == 0 && StaleCount == 0 && ConflictCount == 0 && UnavailableCount == 0;
}

/// <summary>
/// Service executing human approval decisions (Approve, Dismiss, Revert) on the pending queue (docs/plan-etap6.md §5.4 & §5.7).
/// Adheres strictly to safety checks (§5.7) and invariant 1 (source read-only except during explicit approved actions).
/// </summary>
public sealed class ApprovalService
{
    private readonly StateStore _store;
    private readonly ISyncLog _log;

    public ApprovalService(StateStore store, ISyncLog log)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>
    /// Approves a pending change (✓): applies the change to the source folder A.
    /// </summary>
    public Task<ApprovalResult> ApproveAsync(long pendingId, string sourceRoot, string targetRoot, DateTimeOffset now, CancellationToken ct = default)
    {
        return Task.FromResult(ProcessEntry(pendingId, sourceRoot, targetRoot, now, DecisionType.Approve, ct));
    }

    /// <summary>
    /// Dismisses a pending change (⃠): leaves target B as is, records state as Diverged/LocalOnly/Tombstone.
    /// </summary>
    public Task<ApprovalResult> DismissAsync(long pendingId, string sourceRoot, string targetRoot, DateTimeOffset now, CancellationToken ct = default)
    {
        return Task.FromResult(ProcessEntry(pendingId, sourceRoot, targetRoot, now, DecisionType.Dismiss, ct));
    }

    /// <summary>
    /// Reverts a pending change (↺): restores target B to the initial state from source A.
    /// </summary>
    public Task<ApprovalResult> RevertAsync(long pendingId, string sourceRoot, string targetRoot, DateTimeOffset now, CancellationToken ct = default)
    {
        return Task.FromResult(ProcessEntry(pendingId, sourceRoot, targetRoot, now, DecisionType.Revert, ct));
    }

    private enum DecisionType { Approve, Dismiss, Revert }

    private ApprovalResult ProcessEntry(
        long pendingId,
        string sourceRoot,
        string targetRoot,
        DateTimeOffset now,
        DecisionType decision,
        CancellationToken ct)
    {
        var result = new ApprovalResult();
        var entry = _store.Pending.GetById(pendingId);

        if (entry is null || entry.Status != PendingStatus.Pending)
        {
            result.Errors.Add($"Pending entry #{pendingId} not found or no longer open.");
            return result;
        }

        var pairName = entry.Pair;
        var fullSource = Path.GetFullPath(sourceRoot).TrimEnd('\\', '/');
        var fullTarget = Path.GetFullPath(targetRoot).TrimEnd('\\', '/');

        // Positive verification §5.7: check if source A is accessible
        if (!Directory.Exists(fullSource) && decision != DecisionType.Dismiss)
        {
            result.UnavailableCount = 1;
            result.Errors.Add($"Source root '{fullSource}' is unavailable; decision cannot be applied.");
            return result;
        }

        // Positive verification §5.7: check if target B changed again since detection time
        var targetFile = Path.Combine(fullTarget, entry.Path);
        var targetExists = File.Exists(targetFile) || Directory.Exists(targetFile);

        if (targetExists && entry.Change != PendingChange.Deleted)
        {
            var info = new FileInfo(targetFile);
            if (info.Exists)
            {
                var curSize = info.Length;
                var curMtime = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
                if (entry.DstSize.HasValue && entry.DstSize.Value != curSize)
                {
                    _store.Pending.Resolve(entry.Id, PendingStatus.Stale, now);
                    _store.Pending.Upsert(entry with { DstSize = curSize, DstMtimeUtc = curMtime, DetectedUtc = now });
                    result.StaleCount = 1;
                    result.Errors.Add($"{entry.Path}: file in target changed again since detection.");
                    return result;
                }
            }
        }

        try
        {
            switch (decision)
            {
                case DecisionType.Approve:
                    ExecuteApprove(entry, fullSource, fullTarget, pairName, now, result, ct);
                    break;
                case DecisionType.Dismiss:
                    ExecuteDismiss(entry, pairName, now, result);
                    break;
                case DecisionType.Revert:
                    ExecuteRevert(entry, fullSource, fullTarget, pairName, now, result, ct);
                    break;
            }
        }
        catch (Exception ex)
        {
            result.Errors.Add($"{entry.Path}: error executing decision: {ex.Message}");
        }

        return result;
    }

    private void ExecuteApprove(PendingEntry entry, string sourceRoot, string targetRoot, string pairName, DateTimeOffset now, ApprovalResult result, CancellationToken ct)
    {
        var executor = new SyncExecutor(sourceRoot, targetRoot, _log, dryRun: false);

        if (entry.Change is PendingChange.Added or PendingChange.Modified or PendingChange.Conflict)
        {
            var targetFile = Path.Combine(targetRoot, entry.Path);
            var isDir = Directory.Exists(targetFile);
            var actionKind = isDir ? SyncActionKind.CreateDirectory : SyncActionKind.CopyFile;

            var plan = new SyncPlan();
            var proposed = new StateEntry
            {
                PairName = pairName,
                RelativePath = entry.Path,
                Kind = isDir ? EntryKind.Directory : EntryKind.File,
                Status = EntryStatus.Active,
                FirstSeenUtc = entry.DetectedUtc,
                LastSeenUtc = now,
                SourceSize = entry.DstSize,
                SourceMtimeUtc = entry.DstMtimeUtc,
                CopiedSize = entry.DstSize,
                CopiedMtimeUtc = entry.DstMtimeUtc,
            };

            plan.Actions.Add(new SyncAction(actionKind, entry.Path, proposed, SyncSide.Source));
            var execRes = executor.Execute(plan, $"[{pairName}]", ct);

            if (execRes.Errors == 0)
            {
                _store.Upsert(execRes.Completed.Count > 0 ? execRes.Completed : new[] { proposed });
                _store.Pending.Resolve(entry.Id, PendingStatus.Approved, now);
                result.ApprovedCount = 1;
            }
            else
            {
                result.Errors.Add($"Failed to copy approved file {entry.Path} to source.");
            }
        }
        else if (entry.Change == PendingChange.Deleted)
        {
            var sourceFile = Path.Combine(sourceRoot, entry.Path);
            var isDir = Directory.Exists(sourceFile);
            var actionKind = isDir ? SyncActionKind.DeleteDirectory : SyncActionKind.DeleteFile;

            var plan = new SyncPlan();
            var proposed = new StateEntry
            {
                PairName = pairName,
                RelativePath = entry.Path,
                Kind = isDir ? EntryKind.Directory : EntryKind.File,
                Status = EntryStatus.Tombstone,
                FirstSeenUtc = entry.DetectedUtc,
                LastSeenUtc = now,
            };

            plan.Actions.Add(new SyncAction(actionKind, entry.Path, proposed, SyncSide.Source));
            var execRes = executor.Execute(plan, $"[{pairName}]", ct);

            if (execRes.Errors == 0)
            {
                _store.Upsert(new[] { proposed });
                _store.Pending.Resolve(entry.Id, PendingStatus.Approved, now);
                result.ApprovedCount = 1;
            }
            else
            {
                result.Errors.Add($"Failed to delete approved path {entry.Path} in source.");
            }
        }
        else if (entry.Change is PendingChange.Renamed or PendingChange.Moved)
        {
            if (!string.IsNullOrEmpty(entry.FromPath))
            {
                var oldSource = Path.Combine(sourceRoot, entry.FromPath);
                var newSource = Path.Combine(sourceRoot, entry.Path);

                var dir = Path.GetDirectoryName(newSource);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                if (File.Exists(oldSource))
                {
                    File.Move(oldSource, newSource, overwrite: true);
                }
                else if (Directory.Exists(oldSource))
                {
                    Directory.Move(oldSource, newSource);
                }

                _store.Delete(pairName, new[] { entry.FromPath });
                _store.Upsert(new[]
                {
                    new StateEntry
                    {
                        PairName = pairName,
                        RelativePath = entry.Path,
                        Kind = entry.Kind,
                        Status = EntryStatus.Active,
                        FirstSeenUtc = entry.DetectedUtc,
                        LastSeenUtc = now,
                        SourceSize = entry.DstSize,
                        SourceMtimeUtc = entry.DstMtimeUtc,
                        CopiedSize = entry.DstSize,
                        CopiedMtimeUtc = entry.DstMtimeUtc,
                    }
                });

                _store.Pending.Resolve(entry.Id, PendingStatus.Approved, now);
                result.ApprovedCount = 1;
            }
        }
    }

    private void ExecuteDismiss(PendingEntry entry, string pairName, DateTimeOffset now, ApprovalResult result)
    {
        var targetStatus = entry.Change switch
        {
            PendingChange.Added => EntryStatus.LocalOnly,
            PendingChange.Deleted => EntryStatus.Tombstone,
            _ => EntryStatus.Diverged,
        };

        _store.Upsert(new[]
        {
            new StateEntry
            {
                PairName = pairName,
                RelativePath = entry.Path,
                Kind = entry.Kind,
                Status = targetStatus,
                FirstSeenUtc = entry.DetectedUtc,
                LastSeenUtc = now,
                SourceSize = entry.SrcSize,
                SourceMtimeUtc = entry.SrcMtimeUtc,
                CopiedSize = entry.DstSize,
                CopiedMtimeUtc = entry.DstMtimeUtc,
            }
        });

        _store.Pending.Resolve(entry.Id, PendingStatus.Dismissed, now);
        result.DismissedCount = 1;
    }

    private void ExecuteRevert(PendingEntry entry, string sourceRoot, string targetRoot, string pairName, DateTimeOffset now, ApprovalResult result, CancellationToken ct)
    {
        var executor = new SyncExecutor(sourceRoot, targetRoot, _log, dryRun: false);

        if (entry.Change == PendingChange.Added)
        {
            var targetFile = Path.Combine(targetRoot, entry.Path);
            if (File.Exists(targetFile))
            {
                if (RecycleBin.IsAvailableFor(targetFile))
                {
                    RecycleBin.Send(targetFile);
                }
                else
                {
                    File.Delete(targetFile);
                }
            }
            else if (Directory.Exists(targetFile))
            {
                Directory.Delete(targetFile, recursive: true);
            }

            _store.Delete(pairName, new[] { entry.Path });
            _store.Pending.Resolve(entry.Id, PendingStatus.Reverted, now);
            result.RevertedCount = 1;
        }
        else if (entry.Change is PendingChange.Modified or PendingChange.Deleted or PendingChange.Conflict)
        {
            var sourceFile = Path.Combine(sourceRoot, entry.Path);
            var isDir = Directory.Exists(sourceFile);
            var actionKind = isDir ? SyncActionKind.CreateDirectory : SyncActionKind.CopyFile;

            var proposed = new StateEntry
            {
                PairName = pairName,
                RelativePath = entry.Path,
                Kind = isDir ? EntryKind.Directory : EntryKind.File,
                Status = EntryStatus.Active,
                FirstSeenUtc = entry.DetectedUtc,
                LastSeenUtc = now,
                SourceSize = entry.SrcSize,
                SourceMtimeUtc = entry.SrcMtimeUtc,
                CopiedSize = entry.SrcSize,
                CopiedMtimeUtc = entry.SrcMtimeUtc,
            };

            var plan = new SyncPlan();
            plan.Actions.Add(new SyncAction(actionKind, entry.Path, proposed, SyncSide.Target));
            var execRes = executor.Execute(plan, $"[{pairName}]", ct);

            if (execRes.Errors == 0)
            {
                _store.Upsert(execRes.Completed.Count > 0 ? execRes.Completed : new[] { proposed });
                _store.Pending.Resolve(entry.Id, PendingStatus.Reverted, now);
                result.RevertedCount = 1;
            }
            else
            {
                result.Errors.Add($"Failed to revert {entry.Path} in target.");
            }
        }
        else if (entry.Change is PendingChange.Renamed or PendingChange.Moved)
        {
            if (!string.IsNullOrEmpty(entry.FromPath))
            {
                var oldTarget = Path.Combine(targetRoot, entry.Path);
                var origTarget = Path.Combine(targetRoot, entry.FromPath);

                var dir = Path.GetDirectoryName(origTarget);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                if (File.Exists(oldTarget))
                {
                    File.Move(oldTarget, origTarget, overwrite: true);
                }
                else if (Directory.Exists(oldTarget))
                {
                    Directory.Move(oldTarget, origTarget);
                }

                _store.Delete(pairName, new[] { entry.Path });
                _store.Upsert(new[]
                {
                    new StateEntry
                    {
                        PairName = pairName,
                        RelativePath = entry.FromPath,
                        Kind = entry.Kind,
                        Status = EntryStatus.Active,
                        FirstSeenUtc = entry.DetectedUtc,
                        LastSeenUtc = now,
                        SourceSize = entry.SrcSize,
                        SourceMtimeUtc = entry.SrcMtimeUtc,
                        CopiedSize = entry.SrcSize,
                        CopiedMtimeUtc = entry.SrcMtimeUtc,
                    }
                });

                _store.Pending.Resolve(entry.Id, PendingStatus.Reverted, now);
                result.RevertedCount = 1;
            }
        }
    }
}
