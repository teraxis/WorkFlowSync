using WorkFlowSync.Core.Model;
using WorkFlowSync.Core.Scanning;

namespace WorkFlowSync.Core.Planning;

/// <summary>
/// Pure decision logic — no file-system access. Implements the decision table in
/// docs/product/features/mirror-rules.md for every path seen in the source, the target or the state.
/// </summary>
public sealed class SyncPlanner
{
    /// <summary>Tolerance for mtime comparison (FAT/SMB rounding, DST shifts) — same idea as FFS TimeAndSize.</summary>
    public static readonly TimeSpan MtimeTolerance = TimeSpan.FromSeconds(2);

    private readonly string _pair;
    private readonly DateTimeOffset _now;
    private readonly bool _backfillFirstSeen;
    private readonly TimeSpan? _maxAge;
    private readonly TimeSpan? _autoClean;
    private readonly int? _keepLatestFiles;
    private readonly bool _cleanExistingFiles;
    private readonly DateTimeOffset? _cleanupFromUtc;
    private readonly PlanScope? _scope;
    private readonly WorkFlowSync.Core.Config.GoogleDocExportMode _googleDocExport;
    private readonly bool _convertGoogleRecordings;
    private readonly bool _freeUpSourceSpaceAfterCopy;
    private readonly string? _targetRoot;
    private readonly bool _shortenLongPaths;
    private readonly int _maxPathLength;

    /// <param name="backfillFirstSeen">First pass: use min(ctime, mtime) of the source item as first_seen (docs F3).</param>
    /// <param name="maxAge">
    /// Intake window: only files that appeared within it are copied and kept up to date; null = copy everything.
    /// Purely a filter — it never removes anything from the target, and the source is read-only either way (docs F3).
    /// </param>
    /// <param name="autoClean">Move our own copies older than this window to the Recycle Bin; null = keep forever (docs F3).</param>
    /// <param name="keepLatestFiles">Configured cleanup: keep at most this number of newest files in target; null = unlimited.</param>
    /// <param name="cleanExistingFiles">Whether cleanup applies to files that existed before cleanup was enabled.</param>
    /// <param name="cleanupFromUtc">Timestamp when cleanup was enabled with cleanExistingFiles = false.</param>
    /// <param name="scope">
    /// Limits the pass to one directory. A scoped pass only adds and updates: every destructive decision is
    /// inferred from absence, and absence inside a slice of the tree proves nothing (docs/plan-etap5.md §4.2).
    /// </param>
    /// <param name="googleDocExport">
    /// Export mode for native Google web documents (.gdoc, .gsheet, etc.): Office, Pdf, or Skip.
    /// </param>
    /// <param name="convertGoogleRecordings">
    /// Whether to convert native Google recordings (.gvid) to MP4.
    /// </param>
    public SyncPlanner(string pair, DateTimeOffset now, bool backfillFirstSeen, TimeSpan? maxAge = null,
        TimeSpan? autoClean = null, int? keepLatestFiles = null, bool cleanExistingFiles = true,
        DateTimeOffset? cleanupFromUtc = null, PlanScope? scope = null,
        WorkFlowSync.Core.Config.GoogleDocExportMode googleDocExport = WorkFlowSync.Core.Config.GoogleDocExportMode.Office,
        bool convertGoogleRecordings = true,
        bool freeUpSourceSpaceAfterCopy = false,
        string? targetRoot = null,
        bool shortenLongPaths = false,
        int maxPathLength = 240)
    {
        _pair = pair;
        _now = now;
        _backfillFirstSeen = backfillFirstSeen;
        _maxAge = maxAge;
        _autoClean = autoClean;
        _keepLatestFiles = keepLatestFiles;
        _cleanExistingFiles = cleanExistingFiles;
        _cleanupFromUtc = cleanupFromUtc;
        _scope = scope;
        _googleDocExport = googleDocExport;
        _convertGoogleRecordings = convertGoogleRecordings;
        _freeUpSourceSpaceAfterCopy = freeUpSourceSpaceAfterCopy;
        _targetRoot = targetRoot;
        _shortenLongPaths = shortenLongPaths;
        _maxPathLength = maxPathLength;
    }

