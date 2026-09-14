namespace WorkFlowSync.Core.Model;

/// <summary>
/// Lifecycle of a mirrored item. See docs/product/features/state-and-tombstones.md.
/// </summary>
public enum EntryStatus
{
    /// <summary>Mirrored and kept in sync with the source while the local copy stays untouched.</summary>
    Active = 0,

    /// <summary>Deleted or moved locally (or expired by retention). Never re-pulled from the source.</summary>
    Tombstone = 1,

    /// <summary>Changed locally by the user. Kept as is; source updates are ignored.</summary>
    LocalModified = 2,
}
