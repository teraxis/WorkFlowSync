using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Enumeration;
using System.Threading.Channels;
using WorkFlowSync.Core.Config;
using WorkFlowSync.Core.Model;

namespace WorkFlowSync.Core.Scanning;

/// <summary>
/// Parallel directory-tree scanner tuned for SMB (docs/product/features/scanner-performance.md):
/// one large-buffer listing per directory, no per-file calls, own recursion with reparse-point
/// handling (docs/product/features/links-handling.md), excludes and a skip predicate for tombstone folders.
/// </summary>
public sealed class TreeScanner
{
    private const int MaxLinkDepth = 8;

    // Cloud-file placeholders (OneDrive Files On-Demand) are reparse points too; they are ordinary files/dirs
    // for the scanner. The flags themselves live in CloudFiles, which the executor also consults.

    private readonly EnumerationOptions _options;
    private readonly int _parallelism;
    private readonly LinkMode _links;
    private readonly ExcludeMatcher _excludes;
    private readonly Func<string, bool>? _skipDirectory;
    private readonly ThreadPriority _priority;

    /// <param name="skipDirectory">Relative directory path → true to neither record nor descend (tombstoned folders).</param>
    /// <param name="priority">Scheduling priority of the listing threads (docs F5: «Навантаження на процесор»).</param>
    public TreeScanner(int bufferSize, int parallelism, LinkMode links, ExcludeMatcher? excludes = null,
        Func<string, bool>? skipDirectory = null, ThreadPriority priority = ThreadPriority.Normal)
    {
        _options = new EnumerationOptions
        {
            BufferSize = Math.Max(4096, bufferSize),
            AttributesToSkip = 0,                 // default skips Hidden|System — FFS copies them, so do we
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,        // we recurse ourselves
            ReturnSpecialDirectories = false,
            MatchType = MatchType.Simple,
        };
        _parallelism = Math.Clamp(parallelism, 1, 64);
        _links = links;
        _excludes = excludes ?? ExcludeMatcher.Empty;
        _skipDirectory = skipDirectory;
        _priority = priority;
    }

    /// <param name="RealPath">Physical directory behind <paramref name="FullPath"/> (differs once a link was followed).</param>
    /// <param name="Chain">Physical directories where each followed link on the current path lived; used for cycle detection.</param>
    private readonly record struct WorkItem(string FullPath, string RelativePath, string RealPath, string[] Chain, bool ViaLink, int LinkDepth);

    private readonly record struct RawEntry(string Name, string FullPath, bool IsDirectory, long Length,
        DateTimeOffset Mtime, DateTimeOffset Ctime, FileAttributes Attributes);

    /// <param name="relativeRoot">
    /// Scan only this subtree, while still reporting paths relative to <paramref name="root"/> — what a
    /// partial pass needs, so its entries line up with the state database (docs/plan-etap5.md §4.2).
    /// </param>
    public ScanResult Scan(string root, CancellationToken ct = default, string? relativeRoot = null)
    {
        var sw = Stopwatch.StartNew();
        root = Path.GetFullPath(root).TrimEnd('\\', '/');
        var relative = relativeRoot?.Trim().Trim('\\', '/') ?? string.Empty;
        // Everything below walks from here; only the relative paths handed out keep the pair root as origin.
        var start = relative.Length == 0 ? root : Path.Combine(root, relative);
        if (!Directory.Exists(start)) throw new RootUnavailableException(start);

        var result = new ScanResult();
        var warnings = new ConcurrentBag<string>();
        var entries = new ConcurrentBag<List<ScanEntry>>();
        int dirs = 0, files = 0, links = 0;

        var channel = Channel.CreateUnbounded<WorkItem>(new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });
        var pending = 1;
        channel.Writer.TryWrite(new WorkItem(start, relative, start, Array.Empty<string>(), false, 0));

