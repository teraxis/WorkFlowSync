using System.Text;
using System.Text.RegularExpressions;
using WorkFlowSync.Core.Config;
using WorkFlowSync.Core.Model;
using WorkFlowSync.Core.Planning;
using WorkFlowSync.Core.Scanning;
using WorkFlowSync.Core.State;

namespace WorkFlowSync.Core.Execution;

/// <summary>Plans same-root target moves from observed files and recorded source identities.</summary>
public static class TargetRepair
{
    public sealed record Row(string OldPath, string NewPath, string Action, string? Detail = null);

    public sealed class Plan : IDisposable
    {
        public required string Pair { get; init; }
        public required string TargetRoot { get; init; }
        public List<Row> Rows { get; } = new();
        internal List<(Row Row, string Source, StateEntry State)> Work { get; } = new();
        public int ActionCount => Work.Count;
        public int Conflicts => Rows.Count(r => r.Action is "conflict" or "missing" or "untracked");

        public void Dispose()
        {
            Rows.Clear();
            Work.Clear();
        }
    }

    public static Plan Prepare(FolderPair pair, SyncConfig config, StateStore store)
    {
        if (pair.Mode != SyncMode.Mirror || !pair.ShortenLongPaths)
            throw new InvalidOperationException("Target repair requires a mirror pair with shortening enabled");
        if (pair.FreeUpSpaceAfterCopy && !CloudFiles.IsSyncRoot(pair.Target))
            throw new IOException("Waiting for OneDrive / Files On-Demand target");
        var state = store.Load(pair.Name);
        bool TombstoneDir(string rel) => state.TryGetValue(rel, out var e) &&
            e.Kind == EntryKind.Directory && e.Status == EntryStatus.Tombstone;
        var source = new TreeScanner(config.ScanBufferSize, config.ScanParallelism, pair.Links,
            new ExcludeMatcher(pair.Exclude), TombstoneDir).Scan(pair.Source);
        var target = new TreeScanner(config.ScanBufferSize, config.ScanParallelism, LinkMode.Skip).Scan(pair.Target);
        if (source.Warnings.Any(w => w.Contains("cannot list", StringComparison.OrdinalIgnoreCase)) ||
            target.Warnings.Any(w => w.Contains("cannot list", StringComparison.OrdinalIgnoreCase)))
            throw new IOException("Repair stopped because a directory could not be scanned completely");
        return Build(pair.Name, pair.Target, PathLimits.Effective(pair.Target, pair.MaxPathLength),
            source.Entries, target.Entries, state);
    }

    public static Plan Build(string pair, string targetRoot, int limit,
        IReadOnlyDictionary<string, ScanEntry> source,
        IReadOnlyDictionary<string, ScanEntry> target,
        IReadOnlyDictionary<string, StateEntry> state)
    {
        var plan = new Plan { Pair = pair, TargetRoot = Path.GetFullPath(targetRoot) };
        var canonical = PathShortener.MapEntries(source, targetRoot, true, limit, null, null, out var mappedToSource);
        var sourceToCanonical = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in canonical.Keys)
            sourceToCanonical[mappedToSource.GetValueOrDefault(path) ?? path] = path;