    /// <summary>True while planning only part of the tree; destructive rules are off.</summary>
    private bool Scoped => _scope is not null;

    public SyncPlan Plan(IReadOnlyDictionary<string, ScanEntry> source, IReadOnlyDictionary<string, ScanEntry> target,
        IReadOnlyDictionary<string, StateEntry> state)
    {
        var plan = new SyncPlan();
        var mappedSource = GoogleDrive.MapGoogleDocsForExport(source, _googleDocExport, _convertGoogleRecordings, out var targetToSourceMap);
        if (_shortenLongPaths && !string.IsNullOrEmpty(_targetRoot))
        {
            mappedSource = PathShortener.MapEntries(mappedSource, _targetRoot, _shortenLongPaths, _maxPathLength, state, targetToSourceMap, out targetToSourceMap);
        }
        var tombstoneDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in state.Values)
            if (e.Kind == EntryKind.Directory && e.Status == EntryStatus.Tombstone)
                tombstoneDirs.Add(e.RelativePath);

        // Parents before children so a directory decision (tombstone cascade) precedes its content.
        var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        all.UnionWith(mappedSource.Keys);
        all.UnionWith(target.Keys);
        all.UnionWith(state.Keys);
        // Belt and braces: callers already hand a scoped pass nothing but its own subtree, but a stray path
        // slipping through must never be acted on — that is the whole safety property of a partial pass.
        if (_scope is { } scope) all.RemoveWhere(p => !scope.Contains(p));
        var ordered = all.OrderBy(Depth).ThenBy(p => p, StringComparer.OrdinalIgnoreCase);

        var expiredFiles = new List<StateEntry>();

        foreach (var path in ordered)
        {
            mappedSource.TryGetValue(path, out var s);
            target.TryGetValue(path, out var l);
            state.TryGetValue(path, out var db);
            targetToSourceMap.TryGetValue(path, out var sourceRel);

            if (GoogleDrive.IsGoogleDoc(path))
            {
                if (db is not null && !Scoped) plan.Forget.Add(path);
                plan.Warnings.Add($"{path}: native Google document without binary file content; skipped");
                continue;
            }

            if (UnderTombstone(path, tombstoneDirs))
            {
                // Rule 3 cascade: anything below a locally deleted folder is dead, whatever the source has.
                if (db is not null && db.Status != EntryStatus.Tombstone)
                    Tombstone(plan, db, tombstoneDirs, "parent folder tombstoned");
                continue;
            }

            if (db is null)
            {
                if (s is null) { plan.Stats.IgnoredLocalOnly++; continue; }   // local-only item: not ours
                if (l is null) { AddNew(plan, s, sourceRel); continue; }
                Adopt(plan, s, l, sourceRel);
                continue;
            }

            switch (db.Status)
            {
                case EntryStatus.Tombstone:
                    if (db.Kind == EntryKind.Directory) tombstoneDirs.Add(path);
                    break;

                case EntryStatus.LocalModified:
                    if (l is null) Tombstone(plan, db, tombstoneDirs, "local copy removed");
                    break;

                case EntryStatus.TooOld:
                    // Held back by the intake window, with the date we first saw it. Re-checked every pass, so
                    // widening the window (or switching it off) lets the file in on the very next one.
                    if (s is null) { if (!Scoped) plan.Forget.Add(path); break; }
                    if (OutsideIntakeWindow(db.FirstSeenUtc)) { plan.Stats.TooOld++; break; }
                    if (l is null)
                    {
                        var admitted = db with
                        {
                            Status = EntryStatus.Active, StatusChangedUtc = _now, LastSeenUtc = _now,
                            SourceSize = s.Size, SourceMtimeUtc = s.MtimeUtc, ViaLink = s.ViaLink,
                            RemoteId = sourceRel ?? db.RemoteId,
                        };
                        plan.Actions.Add(new SyncAction(SyncActionKind.CopyFile, path, admitted, SyncSide.Target, sourceRel));
                        plan.Stats.New++;
                        plan.Stats.BytesToCopy += s.Size;
                        break;
                    }
                    // A file of the same name turned up locally while we were holding back: it is not ours
                    // to overwrite, so adopt it the way any other unknown local copy is adopted.
                    Adopt(plan, s, l, sourceRel);
                    break;

                case EntryStatus.Active:
                    if (l is null)
                    {
                        // Source present or not: the user removed our copy → never bring it back.
                        Tombstone(plan, db, tombstoneDirs, s is null ? "gone on both sides" : "removed locally");
                        break;
                    }
                    if (s is null)
                    {
                        // rule 1: deleted in source → keep (unless auto-clean says otherwise)
                        if (db.Kind == EntryKind.File && LocalIntact(l, db) && IsExpired(db)) expiredFiles.Add(db);
                        else plan.Stats.Unchanged++;
                        break;
                    }
                    if (s.Kind != db.Kind || l.Kind != db.Kind)
                    {
                        plan.Warnings.Add($"{path}: kind changed (state={db.Kind}, source={s.Kind}, target={l.Kind}); skipped");
                        break;
                    }
                    if (db.Kind == EntryKind.Directory) { plan.Stats.Unchanged++; break; }

                    if (!LocalIntact(l, db))
                    {
                        plan.StateUpdates.Add(db with { Status = EntryStatus.LocalModified, StatusChangedUtc = _now, LastSeenUtc = _now });
                        plan.Stats.LocalModified++;
                        break;
                    }
                    if (IsExpired(db))
                    {
                        // Auto-clean beats update: an expired file is recycled even if the source changed it.
                        expiredFiles.Add(db);
                        break;
                    }
                    if (!Same(s, db.SourceSize, db.SourceMtimeUtc))
                    {
                        // Outside the intake window the pair stops following this file: the copy already here
                        // stays exactly as it is (auto-clean, not the filter, is what removes things).
                        if (OutsideIntakeWindow(db.FirstSeenUtc)) { plan.Stats.TooOld++; break; }
                        var proposed = db with { SourceSize = s.Size, SourceMtimeUtc = s.MtimeUtc, LastSeenUtc = _now, ViaLink = s.ViaLink, RemoteId = sourceRel ?? db.RemoteId };
                        plan.Actions.Add(new SyncAction(SyncActionKind.UpdateFile, path, proposed, SyncSide.Target, sourceRel));
                        plan.Stats.Updated++;
                        plan.Stats.BytesToCopy += s.Size;
                        break;
                    }
                    plan.Stats.Unchanged++;
                    if (_freeUpSourceSpaceAfterCopy && db.Kind == EntryKind.File && ShouldFreeUpSource(s))
                    {
                        plan.Actions.Add(new SyncAction(SyncActionKind.FreeUpSourceFile, path, db, SyncSide.Target, sourceRel));
                        plan.Stats.SourceFreed++;
                    }
                    break;
            }
        }