        // Probe the root synchronously so an offline share fails fast and loudly.
        try
        {
            using var probe = new FileSystemEnumerable<int>(start, (ref FileSystemEntry _) => 0, _options).GetEnumerator();
            probe.MoveNext();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new RootUnavailableException(start, ex);
        }

        // Dedicated threads, not the thread pool: only then does the priority below actually stick,
        // and a long pass cannot starve the pool that the UI and the loop use.
        var workers = new Thread[_parallelism];
        var failures = new ConcurrentBag<Exception>();
        for (var w = 0; w < workers.Length; w++)
        {
            workers[w] = new Thread(() =>
            {
              try
              {
                var local = new List<ScanEntry>(1024);
                entries.Add(local);
                while (WaitToRead(channel.Reader, ct))
                {
                    while (channel.Reader.TryRead(out var item))
                    {
                        try
                        {
                            ct.ThrowIfCancellationRequested();
                            ProcessDirectory(item, local, warnings, channel.Writer, ref pending, ref dirs, ref files, ref links);
                        }
                        catch (OperationCanceledException)
                        {
                            channel.Writer.TryComplete();
                            throw;
                        }
                        catch (Exception ex)
                        {
                            warnings.Add($"{item.RelativePath}: {ex.GetType().Name}: {ex.Message}");
                        }
                        finally
                        {
                            if (Interlocked.Decrement(ref pending) == 0)
                                channel.Writer.TryComplete();
                        }
                    }
                }
              }
              catch (OperationCanceledException) { /* Scan() rethrows via the token below */ }
              catch (Exception ex) { failures.Add(ex); }
            })
            {
                IsBackground = true,
                Name = "wfs-scan",
            };
            CpuBudget.TrySetPriority(workers[w], _priority);
            workers[w].Start();
        }
        foreach (var t in workers) t.Join();
        ct.ThrowIfCancellationRequested();
        if (!failures.IsEmpty) throw new AggregateException(failures);

        foreach (var list in entries)
            foreach (var e in list)
                result.Entries[e.RelativePath] = e;

        // A scan reports children, never the directory it started from. For a full pass that is right — the pair
        // root has no state row. A scoped scan does start from a folder the state knows about, so without this
        // the planner would see that folder in the state, miss it in both scans, and conclude it had vanished.
        if (relative.Length > 0 && !result.Entries.ContainsKey(relative))
        {
            var info = new DirectoryInfo(start);   // one call per pass, not per entry
            result.Entries[relative] = new ScanEntry
            {
                RelativePath = relative,
                Kind = EntryKind.Directory,
                MtimeUtc = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                CtimeUtc = new DateTimeOffset(info.CreationTimeUtc, TimeSpan.Zero),
            };
            dirs++;
        }