        var tracked = new HashSet<string>(state.Keys, StringComparer.OrdinalIgnoreCase);
        var plannedDestinations = new Dictionary<string, StateEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in state.Values.OrderBy(e => e.Kind == EntryKind.Directory ? 1 : 0)
                     .ThenBy(e => e.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            if (entry.Status != EntryStatus.Active) continue; // tombstones and local edits are final
            var sourcePath = entry.RemoteId ?? entry.RelativePath;
            var oldPath = entry.TargetPath ?? entry.RelativePath;
            if (!sourceToCanonical.TryGetValue(sourcePath, out var newPath)) continue;
            if (oldPath.Equals(newPath, StringComparison.OrdinalIgnoreCase)) continue;

            target.TryGetValue(oldPath, out var observedOld);
            target.TryGetValue(newPath, out var observedNew);
            string action;
            string? detail = null;
            var destinationOwner = plannedDestinations.GetValueOrDefault(newPath);
            if (destinationOwner is null && state.TryGetValue(newPath, out var existingOwner))
                destinationOwner = existingOwner;
            var sameOwner = destinationOwner is not null &&
                string.Equals(destinationOwner.RemoteId ?? destinationOwner.RelativePath, sourcePath, StringComparison.OrdinalIgnoreCase);
            if (destinationOwner is not null && !sameOwner)
            {
                action = "conflict";
                detail = "Canonical destination belongs to another source";
            }
            else if (entry.Kind == EntryKind.Directory)
            {
                action = observedOld is null ? "missing" :
                    sameOwner ? "drop-duplicate-directory-state" : "map-directory";
            }
            else if (observedOld is not null && !Matches(observedOld, entry))
            {
                action = "conflict";
                detail = "Recorded copy differs from target";
            }
            else if (observedNew is not null && !Matches(observedNew, entry))
            {
                action = "conflict";
                detail = "Canonical destination contains different file";
            }
            else if (sameOwner)
            {
                action = observedOld is null ? "drop-stale-state" : "recycle-duplicate-state";
            }
            else if (observedOld is null)
            {
                action = observedNew is null ? "missing" : "adopt-existing";
            }
            else
            {
                action = observedNew is null ? "move" : "recycle-duplicate";
            }
            var row = new Row(oldPath, newPath, action, detail);
            plan.Rows.Add(row);
            if (action is "move" or "recycle-duplicate" or "adopt-existing" or "map-directory" or
                "recycle-duplicate-state" or "drop-stale-state" or "drop-duplicate-directory-state")
                plan.Work.Add((row, sourcePath, entry));
            if (action is "move" or "recycle-duplicate" or "adopt-existing" or "map-directory")
                plannedDestinations[newPath] = entry;
            if (observedNew is not null && action is ("recycle-duplicate" or "adopt-existing"))
                tracked.Add(newPath);
        }

