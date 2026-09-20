namespace WorkFlowSync.Core.Model;

/// <summary>
/// Lifecycle of a mirrored item. See docs/product/features/state-and-tombstones.md.
/// </summary>
public enum EntryStatus
{
    /// <summary>Mirrored and kept in sync with the source while the local copy stays untouched.</summary>
    Active = 0,

    /// <summary>Deleted or moved locally (or expired by auto-clean). Never re-pulled from the source.</summary>
    Tombstone = 1,

    /// <summary>Changed locally by the user. Kept as is; source updates are ignored.</summary>
    LocalModified = 2,

    /// <summary>
    /// In the source, but it appeared before the pair's intake window (maxAge), so nothing was copied and
    /// nothing exists locally. The row exists only to remember WHEN we first saw it: without it every later
    /// pass would meet the file as brand new, date it "today" and copy the very archive the window refused.
    /// Widening the window brings such a file in; this is not a tombstone and carries no decision of the user's.
    /// </summary>
    TooOld = 3,

    /// <summary>
    /// Kept as is by the user when approval was requested for a modified or renamed file in target.
    /// Both sides stay diverged; not asked again until target changes further.
    /// </summary>
    Diverged = 4,

    /// <summary>
    /// Kept as is by the user when approval was requested for an added file in target.
    /// Lives in target only and is not copied to source; not asked again until target changes.
    /// </summary>
    LocalOnly = 5,
}

