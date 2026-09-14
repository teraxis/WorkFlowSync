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
    private readonly TimeSpan? _retention;

    /// <param name="backfillFirstSeen">First pass: use min(ctime, mtime) of the source item as first_seen (docs F3).</param>
    /// <param name="retention">Keep only files first seen within this window; null = forever (docs F3).</param>
    public SyncPlanner(string pair, DateTimeOffset now, bool backfillFirstSeen, TimeSpan? retention = null)
    {
        _pair = pair;
        _now = now;
        _backfillFirstSeen = backfillFirstSeen;
        _retention = retention;
    }

    public SyncPlan Plan(IReadOnlyDictionary<string, ScanEntry> source, IReadOnlyDictionary<string, ScanEntry> target,
        IReadOnlyDictionary<string, StateEntry> state)
    {
        var plan = new SyncPlan();
        var tombstoneDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in state.Values)
            if (e.Kind == EntryKind.Directory && e.Status == EntryStatus.Tombstone)
                tombstoneDirs.Add(e.RelativePath);

        // Parents before children so a directory decision (tombstone cascade) precedes its content.
        var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        all.UnionWith(source.Keys);
        all.UnionWith(target.Keys);
        all.UnionWith(state.Keys);
        var ordered = all.OrderBy(Depth).ThenBy(p => p, StringComparer.OrdinalIgnoreCase);

        var expiredFiles = new List<StateEntry>();

        foreach (var path in ordered)
        {
            source.TryGetValue(path, out var s);
            target.TryGetValue(path, out var l);
            state.TryGetValue(path, out var db);

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
                if (l is null) { AddNew(plan, s); continue; }
                Adopt(plan, s, l);
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

                case EntryStatus.Active:
                    if (l is null)
                    {
                        // Source present or not: the user removed our copy → never bring it back.
                        Tombstone(plan, db, tombstoneDirs, s is null ? "gone on both sides" : "removed locally");
                        break;
                    }
                    if (s is null)
                    {
                        // rule 1: deleted in source → keep (unless retention says otherwise)
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
                        // Retention beats update: an expired file is recycled even if the source changed it.
                        expiredFiles.Add(db);
                        break;
                    }
                    if (!Same(s, db.SourceSize, db.SourceMtimeUtc))
                    {
                        var proposed = db with { SourceSize = s.Size, SourceMtimeUtc = s.MtimeUtc, LastSeenUtc = _now, ViaLink = s.ViaLink };
                        plan.Actions.Add(new SyncAction(SyncActionKind.UpdateFile, path, proposed));
                        plan.Stats.Updated++;
                        plan.Stats.BytesToCopy += s.Size;
                        break;
                    }
                    plan.Stats.Unchanged++;
                    break;
            }
        }

        ApplyRetention(plan, expiredFiles, target, state);
        return plan;
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

    private bool IsExpired(StateEntry db) =>
        _retention is { } r && db.Kind == EntryKind.File && db.FirstSeenUtc < _now - r;

    private void AddNew(SyncPlan plan, ScanEntry s)
    {
        var entry = NewEntry(s);
        plan.Actions.Add(new SyncAction(s.Kind == EntryKind.Directory ? SyncActionKind.CreateDirectory : SyncActionKind.CopyFile, s.RelativePath, entry));
        plan.Stats.New++;
        plan.Stats.BytesToCopy += s.Size;
    }

    /// <summary>Present on both sides but unknown to us (e.g. copied earlier by FreeFileSync): record it, never overwrite it.</summary>
    private void Adopt(SyncPlan plan, ScanEntry s, ScanEntry l)
    {
        var entry = NewEntry(s) with { CopiedSize = l.Kind == EntryKind.File ? l.Size : null, CopiedMtimeUtc = l.Kind == EntryKind.File ? l.MtimeUtc : null };
        if (s.Kind == EntryKind.File && l.Kind == EntryKind.File && !Same(s, l.Size, l.MtimeUtc))
        {
            entry = entry with { Status = EntryStatus.LocalModified, StatusChangedUtc = _now };
            plan.Stats.LocalModified++;
        }
        else
        {
            plan.Stats.Adopted++;
        }
        plan.StateUpdates.Add(entry);
    }

    private void Tombstone(SyncPlan plan, StateEntry db, HashSet<string> tombstoneDirs, string reason)
    {
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