        foreach (var (path, item) in target)
        {
            if (tracked.Contains(path)) continue;
            if (Path.Combine(targetRoot, path).Length <= limit &&
                !Regex.IsMatch(path, "_#[0-9a-f]{4,8}(?:\\.|\\\\|$)", RegexOptions.IgnoreCase)) continue;
            plan.Rows.Add(new Row(path, "", "untracked", item.Kind.ToString()));
        }
        return plan;
    }

    public static void Execute(Plan plan, StateStore store)
    {
        if (!RecycleBin.IsAvailableFor(plan.TargetRoot))
            throw new InvalidOperationException("Target repair requires a local Recycle Bin");

        foreach (var (row, source, recorded) in plan.Work.Where(w => w.State.Kind == EntryKind.File))
        {
            var oldFull = Path.Combine(plan.TargetRoot, row.OldPath);
            var newFull = Path.Combine(plan.TargetRoot, row.NewPath);
            if (row.Action == "move")
            {
                if (!Matches(new FileInfo(FsPath(oldFull)), recorded))
                    throw new IOException($"Repair source changed after planning: {oldFull}");
                Directory.CreateDirectory(FsPath(Path.GetDirectoryName(newFull)!));
                if (File.Exists(FsPath(newFull)))
                    throw new IOException($"Repair destination appeared after planning: {newFull}");
                File.Move(FsPath(oldFull), FsPath(newFull));
                try { store.RelocateTarget(plan.Pair, row.OldPath, row.NewPath, source); }
                catch
                {
                    File.Move(FsPath(newFull), FsPath(oldFull));
                    throw;
                }
            }
            else if (row.Action is "recycle-duplicate" or "recycle-duplicate-state")
            {
                // Recheck metadata immediately before removal; neither an unrelated file nor a changed
                // placeholder may be removed based on an earlier scan.
                var old = new FileInfo(FsPath(oldFull));
                var newer = new FileInfo(FsPath(newFull));
                if (!Matches(old, recorded) || !Matches(newer, recorded) || old.Length != newer.Length ||
                    Math.Abs((old.LastWriteTimeUtc - newer.LastWriteTimeUtc).TotalSeconds) > 2)
                    throw new IOException($"Repair duplicate changed after planning: {oldFull}");
                RecycleLongPath(plan.TargetRoot, oldFull, directory: false);
                if (row.Action == "recycle-duplicate-state")
                    store.Delete(plan.Pair, new[] { row.OldPath });
                else
                    store.RelocateTarget(plan.Pair, row.OldPath, row.NewPath, source);
            }
            else if (row.Action == "drop-stale-state")
                store.Delete(plan.Pair, new[] { row.OldPath });
            else if (row.Action == "adopt-existing")
            {
                if (!File.Exists(FsPath(newFull)) || !Matches(new FileInfo(FsPath(newFull)), recorded))
                    throw new IOException($"Repair destination disappeared after planning: {newFull}");
                store.RelocateTarget(plan.Pair, row.OldPath, row.NewPath, source);
            }
        }

        foreach (var (row, source, _) in plan.Work.Where(w => w.State.Kind == EntryKind.Directory)
                     .OrderBy(w => w.Row.OldPath.Length))
        {
            if (row.Action == "drop-duplicate-directory-state")
            {
                store.Delete(plan.Pair, new[] { row.OldPath });
                continue;
            }
            var newFull = Path.Combine(plan.TargetRoot, row.NewPath);
            Directory.CreateDirectory(FsPath(newFull));
            store.RelocateTarget(plan.Pair, row.OldPath, row.NewPath, source);
        }

        foreach (var row in plan.Work.Where(w => w.State.Kind == EntryKind.Directory)
                     .OrderByDescending(w => w.Row.OldPath.Length).Select(w => w.Row))
        {
            var oldFull = Path.Combine(plan.TargetRoot, row.OldPath);
            if (Directory.Exists(FsPath(oldFull)) && !Directory.EnumerateFileSystemEntries(FsPath(oldFull)).Any())
                RecycleLongPath(plan.TargetRoot, oldFull, directory: true);
        }
    }

    public static void WriteCsv(Plan plan, string reportPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
        using var writer = new StreamWriter(reportPath, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        writer.WriteLine("old_path,new_path,action,detail");
        foreach (var row in plan.Rows)
            writer.WriteLine(string.Join(",", Csv(row.OldPath), Csv(row.NewPath), Csv(row.Action), Csv(row.Detail ?? "")));
    }

    private static string Csv(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

    private static bool Matches(ScanEntry actual, StateEntry recorded) =>
        actual.Kind == EntryKind.File && actual.Size == recorded.CopiedSize &&
        recorded.CopiedMtimeUtc is { } mtime &&
        Math.Abs((actual.MtimeUtc - mtime).TotalSeconds) <= 2;

    private static bool Matches(FileInfo actual, StateEntry recorded) =>
        actual.Exists && actual.Length == recorded.CopiedSize &&
        recorded.CopiedMtimeUtc is { } mtime &&
        Math.Abs((actual.LastWriteTimeUtc - mtime.UtcDateTime).TotalSeconds) <= 2;

    private static string FsPath(string fullPath)
    {
        if (!OperatingSystem.IsWindows() || fullPath.StartsWith(@"\\?\", StringComparison.Ordinal)) return fullPath;
        return fullPath.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\UNC\" + fullPath[2..]
            : @"\\?\" + fullPath;
    }

    private static void RecycleLongPath(string targetRoot, string oldPath, bool directory)
    {
        // SHFileOperation is limited by MAX_PATH. Give it a short temporary name inside the same
        // sync root, then roll back the rename if sending the item to the bin fails.
        if (oldPath.Length < 259)
        {
            RecycleBin.Send(oldPath);
            return;
        }
        var temporary = Path.Combine(targetRoot, ".wfs-repair-" + Guid.NewGuid().ToString("N"));
        if (directory) Directory.Move(FsPath(oldPath), FsPath(temporary));
        else File.Move(FsPath(oldPath), FsPath(temporary));
        try { RecycleBin.Send(temporary); }
        catch
        {
            if (directory) Directory.Move(FsPath(temporary), FsPath(oldPath));
            else File.Move(FsPath(temporary), FsPath(oldPath));
            throw;
        }
    }
}
