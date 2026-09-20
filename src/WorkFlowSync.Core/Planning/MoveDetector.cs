using WorkFlowSync.Core.Model;
using WorkFlowSync.Core.Scanning;

namespace WorkFlowSync.Core.Planning;

/// <summary>
/// A matched pair of missing and appeared items recognized as a rename or move (docs/plan-etap6.md §5.3).
/// Pure component with no file I/O.
/// </summary>
public sealed record MoveMatch
{
    public required ScanEntry OldItem { get; init; }
    public required ScanEntry NewItem { get; init; }
    public required PendingChange ChangeType { get; init; }
    public bool IsCaseOnlyRename { get; init; }
}

/// <summary>
/// Pairwise detector for file/directory renames and moves within one scan pass (docs/plan-etap6.md §5.3 & §5.4a).
/// Pure component, no file system access.
/// </summary>
public static class MoveDetector
{
    /// <summary>
    /// Matches disappeared items against appeared items to detect renames and moves.
    /// Returns the matched pairs and the unmatched remainders.
    /// </summary>
    public static (IReadOnlyList<MoveMatch> Matches, IReadOnlyList<ScanEntry> UnmatchedMissing, IReadOnlyList<ScanEntry> UnmatchedAppeared) Detect(
        IEnumerable<ScanEntry> missing,
        IEnumerable<ScanEntry> appeared)
    {
        var missingList = missing.ToList();
        var appearedList = appeared.ToList();

        var matches = new List<MoveMatch>();
        var matchedMissing = new HashSet<ScanEntry>();
        var matchedAppeared = new HashSet<ScanEntry>();

        // 1. First pass: check case-only renames (e.g. "Звіт.docx" -> "ЗВІТ.docx" on same relative path)
        var appearedByNormalizedPath = appearedList.ToDictionary(a => a.RelativePath, StringComparer.OrdinalIgnoreCase);
        foreach (var m in missingList)
        {
            if (appearedByNormalizedPath.TryGetValue(m.RelativePath, out var a))
            {
                // Same path ignoring case, but different ordinal string
                if (!string.Equals(m.RelativePath, a.RelativePath, StringComparison.Ordinal))
                {
                    matches.Add(new MoveMatch
                    {
                        OldItem = m,
                        NewItem = a,
                        ChangeType = PendingChange.Renamed,
                        IsCaseOnlyRename = true,
                    });
                    matchedMissing.Add(m);
                    matchedAppeared.Add(a);
                }
            }
        }

        // 2. Second pass: match remaining items by size, mtime tolerance, extension, and strict 1-to-1 candidate count
        var remainingMissing = missingList.Where(m => !matchedMissing.Contains(m)).ToList();
        var remainingAppeared = appearedList.Where(a => !matchedAppeared.Contains(a)).ToList();

        foreach (var m in remainingMissing)
        {
            // Zero-byte files (0 bytes) are never glued (rule §5.3)
            if (m.Kind == EntryKind.File && m.Size == 0) continue;

            var candidates = remainingAppeared
                .Where(a => !matchedAppeared.Contains(a))
                .Where(a => a.Kind == m.Kind)
                .Where(a => m.Kind == EntryKind.Directory || SameExtension(m.RelativePath, a.RelativePath))
                .Where(a => m.Kind == EntryKind.Directory || m.Size == a.Size)
                .Where(a => MtimeMatches(m.MtimeUtc, a.MtimeUtc))
                .ToList();

            // Must be EXACTLY ONE candidate on appeared side for this missing item
            if (candidates.Count == 1)
            {
                var cand = candidates[0];

                // Reverse check: make sure this candidate also matches ONLY this missing item
                var reverseCandidates = remainingMissing
                    .Where(rm => !matchedMissing.Contains(rm))
                    .Where(rm => rm.Kind == cand.Kind)
                    .Where(rm => cand.Kind == EntryKind.Directory || SameExtension(rm.RelativePath, cand.RelativePath))
                    .Where(rm => cand.Kind == EntryKind.Directory || rm.Size == cand.Size)
                    .Where(rm => MtimeMatches(rm.MtimeUtc, cand.MtimeUtc))
                    .ToList();

                if (reverseCandidates.Count == 1)
                {
                    var changeType = ClassifyChange(m.RelativePath, cand.RelativePath);
                    matches.Add(new MoveMatch
                    {
                        OldItem = m,
                        NewItem = cand,
                        ChangeType = changeType,
                        IsCaseOnlyRename = false,
                    });
                    matchedMissing.Add(m);
                    matchedAppeared.Add(cand);
                }
            }
        }

        var unmatchedMissingList = missingList.Where(m => !matchedMissing.Contains(m)).ToList();
        var unmatchedAppearedList = appearedList.Where(a => !matchedAppeared.Contains(a)).ToList();

        return (matches, unmatchedMissingList, unmatchedAppearedList);
    }

    private static bool SameExtension(string path1, string path2)
    {
        var ext1 = Path.GetExtension(path1);
        var ext2 = Path.GetExtension(path2);
        return string.Equals(ext1, ext2, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MtimeMatches(DateTimeOffset? t1, DateTimeOffset? t2)
    {
        if (t1 is null || t2 is null) return false;
        return Math.Abs((t1.Value - t2.Value).TotalSeconds) <= 2.0;
    }

    private static PendingChange ClassifyChange(string oldPath, string newPath)
    {
        var oldDir = Path.GetDirectoryName(oldPath) ?? "";
        var newDir = Path.GetDirectoryName(newPath) ?? "";

        return string.Equals(oldDir, newDir, StringComparison.OrdinalIgnoreCase)
            ? PendingChange.Renamed
            : PendingChange.Moved;
    }
}
