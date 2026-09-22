using WorkFlowSync.Core.Config;
using WorkFlowSync.Core.Logging;
using WorkFlowSync.Core.Model;
using WorkFlowSync.Core.Planning;
using WorkFlowSync.Core.Watching;

namespace WorkFlowSync.Core.Execution;

public sealed class ExecutionResult
{
    public int DirectoriesCreated { get; set; }
    public int FilesCopied { get; set; }
    public int FilesUpdated { get; set; }
    public int Errors { get; set; }
    public int FilesRecycled { get; set; }
    public int DirectoriesRecycled { get; set; }
    public long BytesCopied { get; set; }

    /// <summary>Files handed to Windows/OneDrive with the «online-only» intent.</summary>
    public int OnlineOnlyRequested { get; set; }

    /// <summary>How many times copying had to wait for OneDrive to release disk space.</summary>
    public int CloudSpaceWaits { get; set; }

    /// <summary>How many passes stopped adding target files because the independent disk quota was reached.</summary>
    public int DiskQuotaStops { get; set; }

    /// <summary>Actions a safeguard declined to carry out: a cloud-only file, or a removal the other side contradicted.</summary>
    public int Skipped { get; set; }

    /// <summary>
    /// State rows still to persist: successful actions with copied_* filled in. Rows handed to a
    /// checkpoint are removed from here — the caller only has to write what is left.
    /// </summary>
    public List<StateEntry> Completed { get; } = new();

    /// <summary>How many rows were already written by checkpoints during the pass.</summary>
    public int CheckpointedRows { get; set; }

    /// <summary>State rows to delete (directories recycled by retention — they may legitimately come back).</summary>
    public List<string> Forgotten { get; } = new();
}

/// <summary>
/// Applies a <see cref="SyncPlan"/>. In mirror mode the source is only ever opened for reading; in two-way mode
/// an action carrying <see cref="SyncSide.Source"/> writes back to the source root instead.
/// Per-file failures are logged and skipped (the entry stays absent from state, so the next pass retries).
/// </summary>
public sealed class SyncExecutor
{
    private readonly string _sourceRoot;
    private readonly string _targetRoot;
    private readonly ISyncLog _log;
    private readonly bool _dryRun;
    private readonly CloudFileMode _cloudFiles;
    private readonly bool _diskQuotaEnabled;
    private readonly bool _freeUpSpaceAfterCopy;
    private readonly long _minFreeSpaceBytes;
    private readonly ICloudFilePlatform _cloudPlatform;
    private bool? _cloudTargetReady;
    private bool _cloudTargetFailureReported;
    private bool _cloudRequestFailed;
    private bool _quotaBlocked;

    // Answered once per pass instead of once per file: both are properties of the root, not of the item.
    private readonly bool _sourceHasRecycleBin;
    private readonly bool _targetHasRecycleBin;
    private readonly bool _sourceMayHavePlaceholders;
    private readonly bool _targetMayHavePlaceholders;

    /// <summary>
    /// Where to note everything this executor writes, so the watcher can ignore our own footsteps
    /// (docs/plan-etap5.md §4.3, rule 7). Null when nobody is watching.
    /// </summary>
    public SelfWriteLog? SelfWrites { get; init; }

    public SyncExecutor(string sourceRoot, string targetRoot, ISyncLog log, bool dryRun,
        CloudFileMode cloudFiles = CloudFileMode.Skip, bool diskQuotaEnabled = false, bool freeUpSpaceAfterCopy = false,
        int minFreeSpaceGb = 5, ICloudFilePlatform? cloudPlatform = null)
    {
        _sourceRoot = Path.GetFullPath(sourceRoot).TrimEnd('\\', '/');
        _targetRoot = Path.GetFullPath(targetRoot).TrimEnd('\\', '/');
        _log = log;
        _dryRun = dryRun;
        _cloudFiles = cloudFiles;
        _diskQuotaEnabled = diskQuotaEnabled;
        _freeUpSpaceAfterCopy = freeUpSpaceAfterCopy;
        _minFreeSpaceBytes = checked((long)minFreeSpaceGb * 1024 * 1024 * 1024);
        _cloudPlatform = cloudPlatform ?? new WindowsCloudFilePlatform();
        _sourceHasRecycleBin = RecycleBin.IsAvailableFor(_sourceRoot);
        _targetHasRecycleBin = RecycleBin.IsAvailableFor(_targetRoot);
        _sourceMayHavePlaceholders = CloudFiles.Possible(_sourceRoot);
        _targetMayHavePlaceholders = CloudFiles.Possible(_targetRoot);
    }