        result.Warnings.AddRange(warnings);
        result.DirectoryCount = dirs;
        result.FileCount = files;
        result.LinkCount = links;
        result.Elapsed = sw.Elapsed;
        return result;
    }

    /// <summary>Blocking wait for the next work item; returns false once the channel is complete.</summary>
    private static bool WaitToRead(ChannelReader<WorkItem> reader, CancellationToken ct)
    {
        try { return reader.WaitToReadAsync(ct).AsTask().GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { return false; }
    }

    private void ProcessDirectory(WorkItem item, List<ScanEntry> local, ConcurrentBag<string> warnings, ChannelWriter<WorkItem> writer, ref int pending, ref int dirs, ref int files, ref int links)
    {
        var raw = new List<RawEntry>();
        try
        {
            var enumerable = new FileSystemEnumerable<RawEntry>(item.FullPath,
                (ref FileSystemEntry e) => new RawEntry(e.FileName.ToString(), e.ToFullPath(), e.IsDirectory,
                    e.IsDirectory ? 0 : e.Length, e.LastWriteTimeUtc, e.CreationTimeUtc, e.Attributes),
                _options);
            foreach (var r in enumerable) raw.Add(r);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"{(item.RelativePath.Length == 0 ? "\\" : item.RelativePath)}: cannot list: {ex.Message}");
            return;
        }

        foreach (var r in raw)
        {
            var rel = item.RelativePath.Length == 0 ? r.Name : item.RelativePath + "\\" + r.Name;
            if (IsReservedName(rel, r.Name)) continue;
            if (_excludes.IsExcluded(rel)) continue;

            if (!r.IsDirectory)
            {
                local.Add(new ScanEntry { RelativePath = rel, Kind = EntryKind.File, Size = r.Length, MtimeUtc = r.Mtime, CtimeUtc = r.Ctime, ViaLink = item.ViaLink });
                Interlocked.Increment(ref files);
                continue;
            }

            if (_skipDirectory?.Invoke(rel) == true) continue;

            var viaLink = item.ViaLink;
            var depth = item.LinkDepth;
            var realPath = item.RealPath + "\\" + r.Name;
            var chain = item.Chain;

            if (IsLinkCandidate(r.Attributes))
            {
                var target = TryResolveLink(r.FullPath);
                if (target is not null)
                {
                    switch (_links)
                    {
                        case LinkMode.Skip:
                            continue;
                        case LinkMode.Recreate:
                            warnings.Add($"{rel}: link recreate mode is not implemented yet; skipped");
                            continue;
                        case LinkMode.Follow:
                            if (depth >= MaxLinkDepth) { warnings.Add($"{rel}: link nesting deeper than {MaxLinkDepth}; skipped"); continue; }
                            if (IsCycle(target, item.RealPath, item.Chain)) { warnings.Add($"{rel}: link points at an ancestor ({target}); cycle skipped"); continue; }
                            if (!Directory.Exists(target)) { warnings.Add($"{rel}: link target unavailable ({target}); skipped"); continue; }
                            // Remember where this link lived so links inside the target cannot lead back here.
                            chain = item.Chain.Append(item.RealPath).ToArray();
                            realPath = target;
                            viaLink = true;
                            depth++;
                            Interlocked.Increment(ref links);
                            break;
                    }
                }
            }

            local.Add(new ScanEntry { RelativePath = rel, Kind = EntryKind.Directory, MtimeUtc = r.Mtime, CtimeUtc = r.Ctime, ViaLink = viaLink });
            Interlocked.Increment(ref dirs);
            Interlocked.Increment(ref pending);
            // Enumerate through the logical path so children keep logical relative paths.
            writer.TryWrite(new WorkItem(r.FullPath, rel, realPath, chain, viaLink, depth));
        }
    }

    private static bool IsReservedName(string relPath, string name)
    {
        if (name.Equals(".wfsversions", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.EndsWith(".wfs-tmp", StringComparison.OrdinalIgnoreCase)) return true;
        if (relPath.Equals(".wfsversions", StringComparison.OrdinalIgnoreCase) ||
            relPath.StartsWith(".wfsversions\\", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>A link target that contains (or is) the current physical directory, or any directory a link on this
    /// path was followed from, would recurse forever. Sibling links (two names for one folder) are fine and mirrored twice.</summary>
    private static bool IsCycle(string target, string currentReal, string[] chain)
    {
        if (IsSameOrAncestor(target, currentReal)) return true;
        foreach (var c in chain) if (IsSameOrAncestor(target, c)) return true;
        return false;
    }

    private static bool IsSameOrAncestor(string ancestor, string path) =>
        path.Equals(ancestor, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(ancestor + "\\", StringComparison.OrdinalIgnoreCase);

    private static bool IsLinkCandidate(FileAttributes a) =>
        a.HasFlag(FileAttributes.ReparsePoint) && !CloudFiles.IsPlaceholder(a);

    /// <summary>Final target of a symlink/junction, or null when the reparse point is not a link.</summary>
    private static string? TryResolveLink(string fullPath)
    {
        try
        {
            var target = new DirectoryInfo(fullPath).ResolveLinkTarget(returnFinalTarget: true);
            return target is null ? null : Path.GetFullPath(target.FullName).TrimEnd('\\', '/');
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
