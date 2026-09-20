using WorkFlowSync.Core.Model;
using WorkFlowSync.Core.Scanning;

namespace WorkFlowSync.Core.Planning;

/// <summary>
/// Pure decision logic for <see cref="Config.SyncMode.TwoWay"/> — no file-system access.
/// Whatever happens in one folder happens in the other: additions, edits and deletions travel both ways
/// (docs/product/features/sync-modes.md). The mirror rules about local deletions being final do not apply,
/// and retention is not used here.
/// <para>
/// The state row keeps both sides as of the last pass: <see cref="StateEntry.SourceSize"/>/<see cref="StateEntry.SourceMtimeUtc"/>
/// describe side A (the pair's source) and <see cref="StateEntry.CopiedSize"/>/<see cref="StateEntry.CopiedMtimeUtc"/> side B
/// (the target), so the same table serves both modes.
/// </para>
/// </summary>
public sealed class TwoWayPlanner
{
    private readonly string _pair;
    private readonly DateTimeOffset _now;
    private readonly PlanScope? _scope;
    private readonly FrozenPaths? _frozenPaths;

    /// <param name="scope">
    /// Limits the pass to one directory. Deletions do not travel in a scoped pass: they are inferred from
    /// absence, and on a root without a Recycle Bin they cannot be undone (docs/plan-etap5.md §4.2, §4.5).
    /// </param>
    /// <param name="frozenPaths">
    /// Paths and directory subtrees frozen from automatic sync because they are awaiting human approval (docs/plan-etap6.md §5.2).
    /// </param>
    public TwoWayPlanner(string pair, DateTimeOffset now, PlanScope? scope = null, FrozenPaths? frozenPaths = null)
    {
        _pair = pair;
        _now = now;
        _scope = scope;
        _frozenPaths = frozenPaths;
    }

    public SyncPlan Plan(IReadOnlyDictionary<string, ScanEntry> a, IReadOnlyDictionary<string, ScanEntry> b,
        IReadOnlyDictionary<string, StateEntry> state)
    {
        var plan = new SyncPlan();
        var deletions = new List<SyncAction>();

        var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        all.UnionWith(a.Keys);
        all.UnionWith(b.Keys);
        all.UnionWith(state.Keys);
        if (_scope is { } scope) all.RemoveWhere(p => !scope.Contains(p));

        // Parents before children, so a folder exists before its content is copied into it.
        foreach (var path in all.OrderBy(Depth).ThenBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            if (_frozenPaths is not null && _frozenPaths.IsFrozen(path))
            {
                plan.Stats.AwaitingApproval++;
                continue;
            }

            a.TryGetValue(path, out var sa);
            b.TryGetValue(path, out var sb);
            state.TryGetValue(path, out var db);

            if (sa is null && sb is null)
            {
                // Gone on both sides: nothing left to remember. A scoped pass keeps the row — forgetting it
                // would let the item come back as brand new if the scope was wrong about what it saw.
                if (db is not null)
                {
                    if (_scope is null) plan.Forget.Add(path);
                    else plan.Stats.DeferredToFullPass++;
                }
                continue;
            }

            if (sa is not null && sb is not null)
            {
                Both(plan, path, sa, sb, db);
                continue;
            }

            // Present on one side only.
            var present = sa ?? sb!;
            var missingSide = sa is null ? SyncSide.Source : SyncSide.Target;   // the side that has to change

            if (db is null)
            {
                // New here, never seen before → give it to the other side.
                Create(plan, present, missingSide);
                continue;
            }

            if (Changed(present, db, sa is not null))
            {
                // Deleted on one side but edited on the other since the last pass: keep the edit, copy it back.
                plan.Warnings.Add($"{path}: deleted on one side, changed on the other; the changed copy is kept");
                plan.Stats.Conflicts++;
                Create(plan, present, missingSide);
                continue;
            }

            // A clean deletion: remove the surviving copy too — but only when the whole pair was looked at.
            // On a network root the delete is permanent (docs/plan-etap5.md §4.5), so a partial view of the
            // tree is never enough to justify it.
            if (_scope is not null)
            {
                plan.Stats.DeferredToFullPass++;
                continue;
            }

            var side = sa is null ? SyncSide.Target : SyncSide.Source;   // where the surviving copy lives
            deletions.Add(new SyncAction(present.Kind == EntryKind.Directory ? SyncActionKind.DeleteDirectory : SyncActionKind.DeleteFile,
                path, db, side));
        }

        AddDeletions(plan, deletions);
        return plan;
    }