        ApplyKeepLatestFiles(plan, expiredFiles, target, state);
        PruneEmptyNewDirectories(plan, target);
        ApplyRetention(plan, expiredFiles, target, state);
        return plan;
    }

    /// <summary>
    /// A folder whose whole content the intake window left behind would otherwise arrive as an empty folder.
    /// Drops such <see cref="SyncActionKind.CreateDirectory"/> actions, deepest first so a chain of empty
    /// parents collapses in one go. Only folders this pass invented are candidates — one that already exists
    /// locally is never touched here.
    /// </summary>
    private void PruneEmptyNewDirectories(SyncPlan plan, IReadOnlyDictionary<string, ScanEntry> target)
    {
        if (_maxAge is null && _keepLatestFiles is null) return;

        var dirs = plan.Actions
            .Where(a => a.Kind == SyncActionKind.CreateDirectory)
            .OrderByDescending(a => Depth(a.RelativePath))
            .ToList();
        foreach (var dir in dirs)
        {
            var prefix = dir.RelativePath + "\\";
            var willHold = plan.Actions.Any(a => a != dir && a.RelativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                           || target.Keys.Any(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            if (willHold) continue;
            plan.Actions.Remove(dir);
            plan.Stats.New--;
        }
    }

    /// <summary>
    /// Expired files → Recycle Bin + tombstone. Directories never expire on their own (a new file in an old folder must
    /// still arrive); a directory left with nothing but recycled/absent children is recycled too and its row forgotten,
    /// so it can come back with the next new file. Deepest paths first so the executor empties folders bottom-up.
    /// </summary>
    private void ApplyRetention(SyncPlan plan, List<StateEntry> expiredFiles, IReadOnlyDictionary<string, ScanEntry> target,
        IReadOnlyDictionary<string, StateEntry> state)
    {
        if (expiredFiles.Count == 0) return;

        var removed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in expiredFiles)
        {
            plan.Actions.Add(new SyncAction(SyncActionKind.RecycleFile, f.RelativePath, f with { Status = EntryStatus.Tombstone, StatusChangedUtc = _now }));
            removed.Add(f.RelativePath);
            plan.Stats.Expired++;
        }

        // Remaining local items after the recycle: anything in the target not being removed.
        var remaining = target.Keys.Where(k => !removed.Contains(k)).ToList();
        var candidates = state.Values
            .Where(e => e.Kind == EntryKind.Directory && e.Status == EntryStatus.Active && target.ContainsKey(e.RelativePath))
            .OrderByDescending(e => Depth(e.RelativePath)).ThenByDescending(e => e.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var dir in candidates)
        {
            var prefix = dir.RelativePath + "\\";
            var hasChildren = remaining.Any(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            if (hasChildren) continue;
            // Only directories that actually lost something to retention (or are below such a directory) are touched.
            var lostSomething = removed.Any(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            if (!lostSomething) continue;
            plan.Actions.Add(new SyncAction(SyncActionKind.RecycleEmptyDirectory, dir.RelativePath, dir));
            removed.Add(dir.RelativePath);
            remaining.Remove(dir.RelativePath);
            plan.Stats.EmptyDirs++;
        }
    }

    /// <summary>
    /// Configured cleanup: limit the total number of files in the target folder by keeping only the newest N files.
    /// Files are ranked by their synchronization time (db.CopiedAtUtc ?? db.FirstSeenUtc; newly planned copies are treated as now).
    /// Oldest surplus files are moved to the Recycle Bin and tombstoned (or dropped before copy). Mirror only.
    /// </summary>
    private void ApplyKeepLatestFiles(
        SyncPlan plan,
        List<StateEntry> expiredFiles,
        IReadOnlyDictionary<string, ScanEntry> target,
        IReadOnlyDictionary<string, StateEntry> state)
    {
        if (Scoped || _keepLatestFiles is null || _keepLatestFiles.Value < 1) return;
        var maxCount = _keepLatestFiles.Value;

        var alreadyExpired = new HashSet<string>(expiredFiles.Select(e => e.RelativePath), StringComparer.OrdinalIgnoreCase);

        var items = new List<(string RelativePath, DateTimeOffset SyncTime, StateEntry Entry, bool IsNew, SyncAction? Action)>();

        // 1. Existing active files in target:
        foreach (var (relPath, scan) in target)
        {
            if (scan.Kind != EntryKind.File) continue;
            if (alreadyExpired.Contains(relPath)) continue;
            if (!state.TryGetValue(relPath, out var db) || db.Kind != EntryKind.File || db.Status != EntryStatus.Active) continue;
            if (!LocalIntact(scan, db)) continue; // Never prune locally modified files

            var updateAction = plan.Actions.FirstOrDefault(a => a.Kind == SyncActionKind.UpdateFile && a.RelativePath.Equals(relPath, StringComparison.OrdinalIgnoreCase));
            var syncTime = updateAction is not null ? _now : SyncTime(db);
            if (!_cleanExistingFiles && syncTime < (_cleanupFromUtc ?? _now))
            {
                // Pre-existing file: protected from count-based cleanup
                continue;
            }
            items.Add((relPath, syncTime, db, false, updateAction));
        }

        // 2. Newly planned files to copy to target:
        foreach (var action in plan.Actions.Where(a => a.Kind == SyncActionKind.CopyFile && a.Side == SyncSide.Target).ToList())
        {
            items.Add((action.RelativePath, _now, action.Proposed, true, action));
        }

        if (items.Count <= maxCount) return;

        // Order descending: newest files first, then alphabetical tie-breaker.
        // The top maxCount files are preserved; items beyond index maxCount are pruned.
        var ordered = items
            .OrderByDescending(x => x.SyncTime)
            .ThenByDescending(x => x.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var toPrune = ordered.Skip(maxCount).ToList();
        foreach (var item in toPrune)
        {
            if (item.IsNew && item.Action is { } copyAction)
            {
                plan.Actions.Remove(copyAction);
                plan.StateUpdates.RemoveAll(e => e.RelativePath.Equals(item.RelativePath, StringComparison.OrdinalIgnoreCase));
                plan.StateUpdates.Add(copyAction.Proposed with { Status = EntryStatus.TooOld, StatusChangedUtc = _now });
                plan.Stats.New--;
                plan.Stats.BytesToCopy -= copyAction.Proposed.SourceSize ?? 0;
                plan.Stats.TooOld++;
            }
            else
            {
                if (item.Action is { } updateAction)
                {
                    plan.Actions.Remove(updateAction);
                    plan.Stats.Updated--;
                    plan.Stats.BytesToCopy -= updateAction.Proposed.SourceSize ?? 0;
                }
                else
                {
                    plan.Stats.Unchanged--;
                }
                expiredFiles.Add(item.Entry);
                alreadyExpired.Add(item.RelativePath);
            }
        }
    }

    /// <summary>
    /// Synchronization date: when the file was copied or adopted by WorkFlowSync.
    /// Independent of source creation (ctime) or modification (mtime) dates.
    /// </summary>
    private static DateTimeOffset SyncTime(StateEntry db) =>
        db.CopiedAtUtc ?? db.StatusChangedUtc ?? db.FirstSeenUtc;

    /// <summary>
    /// Auto-clean deletes files by synchronization age. It is a whole-pair decision — an empty folder is only empty once the
    /// whole pair has been looked at — so a scoped pass never expires anything (docs/plan-etap5.md §4.2).
    /// </summary>
    private bool IsExpired(StateEntry db)
    {
        if (Scoped || _autoClean is not { } r || db.Kind != EntryKind.File) return false;
        if (!_cleanExistingFiles && db.FirstSeenUtc < (_cleanupFromUtc ?? _now)) return false;
        return db.FirstSeenUtc < _now - r;
    }

    /// <summary>
    /// The intake filter: this item appeared too long ago to be worth copying. Unlike auto-clean this is a
    /// per-item decision that needs nothing but the item itself, so a scoped pass applies it too.
    /// </summary>
    private bool OutsideIntakeWindow(DateTimeOffset firstSeen) => _maxAge is { } m && firstSeen < _now - m;

    private void AddNew(SyncPlan plan, ScanEntry s, string? sourceRelativePath = null)
    {
        var entry = NewEntry(s);
        if (sourceRelativePath is not null)
            entry = entry with { RemoteId = sourceRelativePath };
        // The intake window is decided here, before a single byte moves: a file that appeared outside it is
        // left in the source and never copied. Directories are not judged by age — a new report dropped into
        // a folder from 2019 must still arrive — so an empty one is pruned afterwards instead.
        if (s.Kind == EntryKind.File && OutsideIntakeWindow(entry.FirstSeenUtc))
        {
            // Remember the date, not the file. Skipping silently would let the next pass meet the same file
            // with no record of it, call it new, and copy exactly what this one refused.
            plan.StateUpdates.Add(entry with { Status = EntryStatus.TooOld, StatusChangedUtc = _now });
            plan.Stats.TooOld++;
            return;
        }
        plan.Actions.Add(new SyncAction(s.Kind == EntryKind.Directory ? SyncActionKind.CreateDirectory : SyncActionKind.CopyFile, s.RelativePath, entry, SyncSide.Target, sourceRelativePath));
        plan.Stats.New++;
        plan.Stats.BytesToCopy += s.Size;
    }

    /// <summary>Present on both sides but unknown to us (e.g. copied earlier by FreeFileSync): record it, never overwrite it.</summary>
    private void Adopt(SyncPlan plan, ScanEntry s, ScanEntry l, string? sourceRelativePath = null)
    {
        var entry = NewEntry(s) with
        {
            CopiedSize = l.Kind == EntryKind.File ? l.Size : null,
            CopiedMtimeUtc = l.Kind == EntryKind.File ? l.MtimeUtc : null,
            CopiedAtUtc = _now,
            RemoteId = sourceRelativePath,
        };

        // Adopting means "ours from now on, keep it in sync" — which is exactly what the intake window says
        // NOT to do for an old file. This is the delete-the-pair-and-add-it-again case: the target still holds
        // the earlier mirror, so every old file arrives here rather than through AddNew, and adopting them all
        // made the window look broken. The file is left on disk untouched; we only record that we saw it.
        if (s.Kind == EntryKind.File && OutsideIntakeWindow(entry.FirstSeenUtc))
        {
            plan.StateUpdates.Add(entry with { Status = EntryStatus.TooOld, StatusChangedUtc = _now });
            plan.Stats.TooOld++;
            return;
        }

        if (s.Kind == EntryKind.File && l.Kind == EntryKind.File && !Same(s, l.Size, l.MtimeUtc))
        {
            entry = entry with { Status = EntryStatus.LocalModified, StatusChangedUtc = _now };
            plan.Stats.LocalModified++;
        }
        else
        {
            plan.Stats.Adopted++;
            if (_freeUpSourceSpaceAfterCopy && s.Kind == EntryKind.File && ShouldFreeUpSource(s))
            {
                plan.Actions.Add(new SyncAction(SyncActionKind.FreeUpSourceFile, s.RelativePath, entry, SyncSide.Target));
                plan.Stats.SourceFreed++;
            }
        }
        plan.StateUpdates.Add(entry);
    }

    /// <summary>Checks whether a source file is still taking up local disk space and can be requested online-only.</summary>
    public static bool ShouldFreeUpSource(ScanEntry s)
    {
        if (s.Kind != EntryKind.File) return false;
        if ((s.Attributes & CloudFiles.Pinned) != 0) return false;
        if (CloudFiles.IsPlaceholder(s.Attributes) || (s.Attributes & CloudFiles.Unpinned) != 0) return false;
        return true;
    }

    private void Tombstone(SyncPlan plan, StateEntry db, HashSet<string> tombstoneDirs, string reason)
    {
        // A scoped pass never marks anything as gone: it is looking at one folder, not the pair.
        // The next full pass reaches the same conclusion with the whole tree in view.
        if (Scoped)
        {
            plan.Stats.DeferredToFullPass++;
            return;
        }
        plan.StateUpdates.Add(db with { Status = EntryStatus.Tombstone, StatusChangedUtc = _now });
        plan.Stats.Tombstoned++;
        if (db.Kind == EntryKind.Directory) tombstoneDirs.Add(db.RelativePath);
    }

    private StateEntry NewEntry(ScanEntry s) => new()
    {
        PairName = _pair,
        RelativePath = s.RelativePath,
        Kind = s.Kind,
        Status = EntryStatus.Active,
        FirstSeenUtc = _backfillFirstSeen ? Min(s.CtimeUtc, s.MtimeUtc, _now) : _now,
        LastSeenUtc = _now,
        SourceSize = s.Kind == EntryKind.File ? s.Size : null,
        SourceMtimeUtc = s.Kind == EntryKind.File ? s.MtimeUtc : null,
        ViaLink = s.ViaLink,
    };

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b, DateTimeOffset cap)
    {
        var m = a < b ? a : b;
        // Bogus timestamps (year 1601/1980, or in the future) fall back to now.
        return m.Year < 1990 || m > cap ? cap : m;
    }

    /// <summary>The local copy still matches what we wrote (or adopted).</summary>
    private static bool LocalIntact(ScanEntry l, StateEntry db) =>
        db.CopiedSize is null || Same(l, db.CopiedSize, db.CopiedMtimeUtc);

    public static bool Same(ScanEntry e, long? size, DateTimeOffset? mtime) =>
        size is { } sz && sz == e.Size && mtime is { } mt && (e.MtimeUtc - mt).Duration() <= MtimeTolerance;

    private static bool UnderTombstone(string path, HashSet<string> tombstoneDirs)
    {
        if (tombstoneDirs.Count == 0) return false;
        var i = path.LastIndexOf('\\');
        while (i > 0)
        {
            if (tombstoneDirs.Contains(path[..i])) return true;
            i = path.LastIndexOf('\\', i - 1);
        }
        return false;
    }

    private static int Depth(string path)
    {
        var d = 0;
        foreach (var c in path) if (c == '\\') d++;
        return d;
    }
}