    /// <summary>Rows that may wait unwritten before a checkpoint is due. Lowered by tests.</summary>
    public int CheckpointEvery { get; init; } = 2000;

    /// <summary>…or this long, so a slow copy of a few huge files is not left unrecorded either.</summary>
    public TimeSpan CheckpointInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Polling cadence while a Files On-Demand target is releasing space. Lowered by tests.</summary>
    public TimeSpan CloudSpacePollInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <param name="checkpoint">
    /// Called with the rows completed since the last call, so a long first pass survives being interrupted
    /// (docs/plan-etap5.md §4.4, point 4). Null in dry-run and wherever nothing should be persisted.
    /// </param>
    public ExecutionResult Execute(SyncPlan plan, string pairTag, CancellationToken ct = default,
        Action<IReadOnlyList<StateEntry>>? checkpoint = null)
    {
        var result = new ExecutionResult();
        var prefix = _dryRun ? $"{pairTag} [dry-run] " : $"{pairTag} ";
        var sinceCheckpoint = System.Diagnostics.Stopwatch.StartNew();

        void FlushCheckpoint(bool force)
        {
            if (checkpoint is null || result.Completed.Count == 0) return;
            if (!force && result.Completed.Count < CheckpointEvery && sinceCheckpoint.Elapsed < CheckpointInterval) return;
            try
            {
                // Clear only after the write returns: a failed checkpoint must not drop the rows on the floor.
                checkpoint(result.Completed);
                result.CheckpointedRows += result.Completed.Count;
                result.Completed.Clear();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The files are already copied; the rows simply wait for the write at the end of the pass.
                _log.Warn($"{pairTag} checkpoint failed, {result.Completed.Count} rows deferred: {ex.GetType().Name}: {ex.Message}");
            }
            sinceCheckpoint.Restart();
        }

        foreach (var action in plan.Actions)
        {
            ct.ThrowIfCancellationRequested();
            FlushCheckpoint(force: false);
            // Mirror only ever writes to the target; two-way may also write back to the source (docs F10).
            var back = action.Side == SyncSide.Source;
            var src = Path.Combine(back ? _targetRoot : _sourceRoot, action.RelativePath);
            var dst = Path.Combine(back ? _sourceRoot : _targetRoot, action.RelativePath);
            var where = back ? " <-" : "";
            // Noted before the write, not after: the notification can reach the watcher first.
            SelfWrites?.Record(dst);
            try
            {
                switch (action.Kind)
                {
                    case SyncActionKind.CreateDirectory:
                        _log.Info($"{prefix}mkdir{where}  \\{action.RelativePath}");
                        if (!_dryRun) Directory.CreateDirectory(dst);
                        result.DirectoriesCreated++;
                        result.Completed.Add(action.Proposed);
                        break;

                    case SyncActionKind.CopyFile:
                    case SyncActionKind.UpdateFile:
                        // Reading a cloud-only file downloads it in full. On a large tree that would silently
                        // pull back everything the user freed up, so it needs saying yes to (docs §4.6).
                        if (_cloudFiles == CloudFileMode.Skip && MayBePlaceholder(back) && CloudFiles.IsPlaceholder(src))
                        {
                            _log.Warn($"{pairTag} skip  \\{action.RelativePath}: the file is only in the cloud; copying it would download it " +
                                      "(pair setting «Файли з хмари»)");
                            result.Skipped++;
                            break;
                        }

                        // The setting is deliberately target-only. In a two-way pair a reverse action writes
                        // to the source, whose storage policy belongs to that side rather than to this target.
                        var manageCloud = !back && !_dryRun && _freeUpSpaceAfterCopy;
                        var protectQuota = !back && !_dryRun && _diskQuotaEnabled;
                        if (manageCloud && !EnsureCloudTarget(pairTag, result))
                        {
                            result.Skipped++;
                            break;
                        }
                        if (manageCloud && _cloudRequestFailed)
                        {
                            result.Skipped++;
                            break;
                        }
                        if (protectQuota && _quotaBlocked)
                        {
                            result.Skipped++;
                            break;
                        }
                        if (protectQuota)
                        {
                            var incomingBytes = Math.Max(0, action.Proposed.SourceSize ?? 0);
                            if (!EnsureFreeSpace(incomingBytes, manageCloud, pairTag, result,
                                    () => FlushCheckpoint(force: true), ct))
                            {
                                result.Skipped++;
                                break;
                            }
                        }

                        var verb = action.Kind == SyncActionKind.CopyFile ? "copy " : "update";
                        _log.Info($"{prefix}{verb}{where}  \\{action.RelativePath}  {FormatSize(action.Proposed.SourceSize ?? 0)}");
                        if (_dryRun)
                        {
                            result.Completed.Add(action.Proposed);
                        }
                        else
                        {
                            var copied = CopyPreservingTime(src, dst, action.Proposed.SourceMtimeUtc);
                            var copiedAt = DateTimeOffset.UtcNow;
                            // Refresh the side we wrote: that is the copy later passes compare against.
                            result.Completed.Add(back
                                ? action.Proposed with { SourceSize = copied.Size, SourceMtimeUtc = copied.Mtime }
                                : action.Proposed with
                                {
                                    CopiedSize = copied.Size,
                                    CopiedMtimeUtc = copied.Mtime,
                                    CopiedAtUtc = copiedAt,
                                });

                            if (manageCloud)
                            {
                                try
                                {
                                    _cloudPlatform.RequestOnlineOnly(dst);
                                    result.OnlineOnlyRequested++;
                                    _log.Debug($"{pairTag} online-only requested  \\{action.RelativePath}");
                                }
                                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
                                {
                                    // The copy itself is complete and must remain in state. Stop adding more bytes,
                                    // though: continuing after the space-release request failed defeats the safety setting.
                                    _cloudRequestFailed = true;
                                    result.Errors++;
                                    _log.Error($"{pairTag} cannot request online-only state for \\{action.RelativePath}; " +
                                               $"further target copies are paused: {ex.GetType().Name}: {ex.Message}");
                                }
                            }
                        }
                        if (action.Kind == SyncActionKind.CopyFile) result.FilesCopied++; else result.FilesUpdated++;
                        result.BytesCopied += action.Proposed.SourceSize ?? 0;
                        break;

                    case SyncActionKind.RecycleFile:
                        _log.Info($"{prefix}recycle  \\{action.RelativePath}  (expired, first seen {action.Proposed.FirstSeenUtc.ToLocalTime():yyyy-MM-dd})");
                        if (!_dryRun) RecycleBin.Send(dst);
                        result.FilesRecycled++;
                        result.Completed.Add(action.Proposed);
                        break;

                    case SyncActionKind.RecycleEmptyDirectory:
                        if (!_dryRun && Directory.Exists(dst) && Directory.EnumerateFileSystemEntries(dst).Any())
                        {
                            _log.Warn($"{pairTag} rmdir  \\{action.RelativePath}: not empty any more, kept");
                            break;
                        }
                        _log.Info($"{prefix}rmdir  \\{action.RelativePath}  (empty after retention)");
                        if (!_dryRun && Directory.Exists(dst)) RecycleBin.Send(dst);
                        result.DirectoriesRecycled++;
                        result.Forgotten.Add(action.RelativePath);
                        break;

                    case SyncActionKind.DeleteFile:
                        // Positive confirmation (docs §4.2): the deletion was inferred from a scan, so check
                        // the very path before acting on it. A stale or partial scan must not cost a file.
                        if (!_dryRun && (File.Exists(src) || Directory.Exists(src)))
                        {
                            _log.Warn($"{pairTag} delete  \\{action.RelativePath}: the other side still has it — not removed, the next pass will re-check");
                            result.Skipped++;
                            break;
                        }
                        _log.Info($"{prefix}delete{where}  \\{action.RelativePath}  (removed on the other side)");
                        if (!_dryRun && File.Exists(dst)) { WarnIfPermanent(pairTag, action.RelativePath, dst, back); RecycleBin.Send(dst); }
                        result.FilesRecycled++;
                        result.Forgotten.Add(action.RelativePath);
                        break;

                    case SyncActionKind.DeleteDirectory:
                        if (!_dryRun && Directory.Exists(dst) && Directory.EnumerateFileSystemEntries(dst).Any())
                        {
                            _log.Warn($"{pairTag} rmdir  \\{action.RelativePath}: not empty any more, kept");
                            break;
                        }
                        if (!_dryRun && Directory.Exists(src))
                        {
                            _log.Warn($"{pairTag} rmdir  \\{action.RelativePath}: the other side still has it — not removed, the next pass will re-check");
                            result.Skipped++;
                            break;
                        }
                        _log.Info($"{prefix}rmdir{where}  \\{action.RelativePath}  (removed on the other side)");
                        if (!_dryRun && Directory.Exists(dst)) { WarnIfPermanent(pairTag, action.RelativePath, dst, back); RecycleBin.Send(dst); }
                        result.DirectoriesRecycled++;
                        result.Forgotten.Add(action.RelativePath);
                        break;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                result.Errors++;
                _log.Error($"{pairTag} {action.Kind} \\{action.RelativePath}: {ex.GetType().Name}: {ex.Message}");
            }
        }
        return result;
    }

    private bool EnsureCloudTarget(string pairTag, ExecutionResult result)
    {
        if (_cloudTargetReady is { } ready) return ready;

        Directory.CreateDirectory(_targetRoot);
        _cloudTargetReady = _cloudPlatform.IsSyncRoot(_targetRoot);
        if (_cloudTargetReady.Value) return true;

        if (!_cloudTargetFailureReported)
        {
            _cloudTargetFailureReported = true;
            result.Errors++;
            _log.Error($"{pairTag} «free up space after copy» is enabled, but the target is not inside an active " +
                       $"Windows Files On-Demand sync root: {_targetRoot}. Target copies are paused to protect the disk.");
        }
        return false;
    }

    private bool EnsureFreeSpace(long incomingBytes, bool mayWaitForCloud, string pairTag, ExecutionResult result,
        Action checkpointBeforeWait, CancellationToken ct)
    {
        var required = checked(_minFreeSpaceBytes + incomingBytes);
        var waiting = false;
        var report = System.Diagnostics.Stopwatch.StartNew();

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var disk = _cloudPlatform.GetDiskSpace(_targetRoot);
            if (required > disk.TotalBytes)
                throw new IOException($"The next file ({FormatSize(incomingBytes)}) plus the configured free-space reserve " +
                                      $"({FormatSize(_minFreeSpaceBytes)}) cannot fit on this {FormatSize(disk.TotalBytes)} volume.");
            if (disk.AvailableBytes >= required)
            {
                if (waiting)
                    _log.Info($"{pairTag} cloud provider released enough space; copying continues (free {FormatSize(disk.AvailableBytes)})");
                return true;
            }

            if (!mayWaitForCloud)
            {
                _quotaBlocked = true;
                result.DiskQuotaStops++;
                checkpointBeforeWait();
                _log.Warn($"{pairTag} disk quota stopped further target copies: free {FormatSize(disk.AvailableBytes)}, " +
                          $"need {FormatSize(required)} before the next copy");
                return false;
            }

            if (!waiting)
            {
                waiting = true;
                result.CloudSpaceWaits++;
                checkpointBeforeWait();
                _log.Warn($"{pairTag} waiting for the cloud provider to upload files and release space: free " +
                          $"{FormatSize(disk.AvailableBytes)}, need {FormatSize(required)} before the next copy");
                report.Restart();
            }
            else if (report.Elapsed >= TimeSpan.FromMinutes(1))
            {
                _log.Warn($"{pairTag} still waiting for the cloud provider to release disk space");
                report.Restart();
            }

            if (CloudSpacePollInterval <= TimeSpan.Zero)
            {
                Thread.Yield();
            }
            else if (ct.WaitHandle.WaitOne(CloudSpacePollInterval))
            {
                ct.ThrowIfCancellationRequested();
            }
        }
    }

