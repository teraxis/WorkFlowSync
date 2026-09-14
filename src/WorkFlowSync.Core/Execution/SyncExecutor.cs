using WorkFlowSync.Core.Logging;
using WorkFlowSync.Core.Model;
using WorkFlowSync.Core.Planning;

namespace WorkFlowSync.Core.Execution;

public sealed class ExecutionResult
{
    public int DirectoriesCreated { get; set; }
    public int FilesCopied { get; set; }
    public int FilesUpdated { get; set; }
    public int Errors { get; set; }
    public long BytesCopied { get; set; }

    /// <summary>State rows to persist: successful actions with copied_* filled in.</summary>
    public List<StateEntry> Completed { get; } = new();
}

/// <summary>
/// Applies a <see cref="SyncPlan"/> to the target. Source is only ever opened for reading.
/// Per-file failures are logged and skipped (the entry stays absent from state, so the next pass retries).
/// </summary>
public sealed class SyncExecutor
{
    private readonly string _sourceRoot;
    private readonly string _targetRoot;
    private readonly ISyncLog _log;
    private readonly bool _dryRun;

    public SyncExecutor(string sourceRoot, string targetRoot, ISyncLog log, bool dryRun)
    {
        _sourceRoot = Path.GetFullPath(sourceRoot).TrimEnd('\\', '/');
        _targetRoot = Path.GetFullPath(targetRoot).TrimEnd('\\', '/');
        _log = log;
        _dryRun = dryRun;
    }

    public ExecutionResult Execute(SyncPlan plan, string pairTag, CancellationToken ct = default)
    {
        var result = new ExecutionResult();
        var prefix = _dryRun ? $"{pairTag} [dry-run] " : $"{pairTag} ";

        foreach (var action in plan.Actions)
        {
            ct.ThrowIfCancellationRequested();
            var src = Path.Combine(_sourceRoot, action.RelativePath);
            var dst = Path.Combine(_targetRoot, action.RelativePath);
            try
            {
                switch (action.Kind)
                {
                    case SyncActionKind.CreateDirectory:
                        _log.Info($"{prefix}mkdir  \\{action.RelativePath}");
                        if (!_dryRun) Directory.CreateDirectory(dst);
                        result.DirectoriesCreated++;
                        result.Completed.Add(action.Proposed);
                        break;

                    case SyncActionKind.CopyFile:
                    case SyncActionKind.UpdateFile:
                        var verb = action.Kind == SyncActionKind.CopyFile ? "copy " : "update";
                        _log.Info($"{prefix}{verb}  \\{action.RelativePath}  {FormatSize(action.Proposed.SourceSize ?? 0)}");
                        if (_dryRun)
                        {
                            result.Completed.Add(action.Proposed);
                        }
                        else
                        {
                            var copied = CopyPreservingTime(src, dst, action.Proposed.SourceMtimeUtc);
                            result.Completed.Add(action.Proposed with { CopiedSize = copied.Size, CopiedMtimeUtc = copied.Mtime });
                        }
                        if (action.Kind == SyncActionKind.CopyFile) result.FilesCopied++; else result.FilesUpdated++;
                        result.BytesCopied += action.Proposed.SourceSize ?? 0;
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
