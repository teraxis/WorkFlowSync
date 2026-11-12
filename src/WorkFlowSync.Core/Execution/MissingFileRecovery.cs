using WorkFlowSync.Core.Config;
using WorkFlowSync.Core.Logging;
using WorkFlowSync.Core.Model;
using WorkFlowSync.Core.Planning;
using WorkFlowSync.Core.Scanning;
using WorkFlowSync.Core.State;

namespace WorkFlowSync.Core.Execution;

public enum MissingFileKind { New, ActiveMissing, Tombstone, PolicyExcluded, Conflict }

public sealed record MissingFileItem(string SourcePath, string TargetPath, MissingFileKind Kind,
    ScanEntry Source, StateEntry? State);

public sealed class MissingFilePlan
{
    public List<MissingFileItem> Items { get; } = new();
    public int TargetOnlyFiles { get; internal set; }
    public int Restorable => Items.Count(i => i.Kind is MissingFileKind.New or MissingFileKind.ActiveMissing);
    public int Deleted => Items.Count(i => i.Kind == MissingFileKind.Tombstone);
    public int Excluded => Items.Count(i => i.Kind == MissingFileKind.PolicyExcluded);
    public int Conflicts => Items.Count(i => i.Kind == MissingFileKind.Conflict);
}

/// <summary>Audits missing copies without changing either tree or the state database.</summary>
public static class MissingFileRecovery
{
    public static MissingFilePlan Prepare(FolderPair pair, SyncConfig config, StateStore store,
        IProgress<RepairProgress>? progress = null)
    {
        if (pair.Mode != SyncMode.Mirror)
            throw new InvalidOperationException("Missing-file recovery supports mirror pairs only");
        var state = store.Load(pair.Name);
        progress?.Report(new RepairProgress("scan_source"));
        var source = new TreeScanner(config.ScanBufferSize, config.ScanParallelism, pair.Links,
            new ExcludeMatcher(pair.Exclude)).Scan(pair.Source);
        progress?.Report(new RepairProgress("scan_target"));
        var target = new TreeScanner(config.ScanBufferSize, config.ScanParallelism, LinkMode.Skip,
            new ExcludeMatcher(pair.Exclude)).Scan(pair.Target);
        if (source.Warnings.Count != 0 || target.Warnings.Count != 0)
            throw new IOException("Recovery audit requires complete source and target scans");
        progress?.Report(new RepairProgress("build"));
        return Build(pair, source.Entries, target.Entries, state);
    }