    /// <summary>Whether the side being READ can hold cloud placeholders at all.</summary>
    private bool MayBePlaceholder(bool readingFromTarget) => readingFromTarget ? _targetMayHavePlaceholders : _sourceMayHavePlaceholders;

    /// <summary>
    /// On a root without a Recycle Bin the removal cannot be undone. The customer chose to keep that
    /// behaviour (docs §4.5), so the log line is the only trace left — it carries the full path on purpose.
    /// </summary>
    private void WarnIfPermanent(string pairTag, string relativePath, string fullPath, bool onSourceSide)
    {
        if (onSourceSide ? _sourceHasRecycleBin : _targetHasRecycleBin) return;
        _log.Warn($"{pairTag} deleted PERMANENTLY (no Recycle Bin on this location): {fullPath}");
    }

    /// <summary>Copy via a temp name then rename, keep the source mtime, and read back the attributes we will compare against later.</summary>
    private static (long Size, DateTimeOffset Mtime) CopyPreservingTime(string src, string dst, DateTimeOffset? sourceMtime)
    {
        var dir = Path.GetDirectoryName(dst);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = dst + ".wfs-tmp";
        try
        {
            File.Copy(src, tmp, overwrite: true);
            // File.Copy keeps the mtime on Windows; re-apply from the listing (no extra network stat) to be sure.
            if (sourceMtime is { } mt) File.SetLastWriteTimeUtc(tmp, mt.UtcDateTime);
            File.Move(tmp, dst, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) { try { File.Delete(tmp); } catch { /* best effort */ } }
        }
        var info = new FileInfo(dst);
        return (info.Length, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };
}
