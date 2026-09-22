using WorkFlowSync.Core.Logging;
using WorkFlowSync.Core.Model;
using WorkFlowSync.Core.Planning;
using WorkFlowSync.Core.State;
using WorkFlowSync.Core.Watching;

namespace WorkFlowSync.Core.Execution;

public sealed record DiskRotationResult(
    int DeletedFiles,
    long FreedBytes,
    bool ReserveSatisfied,
        string? RefusalReason = null);

/// <summary>
/// Emergency target-only rotation for any destination. It permanently removes only files that this build
/// has actually copied and that still exactly match the recorded copy. On a cloud-synchronised destination,
/// the provider may propagate the permanent deletion to the cloud; enabling rotation is an explicit choice.
/// </summary>
public sealed class DiskQuotaRotator
{
    private readonly ICloudFilePlatform _platform;
    private readonly ISyncLog _log;

    public DiskQuotaRotator(ICloudFilePlatform platform, ISyncLog log)
    {
        _platform = platform;
        _log = log;
    }

    public SelfWriteLog? SelfWrites { get; init; }

    public DiskRotationResult Rotate(string pairName, string targetRoot, long requiredFreeBytes,
        IDictionary<string, StateEntry> state, StateStore store, CancellationToken ct = default)
    {
        var tag = $"[{pairName}]";
        var cloudTarget = _platform.GetSyncRootStatus(targetRoot) == CloudRootStatus.Cloud;
        var initial = _platform.GetDiskSpace(targetRoot);
        if (initial.AvailableBytes >= requiredFreeBytes)
            return new DiskRotationResult(0, 0, true);

        var root = Path.GetFullPath(targetRoot).TrimEnd('\\', '/');
        var prefix = root + Path.DirectorySeparatorChar;
        var candidates = new List<Candidate>();
        foreach (var entry in state.Values)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.Kind != EntryKind.File || entry.Status != EntryStatus.Active ||
                entry.CopiedAtUtc is null || entry.CopiedSize is null || entry.CopiedMtimeUtc is null)
                continue;

            var fullPath = Path.GetFullPath(Path.Combine(root, entry.RelativePath));
            if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath)) continue;

            try
            {
                var info = new FileInfo(fullPath);
                // Cloud Files placeholders are reparse points too. They are safe to delete within a
                // confirmed sync root; other reparse points remain excluded from permanent rotation.
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0 && !cloudTarget) continue;
                var modified = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
                if (info.Length != entry.CopiedSize ||
                    (modified - entry.CopiedMtimeUtc.Value).Duration() > SyncPlanner.MtimeTolerance)
                    continue;
                var created = new DateTimeOffset(info.CreationTimeUtc, TimeSpan.Zero);
                candidates.Add(new Candidate(entry, fullPath, info.Length, modified, created));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Warn($"{tag} rotation skipped unreadable candidate: {fullPath}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        candidates.Sort(static (a, b) =>
        {
            var byCopy = Nullable.Compare(a.Entry.CopiedAtUtc, b.Entry.CopiedAtUtc);
            if (byCopy != 0) return byCopy;
            var byModified = a.ModifiedUtc.CompareTo(b.ModifiedUtc);
            if (byModified != 0) return byModified;
            var byCreated = a.CreatedUtc.CompareTo(b.CreatedUtc);
            return byCreated != 0
                ? byCreated
                : StringComparer.OrdinalIgnoreCase.Compare(a.Entry.RelativePath, b.Entry.RelativePath);
        });

        var deleted = 0;
        long freed = 0;
        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // Positive re-check immediately before irreversible deletion. A local edit after the scan
                // disqualifies the file and is preserved for the normal planner to mark local_modified.
                var info = new FileInfo(candidate.FullPath);
                if (!info.Exists || ((info.Attributes & FileAttributes.ReparsePoint) != 0 && !cloudTarget) ||
                    info.Length != candidate.Entry.CopiedSize ||
                    (new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero) - candidate.Entry.CopiedMtimeUtc!.Value).Duration()
                        > SyncPlanner.MtimeTolerance)
                    continue;

                var now = DateTimeOffset.UtcNow;
                var tombstone = candidate.Entry with
                {
                    Status = EntryStatus.Tombstone,
                    StatusChangedUtc = now,
                    LastSeenUtc = now,
                };
                // Persist the final decision before the irreversible operation. A crash between these two
                // lines may leave an extra file on disk, but can never delete a file and then re-copy it.
                store.Upsert(new[] { tombstone });
                state[candidate.Entry.RelativePath] = tombstone;
                try
                {
                    SelfWrites?.Record(candidate.FullPath);
                    File.Delete(candidate.FullPath);
                }
                catch
                {
                    // The bytes are still present, so restore the active row and let the next pass re-evaluate it.
                    store.Upsert(new[] { candidate.Entry });
                    state[candidate.Entry.RelativePath] = candidate.Entry;
                    throw;
                }
                deleted++;
                freed += candidate.Size;
                _log.Warn($"{tag} rotation deleted PERMANENTLY: {candidate.FullPath}");

                if (_platform.GetDiskSpace(targetRoot).AvailableBytes >= requiredFreeBytes)
                    return new DiskRotationResult(deleted, freed, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Error($"{tag} rotation could not delete {candidate.FullPath}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        var satisfied = _platform.GetDiskSpace(targetRoot).AvailableBytes >= requiredFreeBytes;
        if (!satisfied)
            _log.Warn($"{tag} rotation exhausted safe candidates but the free-space reserve is still not available");
        return new DiskRotationResult(deleted, freed, satisfied);
    }

    private sealed record Candidate(
        StateEntry Entry,
        string FullPath,
        long Size,
        DateTimeOffset ModifiedUtc,
        DateTimeOffset CreatedUtc);
}