    public static MissingFilePlan Build(FolderPair pair,
        IReadOnlyDictionary<string, ScanEntry> source,
        IReadOnlyDictionary<string, ScanEntry> target,
        IReadOnlyDictionary<string, StateEntry> state)
    {
        var plan = new MissingFilePlan();
        var stateGroups = state.Values.GroupBy(e => e.RemoteId ?? e.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var stateBySource = stateGroups.ToDictionary(kvp => kvp.Key, kvp => kvp.Value[0],
            StringComparer.OrdinalIgnoreCase);
        var mapped = PathShortener.MapEntries(source, pair.Target, pair.ShortenLongPaths,
            PathLimits.Effective(pair.Target, pair.MaxPathLength), state, null, out var targetToSource);
        var mappedPaths = new HashSet<string>(mapped.Keys, StringComparer.OrdinalIgnoreCase);
        plan.TargetOnlyFiles = target.Count(kvp => kvp.Value.Kind == EntryKind.File && !mappedPaths.Contains(kvp.Key));
        foreach (var (targetPath, entry) in mapped)
        {
            if (entry.Kind != EntryKind.File) continue;
            var sourcePath = targetToSource.GetValueOrDefault(targetPath) ?? entry.RelativePath;
            stateBySource.TryGetValue(sourcePath, out var recorded);
            if (stateGroups.TryGetValue(sourcePath, out var group) && group.Count > 1)
            {
                plan.Items.Add(new MissingFileItem(sourcePath, targetPath, MissingFileKind.Conflict, entry, recorded));
                continue;
            }
            if (target.TryGetValue(targetPath, out var observed))
            {
                if (observed.Kind != EntryKind.File || recorded?.Status == EntryStatus.Tombstone)
                    plan.Items.Add(new MissingFileItem(sourcePath, targetPath, MissingFileKind.Conflict, entry, recorded));
                continue;
            }
            var parent = Path.GetDirectoryName(sourcePath);
            var underTombstone = false;
            while (!string.IsNullOrEmpty(parent))
            {
                if (stateBySource.TryGetValue(parent, out var ancestor) && ancestor.Status == EntryStatus.Tombstone)
                {
                    underTombstone = true;
                    break;
                }
                parent = Path.GetDirectoryName(parent);
            }
            var policyExcluded = recorded?.Status is EntryStatus.TooOld or EntryStatus.LocalModified or
                EntryStatus.Diverged or EntryStatus.LocalOnly ||
                (underTombstone && recorded?.Status != EntryStatus.Tombstone) ||
                pair.KeepLatestFiles is not null ||
                (pair.MaxAge is { } maxAge && recorded is { } prior && prior.FirstSeenUtc < DateTimeOffset.UtcNow - maxAge) ||
                (pair.AutoClean is { } autoClean && recorded is { } old && old.FirstSeenUtc < DateTimeOffset.UtcNow - autoClean) ||
                (pair.CloudFiles == CloudFileMode.Skip && CloudFiles.IsPlaceholder(entry.Attributes)) ||
                GoogleDrive.IsGoogleDoc(sourcePath);
            var kind = policyExcluded ? MissingFileKind.PolicyExcluded : recorded?.Status switch
            {
                EntryStatus.Tombstone => MissingFileKind.Tombstone,
                EntryStatus.Active => MissingFileKind.ActiveMissing,
                _ => MissingFileKind.New,
            };
            plan.Items.Add(new MissingFileItem(sourcePath, targetPath, kind, entry, recorded));
        }
        return plan;
    }

    public static ExecutionResult Apply(FolderPair pair, MissingFilePlan audit, StateStore store, ISyncLog log,
        bool restoreDeleted, IProgress<RepairProgress>? progress = null)
    {
        var candidates = audit.Items.Where(i => i.Kind is MissingFileKind.New or MissingFileKind.ActiveMissing ||
            (restoreDeleted && i.Kind == MissingFileKind.Tombstone)).ToList();
        var syncPlan = new SyncPlan();
        var now = DateTimeOffset.UtcNow;
        if (restoreDeleted)
        {
            var stateByTarget = store.Load(pair.Name).Values
                .GroupBy(e => e.TargetPath ?? e.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var revivedParents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in candidates.Where(i => i.Kind == MissingFileKind.Tombstone))
            {
                var parent = Path.GetDirectoryName(item.TargetPath);
                while (!string.IsNullOrEmpty(parent))
                {
                    if (revivedParents.Add(parent) && stateByTarget.TryGetValue(parent, out var directory) &&
                        directory.Kind == EntryKind.Directory && directory.Status == EntryStatus.Tombstone)
                        syncPlan.Actions.Add(new SyncAction(SyncActionKind.CreateDirectory, parent,
                            directory with { Status = EntryStatus.Active, StatusChangedUtc = now, LastSeenUtc = now }));
                    parent = Path.GetDirectoryName(parent);
                }
            }
            syncPlan.Actions.Sort((a, b) => a.RelativePath.Length.CompareTo(b.RelativePath.Length));
        }
        foreach (var item in candidates)
        {
            var original = item.State;
            var proposed = original is null
                ? new StateEntry
                {
                    PairName = pair.Name,
                    RelativePath = item.TargetPath,
                    TargetPath = item.TargetPath,
                    RemoteId = item.SourcePath,
                    Kind = EntryKind.File,
                    Status = EntryStatus.Active,
                    FirstSeenUtc = now,
                    LastSeenUtc = now,
                    SourceSize = item.Source.Size,
                    SourceMtimeUtc = item.Source.MtimeUtc
                }
                : original with
                {
                    Status = EntryStatus.Active,
                    StatusChangedUtc = now,
                    LastSeenUtc = now,
                    SourceSize = item.Source.Size,
                    SourceMtimeUtc = item.Source.MtimeUtc
                };
            syncPlan.Actions.Add(new SyncAction(SyncActionKind.CopyFile, item.TargetPath, proposed,
                SyncSide.Target, item.SourcePath));
        }
        var executor = new SyncExecutor(pair.Source, pair.Target, log, dryRun: false,
            cloudFiles: pair.CloudFiles, diskQuotaEnabled: pair.DiskQuotaEnabled,
            freeUpSpaceAfterCopy: pair.FreeUpSpaceAfterCopy,
            minFreeSpaceGb: pair.MinFreeSpaceGb, neverOverwriteCopies: true)
        {
            CheckpointEvery = 1,
        };
        var result = executor.Execute(syncPlan, $"[{pair.Name}] recovery", checkpoint: store.Upsert,
            progress: (done, total) => progress?.Report(new RepairProgress("apply", done, total)));
        store.Upsert(result.Completed);
        return result;
    }
}
