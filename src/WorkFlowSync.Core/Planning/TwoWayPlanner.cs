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
    private readonly WorkFlowSync.Core.Config.GoogleDocExportMode _googleDocExport;
    private readonly bool _convertGoogleRecordings;
    private readonly bool _uploadConvertedDocs;
    private readonly bool _requireApproval;
    private readonly bool _freeUpSourceSpaceAfterCopy;
    private readonly string? _targetRoot;
    private readonly bool _shortenLongPaths;
    private readonly int _maxPathLength;

    /// <param name="scope">
    /// Limits the pass to one directory. Deletions do not travel in a scoped pass: they are inferred from
    /// absence, and on a root without a Recycle Bin they cannot be undone (docs/plan-etap5.md §4.2, §4.5).
    /// </param>
    /// <param name="frozenPaths">
    /// Paths and directory subtrees frozen from automatic sync because they are awaiting human approval (docs/plan-etap6.md §5.2).
    /// </param>
    /// <param name="googleDocExport">
    /// Export mode for native Google web documents (.gdoc, .gsheet, etc.): Office, Pdf, or Skip.
    /// Converted files strictly travel towards destination (Side B); reverse sync to Side A is suppressed unless <paramref name="uploadConvertedDocs"/> is true.
    /// </param>
    /// <param name="convertGoogleRecordings">
    /// Whether to convert native Google recordings (.gvid) to MP4.
    /// </param>
    /// <param name="uploadConvertedDocs">
    /// When true, locally modified converted documents are uploaded to Google Drive as regular binary files.
    /// </param>
    /// <param name="requireApproval">
    /// When true, changes in the moderated folder (Side B) are placed in the pending approval queue rather than copied automatically to Side A.
    /// </param>
    public TwoWayPlanner(string pair, DateTimeOffset now, PlanScope? scope = null, FrozenPaths? frozenPaths = null,
        WorkFlowSync.Core.Config.GoogleDocExportMode googleDocExport = WorkFlowSync.Core.Config.GoogleDocExportMode.Office,
        bool convertGoogleRecordings = true,
        bool uploadConvertedDocs = false,
        bool requireApproval = false,
        bool freeUpSourceSpaceAfterCopy = false,
        string? targetRoot = null,
        bool shortenLongPaths = false,
        int maxPathLength = 240)
    {
        _pair = pair;
        _now = now;
        _scope = scope;
        _frozenPaths = frozenPaths;
        _googleDocExport = googleDocExport;
        _convertGoogleRecordings = convertGoogleRecordings;
        _uploadConvertedDocs = uploadConvertedDocs;
        _requireApproval = requireApproval;
        _freeUpSourceSpaceAfterCopy = freeUpSourceSpaceAfterCopy;
        _targetRoot = targetRoot;
        _shortenLongPaths = shortenLongPaths;
        _maxPathLength = maxPathLength;
    }

    public SyncPlan Plan(IReadOnlyDictionary<string, ScanEntry> a, IReadOnlyDictionary<string, ScanEntry> b,
        IReadOnlyDictionary<string, StateEntry> state)
    {
        var plan = new SyncPlan();
        var mappedA = GoogleDrive.MapGoogleDocsForExport(a, _googleDocExport, _convertGoogleRecordings, out var targetToSourceMap);
        if (_shortenLongPaths && !string.IsNullOrEmpty(_targetRoot))
        {
            mappedA = PathShortener.MapEntries(mappedA, _targetRoot, _shortenLongPaths, _maxPathLength, state, targetToSourceMap, out targetToSourceMap);
        }
        var deletions = new List<SyncAction>();

        var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        all.UnionWith(mappedA.Keys);
        all.UnionWith(b.Keys);
        all.UnionWith(state.Keys);
        if (_scope is { } scope) all.RemoveWhere(p => !scope.Contains(p));

        var matchedMoves = new Dictionary<string, MoveMatch>(StringComparer.OrdinalIgnoreCase);
        var matchedOldPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (_requireApproval)
        {
            var missingInB = new List<ScanEntry>();
            var appearedInB = new List<ScanEntry>();

            foreach (var path in all)
            {
                if (_frozenPaths is not null && _frozenPaths.IsFrozen(path)) continue;
                if (GoogleDrive.IsGoogleDoc(path)) continue;

                mappedA.TryGetValue(path, out var sa);
                b.TryGetValue(path, out var sb);
                state.TryGetValue(path, out var db);

                if (db?.Status == EntryStatus.Tombstone) continue;

                if (sb is null && sa is not null && db is not null && !Changed(sa, db, true))
                {
                    missingInB.Add(sa);
                }
                else if (sb is not null && sa is null && db is null)
                {
                    appearedInB.Add(sb);
                }
            }

            var (matches, _, _) = MoveDetector.Detect(missingInB, appearedInB);
            foreach (var m in matches)
            {
                matchedMoves[m.NewItem.RelativePath] = m;
                matchedOldPaths.Add(m.OldItem.RelativePath);
            }
        }

        // Parents before children, so a folder exists before its content is copied into it.
        foreach (var path in all.OrderBy(Depth).ThenBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            if (_frozenPaths is not null && _frozenPaths.IsFrozen(path))
            {
                plan.Stats.AwaitingApproval++;
                continue;
            }

            if (matchedOldPaths.Contains(path))
            {
                plan.Stats.AwaitingApproval++;
                continue;
            }

            if (matchedMoves.TryGetValue(path, out var moveMatch))
            {
                plan.PendingUpdates.Add(new PendingEntry
                {
                    Pair = _pair,
                    Path = moveMatch.NewItem.RelativePath,
                    FromPath = moveMatch.OldItem.RelativePath,
                    Change = moveMatch.ChangeType,
                    Kind = moveMatch.NewItem.Kind,
                    SrcSize = moveMatch.OldItem.Kind == EntryKind.File ? moveMatch.OldItem.Size : null,
                    SrcMtimeUtc = moveMatch.OldItem.MtimeUtc,
                    DstSize = moveMatch.NewItem.Kind == EntryKind.File ? moveMatch.NewItem.Size : null,
                    DstMtimeUtc = moveMatch.NewItem.MtimeUtc,
                    DetectedUtc = _now,
                    Status = PendingStatus.Pending,
                });
                plan.Stats.AwaitingApproval++;
                continue;
            }

            if (GoogleDrive.IsGoogleDoc(path))
            {
                if (state.ContainsKey(path) && _scope is null) plan.Forget.Add(path);
                plan.Warnings.Add($"{path}: native Google document without binary file content; skipped");
                continue;
            }

            mappedA.TryGetValue(path, out var sa);
            b.TryGetValue(path, out var sb);
            state.TryGetValue(path, out var db);
            targetToSourceMap.TryGetValue(path, out var sourceDocPath);

            // Rotation is target-only even in two-way mode. Its tombstone is a final suppression marker:
            // do not restore the target from A and, crucially, do not propagate the target removal to A.
            if (db?.Status == EntryStatus.Tombstone) continue;

            if (db?.Status == EntryStatus.LocalOnly)
            {
                if (sb is not null && SyncPlanner.Same(sb, db.CopiedSize, db.CopiedMtimeUtc))
                {
                    plan.Stats.Unchanged++;
                    continue;
                }
            }

            if (db?.Status == EntryStatus.Diverged)
            {
                var bSame = sb is not null && SyncPlanner.Same(sb, db.CopiedSize, db.CopiedMtimeUtc);
                var aSame = sa is not null && SyncPlanner.Same(sa, db.SourceSize, db.SourceMtimeUtc);
                if (aSame && bSame)
                {
                    plan.Stats.Unchanged++;
                    continue;
                }
            }

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
                Both(plan, path, sa, sb, db, sourceDocPath);
                continue;
            }

            // Present on one side only.
            var present = sa ?? sb!;
            var missingSide = sa is null ? SyncSide.Source : SyncSide.Target;   // the side that has to change

            if (db is null)
            {
                if (_requireApproval && missingSide == SyncSide.Source)
                {
                    plan.PendingUpdates.Add(new PendingEntry
                    {
                        Pair = _pair,
                        Path = path,
                        Change = PendingChange.Added,
                        Kind = sb!.Kind,
                        DstSize = sb.Kind == EntryKind.File ? sb.Size : null,
                        DstMtimeUtc = sb.MtimeUtc,
                        DetectedUtc = _now,
                        Status = PendingStatus.Pending,
                    });
                    plan.Stats.AwaitingApproval++;
                    continue;
                }

                if (sourceDocPath is not null && missingSide == SyncSide.Source)
                {
                    if (!_uploadConvertedDocs)
                    {
                        plan.Warnings.Add($"{path}: reverse sync of converted Google doc to Google Drive is suppressed (destination-only export)");
                        continue;
                    }
                    plan.Warnings.Add($"{path}: uploading converted document back to Google Drive");
                }
                // New here, never seen before → give it to the other side.
                Create(plan, present, missingSide, sourceDocPath);
                continue;
            }

            if (Changed(present, db, sa is not null))
            {
                if (_requireApproval && missingSide == SyncSide.Source)
                {
                    plan.Warnings.Add($"{path}: deleted on source, changed on target; requires approval");
                    plan.Stats.Conflicts++;
                    plan.Stats.AwaitingApproval++;
                    plan.PendingUpdates.Add(new PendingEntry
                    {
                        Pair = _pair,
                        Path = path,
                        Change = PendingChange.Conflict,
                        Kind = sb!.Kind,
                        SrcSize = db.SourceSize,
                        SrcMtimeUtc = db.SourceMtimeUtc,
                        DstSize = sb.Kind == EntryKind.File ? sb.Size : null,
                        DstMtimeUtc = sb.MtimeUtc,
                        DetectedUtc = _now,
                        Status = PendingStatus.Pending,
                    });
                    continue;
                }

                if (sourceDocPath is not null && missingSide == SyncSide.Source)
                {
                    if (!_uploadConvertedDocs)
                    {
                        plan.Warnings.Add($"{path}: reverse sync of converted Google doc to Google Drive is suppressed (destination-only export)");
                        continue;
                    }
                    plan.Warnings.Add($"{path}: uploading modified converted document back to Google Drive");
                }
                // Deleted on one side but edited on the other since the last pass: keep the edit, copy it back.
                plan.Warnings.Add($"{path}: deleted on one side, changed on the other; the changed copy is kept");
                plan.Stats.Conflicts++;
                Create(plan, present, missingSide, sourceDocPath);
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

            if (_requireApproval && side == SyncSide.Source)
            {
                plan.PendingUpdates.Add(new PendingEntry
                {
                    Pair = _pair,
                    Path = path,
                    Change = PendingChange.Deleted,
                    Kind = sa!.Kind,
                    SrcSize = sa.Kind == EntryKind.File ? sa.Size : null,
                    SrcMtimeUtc = sa.MtimeUtc,
                    DetectedUtc = _now,
                    Status = PendingStatus.Pending,
                });
                plan.Stats.AwaitingApproval++;
                continue;
            }

            if (side == SyncSide.Source && sourceDocPath is not null)
            {
                plan.Warnings.Add($"{path}: deletion of Google Doc on Google Drive because of destination deletion is suppressed (destination-only export)");
                continue;
            }

            deletions.Add(new SyncAction(present.Kind == EntryKind.Directory ? SyncActionKind.DeleteDirectory : SyncActionKind.DeleteFile,
                path, db, side));
        }

        AddDeletions(plan, deletions);
        return plan;
    }

    /// <summary>Both sides have the item: adopt it, leave it alone, or carry the newer version across.</summary>
    private void Both(SyncPlan plan, string path, ScanEntry sa, ScanEntry sb, StateEntry? db, string? sourceDocPath = null)
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

            if (_freeUpSourceSpaceAfterCopy && sa.Kind == EntryKind.File && SyncPlanner.ShouldFreeUpSource(sa))
            {
                plan.Actions.Add(new SyncAction(SyncActionKind.FreeUpSourceFile, path, db ?? Row(sa, sb), SyncSide.Target, sourceDocPath));
                plan.Stats.SourceFreed++;
            }
            return;
        }

        if (_requireApproval)
        {
            if (db is not null && aChanged && bChanged)
            {
                plan.Warnings.Add($"{path}: changed on both sides; requires approval (conflict)");
                plan.Stats.Conflicts++;
                plan.Stats.AwaitingApproval++;
                plan.PendingUpdates.Add(new PendingEntry
                {
                    Pair = _pair,
                    Path = path,
                    Change = PendingChange.Conflict,
                    Kind = sb.Kind,
                    SrcSize = sa.Kind == EntryKind.File ? sa.Size : null,
                    SrcMtimeUtc = sa.MtimeUtc,
                    DstSize = sb.Kind == EntryKind.File ? sb.Size : null,
                    DstMtimeUtc = sb.MtimeUtc,
                    DetectedUtc = _now,
                    Status = PendingStatus.Pending
                });
                return;
            }

            if (bChanged && !aChanged)
            {
                plan.Stats.AwaitingApproval++;
                plan.PendingUpdates.Add(new PendingEntry
                {
                    Pair = _pair,
                    Path = path,
                    Change = PendingChange.Modified,
                    Kind = sb.Kind,
                    SrcSize = sa.Kind == EntryKind.File ? sa.Size : null,
                    SrcMtimeUtc = sa.MtimeUtc,
                    DstSize = sb.Kind == EntryKind.File ? sb.Size : null,
                    DstMtimeUtc = sb.MtimeUtc,
                    DetectedUtc = _now,
                    Status = PendingStatus.Pending
                });
                return;
            }

            if (db is null)
            {
                plan.Stats.Conflicts++;
                plan.Stats.AwaitingApproval++;
                plan.PendingUpdates.Add(new PendingEntry
                {
                    Pair = _pair,
                    Path = path,
                    Change = PendingChange.Conflict,
                    Kind = sb.Kind,
                    SrcSize = sa.Kind == EntryKind.File ? sa.Size : null,
                    SrcMtimeUtc = sa.MtimeUtc,
                    DstSize = sb.Kind == EntryKind.File ? sb.Size : null,
                    DstMtimeUtc = sb.MtimeUtc,
                    DetectedUtc = _now,
                    Status = PendingStatus.Pending
                });
                return;
            }
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

        if (sourceDocPath is not null && !fromA)
        {
            if (!_uploadConvertedDocs)
            {
                // Reverse sync to Side A (Google Drive) is suppressed for converted Google Docs
                plan.Warnings.Add($"{path}: reverse sync of converted Google doc to Google Drive is suppressed (destination-only export)");
                return;
            }
            plan.Warnings.Add($"{path}: uploading modified converted document back to Google Drive");
        }

        // After the copy both sides hold the winner, so the row describes it twice; the executor
        // then refreshes the side it actually wrote from the file it produced.
        var winner = fromA ? sa : sb;
        plan.Actions.Add(new SyncAction(SyncActionKind.UpdateFile, path, Row(winner, winner, db),
            fromA ? SyncSide.Target : SyncSide.Source,
            fromA ? sourceDocPath : null));
        plan.Stats.Updated++;
        plan.Stats.BytesToCopy += winner.Size;
    }

    /// <summary>Copy an item that exists on one side only to the other side.</summary>
    private void Create(SyncPlan plan, ScanEntry present, SyncSide to, string? sourceDocPath = null)
    {
        var row = Row(present, present);
        plan.Actions.Add(new SyncAction(
            present.Kind == EntryKind.Directory ? SyncActionKind.CreateDirectory : SyncActionKind.CopyFile,
            present.RelativePath, row, to, to == SyncSide.Target ? sourceDocPath : null));
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
