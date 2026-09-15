namespace WorkFlowSync.Core.Config;

/// <summary>What a folder pair does on every pass (docs/product/features/sync-modes.md).</summary>
public enum SyncMode
{
    /// <summary>
    /// One way, with memory: additions in the source are copied, deletions in the source are ignored,
    /// and anything deleted, moved or edited locally is final. The original behaviour.
    /// </summary>
    Mirror = 0,

    /// <summary>
    /// Both ways: whatever happens in one folder happens in the other — additions, edits and deletions.
    /// The "a local deletion is final" rules do not apply here, and retention is not used.
    /// </summary>
    TwoWay = 1,
}
