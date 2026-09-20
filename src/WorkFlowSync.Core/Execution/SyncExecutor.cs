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

    public SyncExecutor(string sourceRoot, string targetRoot, ISyncLog log, bool dryRun, CloudFileMode cloudFiles = CloudFileMode.Skip)
    {
        _sourceRoot = Path.GetFullPath(sourceRoot).TrimEnd('\\', '/');
        _targetRoot = Path.GetFullPath(targetRoot).TrimEnd('\\', '/');
        _log = log;
        _dryRun = dryRun;
        _cloudFiles = cloudFiles;
        _sourceHasRecycleBin = RecycleBin.IsAvailableFor(_sourceRoot);
        _targetHasRecycleBin = RecycleBin.IsAvailableFor(_targetRoot);
        _sourceMayHavePlaceholders = CloudFiles.Possible(_sourceRoot);
        _targetMayHavePlaceholders = CloudFiles.Possible(_targetRoot);
    }

    /// <summary>Rows that may wait unwritten before a checkpoint is due. Lowered by tests.</summary>
    public int CheckpointEvery { get; init; } = 2000;

    /// <summary>…or this long, so a slow copy of a few huge files is not left unrecorded either.</summary>
    public TimeSpan CheckpointInterval { get; init; } = TimeSpan.FromSeconds(30);

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

        foreach (var action in plan.Actions)
        {
            ct.ThrowIfCancellationRequested();
            if (checkpoint is not null && result.Completed.Count > 0 &&
                (result.Completed.Count >= CheckpointEvery || sinceCheckpoint.Elapsed >= CheckpointInterval))
            {
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

                        var verb = action.Kind == SyncActionKind.CopyFile ? "copy " : "update";
                        _log.Info($"{prefix}{verb}{where}  \\{action.RelativePath}  {FormatSize(action.Proposed.SourceSize ?? 0)}");
                        if (_dryRun)
                        {
                            result.Completed.Add(action.Proposed);
                        }
                        else
                        {
                            var copied = CopyPreservingTime(src, dst, action.Proposed.SourceMtimeUtc);
                            // Refresh the side we wrote: that is the copy later passes compare against.
                            result.Completed.Add(back
                                ? action.Proposed with { SourceSize = copied.Size, SourceMtimeUtc = copied.Mtime }
                                : action.Proposed with { CopiedSize = copied.Size, CopiedMtimeUtc = copied.Mtime });
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
