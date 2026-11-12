using System.Text;
using System.Text.RegularExpressions;
using WorkFlowSync.Core.Config;
using WorkFlowSync.Core.Model;
using WorkFlowSync.Core.Planning;
using WorkFlowSync.Core.Scanning;
using WorkFlowSync.Core.State;

namespace WorkFlowSync.Core.Execution;

public sealed record RepairProgress(string Stage, int Completed = 0, int Total = 0);

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
        public int ActionCount => Work.Count + Rows.Count(r => r.Action is "recycle-cloud-duplicate" or "recycle-empty-directory");
        public int Conflicts => Rows.Count(r => r.Action is "conflict" or "missing" or "untracked");
        public int SourceFileCount { get; internal set; }
        public int KeepFiles { get; internal set; }
        public long KeepBytes { get; internal set; }
        public int RenameFiles => Work.Count(w => w.State.Kind == EntryKind.File && w.Row.Action == "move");
        public long RenameBytes => Work.Where(w => w.State.Kind == EntryKind.File && w.Row.Action == "move")
            .Sum(w => w.State.CopiedSize ?? 0);
        public int RecycleFiles => Work.Count(w => w.State.Kind == EntryKind.File &&
            w.Row.Action is "recycle-duplicate" or "recycle-duplicate-state") +
            Rows.Count(r => r.Action == "recycle-cloud-duplicate");
        public long RecycleBytes => Work.Where(w => w.State.Kind == EntryKind.File &&
            w.Row.Action is "recycle-duplicate" or "recycle-duplicate-state")
            .Sum(w => w.State.CopiedSize ?? 0);

        public void Dispose()
        {
            Rows.Clear();
            Work.Clear();
        }
    }

    public static Plan Prepare(FolderPair pair, SyncConfig config, StateStore store,
        IProgress<RepairProgress>? progress = null)
    {
        if (pair.Mode != SyncMode.Mirror || !pair.ShortenLongPaths)
            throw new InvalidOperationException("Target repair requires a mirror pair with shortening enabled");
        if (pair.FreeUpSpaceAfterCopy && !CloudFiles.IsSyncRoot(pair.Target))
            throw new IOException("Waiting for OneDrive / Files On-Demand target");
        var state = store.Load(pair.Name);
        bool TombstoneDir(string rel) => state.TryGetValue(rel, out var e) &&
            e.Kind == EntryKind.Directory && e.Status == EntryStatus.Tombstone;
        progress?.Report(new RepairProgress("scan_source"));
        var source = new TreeScanner(config.ScanBufferSize, config.ScanParallelism, pair.Links,
            new ExcludeMatcher(pair.Exclude), TombstoneDir).Scan(pair.Source);
        progress?.Report(new RepairProgress("scan_target"));
        var target = new TreeScanner(config.ScanBufferSize, config.ScanParallelism, LinkMode.Skip,
            new ExcludeMatcher(pair.Exclude)).Scan(pair.Target);
        if (source.Warnings.Any(w => w.Contains("cannot list", StringComparison.OrdinalIgnoreCase)) ||
            target.Warnings.Any(w => w.Contains("cannot list", StringComparison.OrdinalIgnoreCase)))
            throw new IOException("Repair stopped because a directory could not be scanned completely");
        progress?.Report(new RepairProgress("build"));
        return Build(pair.Name, pair.Target, PathLimits.Effective(pair.Target, pair.MaxPathLength),
            source.Entries, target.Entries, state, progress);
    }

    public static Plan Build(string pair, string targetRoot, int limit,
        IReadOnlyDictionary<string, ScanEntry> source,
        IReadOnlyDictionary<string, ScanEntry> target,
        IReadOnlyDictionary<string, StateEntry> state,
        IProgress<RepairProgress>? progress = null)
    {
        var plan = new Plan
        {
            Pair = pair,
            TargetRoot = Path.GetFullPath(targetRoot),
            SourceFileCount = source.Values.Count(e => e.Kind == EntryKind.File)
        };
        var canonical = PathShortener.MapEntries(source, targetRoot, true, limit, null, null, out var mappedToSource);
        var sourceToCanonical = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var targetFilesBySize = target.Where(kvp => kvp.Value.Kind == EntryKind.File)
            .GroupBy(kvp => kvp.Value.Size).ToDictionary(g => g.Key, g => g.ToList());
        var sourceFilesBySize = source.Where(kvp => kvp.Value.Kind == EntryKind.File)
            .GroupBy(kvp => kvp.Value.Size).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var path in canonical.Keys)
            sourceToCanonical[mappedToSource.GetValueOrDefault(path) ?? path] = path;

        var tracked = new HashSet<string>(state.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var e in state.Values)
        {
            if (!string.IsNullOrEmpty(e.TargetPath)) tracked.Add(e.TargetPath);
        }
        foreach (var c in canonical.Keys)
        {
            tracked.Add(c);
        }

        var plannedDestinations = new Dictionary<string, StateEntry>(StringComparer.OrdinalIgnoreCase);
        var processed = 0;
        foreach (var entry in state.Values.OrderBy(e => e.Kind == EntryKind.Directory ? 1 : 0)
                     .ThenBy(e => e.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            if (++processed % 5000 == 0)
                progress?.Report(new RepairProgress("build", processed, state.Count));
            var sourcePath = entry.RemoteId ?? entry.RelativePath;
            var oldPath = entry.TargetPath ?? entry.RelativePath;

            if (entry.Status == EntryStatus.Tombstone)
            {
                if (!sourceToCanonical.TryGetValue(sourcePath, out _))
                    continue;
            }
            else if (entry.Status != EntryStatus.Active)
            {
                continue;
            }

            if (!sourceToCanonical.TryGetValue(sourcePath, out var newPath)) continue;

            if (oldPath.Equals(newPath, StringComparison.OrdinalIgnoreCase))
            {
                if (entry.Status == EntryStatus.Tombstone && target.TryGetValue(newPath, out var obsTombstone) && Matches(obsTombstone, entry))
                {
                    var reviveRow = new Row(oldPath, newPath, "adopt-existing", "revive-tombstone");
                    plan.Rows.Add(reviveRow);
                    plan.Work.Add((reviveRow, sourcePath, entry));
                    plannedDestinations[newPath] = entry;
                    tracked.Add(newPath);
                }
                continue;
            }

            target.TryGetValue(oldPath, out var observedOld);
            target.TryGetValue(newPath, out var observedNew);

            if (observedOld is null && observedNew is null && entry.Kind == EntryKind.File)
            {
                var oldDir = Path.GetDirectoryName(oldPath);
                var newDir = Path.GetDirectoryName(newPath);
                var oldName = Path.GetFileName(oldPath);
                var oldStem = Path.GetFileNameWithoutExtension(oldPath);

                var candidate = targetFilesBySize.GetValueOrDefault(entry.CopiedSize ?? -1)?.FirstOrDefault(kvp =>
                    Matches(kvp.Value, entry) &&
                    !tracked.Contains(kvp.Key) &&
                    (Path.GetFileName(kvp.Key).Equals(oldName, StringComparison.OrdinalIgnoreCase) ||
                     Path.GetFileNameWithoutExtension(kvp.Key).Equals(oldStem, StringComparison.OrdinalIgnoreCase) ||
                     kvp.Key.StartsWith(oldPath, StringComparison.OrdinalIgnoreCase) ||
                     (oldDir is not null && (string.Equals(Path.GetDirectoryName(kvp.Key), oldDir, StringComparison.OrdinalIgnoreCase) ||
                                             string.Equals(Path.GetDirectoryName(kvp.Key), newDir, StringComparison.OrdinalIgnoreCase))))).Key;
                if (candidate is not null)
                {
                    observedOld = target[candidate];
                    oldPath = candidate;
                }
            }

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
                action = observedOld is null ? (observedNew is not null || sameOwner ? "drop-duplicate-directory-state" : "map-directory") :
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
            if (Path.GetFileName(path).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;

            if (item.Kind == EntryKind.File)
            {
                var dupMatch = Regex.Match(path, @"^(.*) \(\d+\)(\.[^.\\]*)$");
                if (dupMatch.Success)
                {
                    var orig = dupMatch.Groups[1].Value + dupMatch.Groups[2].Value;
                    if (target.TryGetValue(orig, out var origItem) && origItem.Kind == EntryKind.File &&
                        origItem.Size == item.Size &&
                        Math.Abs((origItem.MtimeUtc - item.MtimeUtc).TotalSeconds) <= 2)
                    {
                        plan.Rows.Add(new Row(path, "", "recycle-cloud-duplicate", "cloud duplicate"));
                        continue;
                    }
                }

                if (Regex.IsMatch(path, "_#[0-9a-f]{4,8}(?:\\.|\\\\|$)"))
                {
                    var stem = Path.GetFileNameWithoutExtension(path);
                    var hashIdx = stem.IndexOf("_#", StringComparison.Ordinal);
                    var cleanStem = hashIdx > 0 ? stem[..hashIdx] : stem;

                    var dirName = Path.GetDirectoryName(path);
                    var matchedSource = sourceFilesBySize.GetValueOrDefault(item.Size)?.FirstOrDefault(s =>
                        (Path.GetFileName(s.Key).Contains(cleanStem, StringComparison.OrdinalIgnoreCase) ||
                         (dirName is not null && (string.Equals(Path.GetDirectoryName(s.Key), dirName, StringComparison.OrdinalIgnoreCase) ||
                                                  Path.GetDirectoryName(s.Key)?.Contains(Path.GetFileName(dirName) ?? "", StringComparison.OrdinalIgnoreCase) == true)))).Key;

                    if (matchedSource is not null && sourceToCanonical.TryGetValue(matchedSource, out var canonPath))
                    {
                        var fakeEntry = new StateEntry
                        {
                            PairName = pair,
                            RelativePath = matchedSource,
                            TargetPath = path,
                            RemoteId = matchedSource,
                            Kind = EntryKind.File,
                            Status = EntryStatus.Active,
                            FirstSeenUtc = item.MtimeUtc,
                            LastSeenUtc = item.MtimeUtc,
                            CopiedSize = item.Size,
                            CopiedMtimeUtc = item.MtimeUtc,
                        };

                        if (canonPath.Equals(path, StringComparison.OrdinalIgnoreCase))
                        {
                            var row = new Row(path, canonPath, "adopt-existing", "adopt-untracked");
                            plan.Rows.Add(row);
                            plan.Work.Add((row, matchedSource, fakeEntry));
                            tracked.Add(path);
                            continue;
                        }
                        else if (!target.ContainsKey(canonPath))
                        {
                            var row = new Row(path, canonPath, "move", "adopt-untracked");
                            plan.Rows.Add(row);
                            plan.Work.Add((row, matchedSource, fakeEntry));
                            tracked.Add(path);
                            tracked.Add(canonPath);
                            continue;
                        }
                    }
                }
            }
            else if (item.Kind == EntryKind.Directory)
            {
                var prefix = path + "\\";
                var hasTrackedChildren = target.Any(kvp => kvp.Value.Kind == EntryKind.File &&
                    kvp.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                    tracked.Contains(kvp.Key) &&
                    !Path.GetFileName(kvp.Key).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase));
                if (!hasTrackedChildren)
                {
                    plan.Rows.Add(new Row(path, "", "recycle-empty-directory", "empty directory"));
                    continue;
                }
            }

            if (Path.Combine(targetRoot, path).Length <= limit &&
                !Regex.IsMatch(path, "_#[0-9a-f]{4,8}(?:\\.|\\\\|$)", RegexOptions.IgnoreCase)) continue;
            plan.Rows.Add(new Row(path, "", "untracked", item.Kind.ToString()));
        }
        var touched = plan.Work.Where(w => w.State.Kind == EntryKind.File)
            .Select(w => w.Source).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var kept = source.Where(kvp => kvp.Value.Kind == EntryKind.File && !touched.Contains(kvp.Key) &&
            sourceToCanonical.TryGetValue(kvp.Key, out var canonicalPath) &&
            target.TryGetValue(canonicalPath, out var observed) && observed.Kind == EntryKind.File &&
            observed.Size == kvp.Value.Size).ToList();
        plan.KeepFiles = kept.Count;
        plan.KeepBytes = kept.Sum(kvp => kvp.Value.Size);
        progress?.Report(new RepairProgress("build", state.Count, state.Count));
        return plan;
    }

    public static void Execute(Plan plan, StateStore store,
        IProgress<RepairProgress>? progress = null, Action<Row>? completed = null)
    {
        if (!RecycleBin.IsAvailableFor(plan.TargetRoot))
            throw new InvalidOperationException("Target repair requires a local Recycle Bin");

        var total = plan.ActionCount;
        var done = 0;
        void Report(Row row)
        {
            completed?.Invoke(row);
            if (++done % 50 == 0 || done == total)
                progress?.Report(new RepairProgress("apply", done, total));
        }

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
                try
                {
                    if (recorded.Status == EntryStatus.Tombstone)
                        store.ReviveTombstone(plan.Pair, row.OldPath, row.NewPath, source);
                    else
                        store.RelocateTarget(plan.Pair, row.OldPath, row.NewPath, source);
                }
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
                else if (recorded.Status == EntryStatus.Tombstone)
                    store.ReviveTombstone(plan.Pair, row.OldPath, row.NewPath, source);
                else
                    store.RelocateTarget(plan.Pair, row.OldPath, row.NewPath, source);
            }
            else if (row.Action == "drop-stale-state")
                store.Delete(plan.Pair, new[] { row.OldPath });
            else if (row.Action == "adopt-existing")
            {
                if (!File.Exists(FsPath(newFull)) || !Matches(new FileInfo(FsPath(newFull)), recorded))
                    throw new IOException($"Repair destination disappeared after planning: {newFull}");
                if (recorded.Status == EntryStatus.Tombstone)
                    store.ReviveTombstone(plan.Pair, row.OldPath, row.NewPath, source);
                else
                    store.RelocateTarget(plan.Pair, row.OldPath, row.NewPath, source);
            }
            Report(row);
        }

        foreach (var (row, source, _) in plan.Work.Where(w => w.State.Kind == EntryKind.Directory)
                     .OrderBy(w => w.Row.OldPath.Length))
        {
            if (row.Action == "drop-duplicate-directory-state")
            {
                store.Delete(plan.Pair, new[] { row.OldPath });
                Report(row);
                continue;
            }
            var newFull = Path.Combine(plan.TargetRoot, row.NewPath);
            Directory.CreateDirectory(FsPath(newFull));
            store.RelocateTarget(plan.Pair, row.OldPath, row.NewPath, source);
            Report(row);
        }

        foreach (var row in plan.Work.Where(w => w.State.Kind == EntryKind.Directory)
                     .OrderByDescending(w => w.Row.OldPath.Length).Select(w => w.Row))
        {
            var oldFull = Path.Combine(plan.TargetRoot, row.OldPath);
            if (Directory.Exists(FsPath(oldFull)) && !Directory.EnumerateFileSystemEntries(FsPath(oldFull)).Any())
                RecycleLongPath(plan.TargetRoot, oldFull, directory: true);
        }

        foreach (var row in plan.Rows.Where(r => r.Action == "recycle-cloud-duplicate"))
        {
            var oldFull = Path.Combine(plan.TargetRoot, row.OldPath);
            if (File.Exists(FsPath(oldFull)))
            {
                var match = Regex.Match(row.OldPath, @"^(.*) \(\d+\)(\.[^.\\]*)$");
                var original = Path.Combine(plan.TargetRoot, match.Groups[1].Value + match.Groups[2].Value);
                var oldInfo = new FileInfo(FsPath(oldFull));
                var originalInfo = new FileInfo(FsPath(original));
                if (!match.Success || !originalInfo.Exists || oldInfo.Length != originalInfo.Length ||
                    Math.Abs((oldInfo.LastWriteTimeUtc - originalInfo.LastWriteTimeUtc).TotalSeconds) > 2)
                    throw new IOException($"Repair duplicate changed after planning: {oldFull}");
                RecycleLongPath(plan.TargetRoot, oldFull, directory: false);
            }
            Report(row);
        }

        foreach (var row in plan.Rows.Where(r => r.Action == "recycle-empty-directory")
                     .OrderByDescending(r => r.OldPath.Length))
        {
            var oldFull = Path.Combine(plan.TargetRoot, row.OldPath);
            if (Directory.Exists(FsPath(oldFull)))
            {
                var entries = Directory.EnumerateFileSystemEntries(FsPath(oldFull)).ToList();
                if (entries.All(e => Path.GetFileName(e).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)))
                {
                    foreach (var e in entries)
                        RecycleLongPath(plan.TargetRoot, e, directory: false);
                }
                if (!Directory.EnumerateFileSystemEntries(FsPath(oldFull)).Any())
                    RecycleLongPath(plan.TargetRoot, oldFull, directory: true);
            }
            Report(row);
        }

        if (plan.Conflicts == 0)
            store.ClearRepairRequired(plan.Pair);
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