    /// <summary>Both sides have the item: adopt it, leave it alone, or carry the newer version across.</summary>
    private void Both(SyncPlan plan, string path, ScanEntry sa, ScanEntry sb, StateEntry? db)
    {
        if (sa.Kind != sb.Kind)
        {
            plan.Warnings.Add($"{path}: a folder on one side and a file on the other; skipped");
            return;
        }

        if (sa.Kind == EntryKind.Directory)
        {
            if (db is null) { plan.StateUpdates.Add(Row(sa, sb)); plan.Stats.Adopted++; }
            else plan.Stats.Unchanged++;
            return;
        }

        var aChanged = db is null || !SyncPlanner.Same(sa, db.SourceSize, db.SourceMtimeUtc);
        var bChanged = db is null || !SyncPlanner.Same(sb, db.CopiedSize, db.CopiedMtimeUtc);

        if (SameFile(sa, sb))
        {
            // Identical on both sides: just remember them (adoption on the first pass, bookkeeping later).
            if (db is null) { plan.StateUpdates.Add(Row(sa, sb)); plan.Stats.Adopted++; }
            else if (aChanged || bChanged) plan.StateUpdates.Add(Row(sa, sb, db));
            else plan.Stats.Unchanged++;
            return;
        }

        if (db is not null && aChanged && bChanged)
        {
            plan.Warnings.Add($"{path}: changed on both sides; the newer copy wins");
            plan.Stats.Conflicts++;
        }

        // Who wins: the side that changed, or — when both did, or we have never seen the item — the newer mtime.
        var fromA = db is null || (aChanged && bChanged)
            ? sa.MtimeUtc >= sb.MtimeUtc
            : aChanged;

        // After the copy both sides hold the winner, so the row describes it twice; the executor
        // then refreshes the side it actually wrote from the file it produced.
        var winner = fromA ? sa : sb;
        plan.Actions.Add(new SyncAction(SyncActionKind.UpdateFile, path, Row(winner, winner, db),
            fromA ? SyncSide.Target : SyncSide.Source));
        plan.Stats.Updated++;
        plan.Stats.BytesToCopy += winner.Size;
    }

    /// <summary>Copy an item that exists on one side only to the other side.</summary>
    private void Create(SyncPlan plan, ScanEntry present, SyncSide to)
    {
        var row = Row(present, present);
        plan.Actions.Add(new SyncAction(
            present.Kind == EntryKind.Directory ? SyncActionKind.CreateDirectory : SyncActionKind.CopyFile,
            present.RelativePath, row, to));
        plan.Stats.New++;
        plan.Stats.BytesToCopy += present.Size;
    }

    /// <summary>
    /// Deletions run after the copies, deepest first. A folder that still receives something in this very pass
    /// is kept: the other side removed it, but there is new content to put back into it.
    /// </summary>
    private void AddDeletions(SyncPlan plan, List<SyncAction> deletions)
    {
        if (deletions.Count == 0) return;
        var written = plan.Actions.Select(x => x.RelativePath).ToList();

        foreach (var d in deletions.OrderByDescending(x => Depth(x.RelativePath))
                                   .ThenByDescending(x => x.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            if (d.Kind == SyncActionKind.DeleteDirectory)
            {
                var prefix = d.RelativePath + "\\";
                if (written.Any(w => w.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                {
                    plan.Warnings.Add($"{d.RelativePath}: folder removed on one side but new content arrived; kept");
                    continue;
                }
            }
            plan.Actions.Add(d);
            plan.Stats.Deleted++;
        }
    }

    /// <summary>The item on this side differs from what the state recorded for it.</summary>
    private static bool Changed(ScanEntry e, StateEntry db, bool sideA)
    {
        if (e.Kind == EntryKind.Directory) return false;
        return sideA ? !SyncPlanner.Same(e, db.SourceSize, db.SourceMtimeUtc)
                     : !SyncPlanner.Same(e, db.CopiedSize, db.CopiedMtimeUtc);
    }

    private static bool SameFile(ScanEntry a, ScanEntry b) =>
        a.Size == b.Size && (a.MtimeUtc - b.MtimeUtc).Duration() <= SyncPlanner.MtimeTolerance;

    /// <summary>A state row describing both sides. <paramref name="db"/> keeps first_seen when the item is already known.</summary>
    private StateEntry Row(ScanEntry a, ScanEntry b, StateEntry? db = null) => new()
    {
        PairName = _pair,
        RelativePath = a.RelativePath,
        Kind = a.Kind,
        Status = EntryStatus.Active,
        FirstSeenUtc = db?.FirstSeenUtc ?? _now,
        LastSeenUtc = _now,
        SourceSize = a.Kind == EntryKind.File ? a.Size : null,
        SourceMtimeUtc = a.Kind == EntryKind.File ? a.MtimeUtc : null,
        CopiedSize = b.Kind == EntryKind.File ? b.Size : null,
        CopiedMtimeUtc = b.Kind == EntryKind.File ? b.MtimeUtc : null,
        ViaLink = a.ViaLink || b.ViaLink,
    };

    private static int Depth(string path)
    {
        var d = 0;
        foreach (var c in path) if (c == '\\') d++;
        return d;
    }
}
