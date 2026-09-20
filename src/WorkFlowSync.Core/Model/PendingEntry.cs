namespace WorkFlowSync.Core.Model;

/// <summary>
/// Type of change observed in the moderated target folder.
/// </summary>
public enum PendingChange
{
    Added = 1,
    Modified = 2,
    Deleted = 3,
    Renamed = 4,
    Moved = 5,
    Conflict = 6,
}

/// <summary>
/// Resolution status of an entry in the approval queue.
/// </summary>
public enum PendingStatus
{
    /// <summary>Awaiting user decision.</summary>
    Pending = 0,

    /// <summary>Approved by the user; change applied to the source folder.</summary>
    Approved = 1,

    /// <summary>Dismissed by the user ("leave as is"); divergence accepted.</summary>
    Dismissed = 2,

    /// <summary>Reverted by the user; target restored to source state.</summary>
    Reverted = 3,

    /// <summary>Superseded by a newer change before the user could decide.</summary>
    Stale = 4,
}

/// <summary>
/// A row in the pending approval queue (docs/plan-etap6.md §5.1).
/// Represents a change observed in the moderated (second) folder awaiting a human decision.
/// </summary>
public sealed record PendingEntry
{
    public long Id { get; init; }

    /// <summary>Name of the folder pair.</summary>
    public required string Pair { get; init; }

    /// <summary>Relative path in the moderated (second) folder.</summary>
    public required string Path { get; init; }

    /// <summary>For renamed or moved items: previous relative path. Null otherwise.</summary>
    public string? FromPath { get; init; }

    /// <summary>What kind of change was observed.</summary>
    public required PendingChange Change { get; init; }

    /// <summary>Whether this item is a file or a directory.</summary>
    public required EntryKind Kind { get; init; }

    /// <summary>Initial file size (first folder / last known state).</summary>
    public long? SrcSize { get; init; }

    /// <summary>Initial file mtime (first folder / last known state).</summary>
    public DateTimeOffset? SrcMtimeUtc { get; init; }

    /// <summary>Modified file size in the moderated folder at detection time.</summary>
    public long? DstSize { get; init; }

    /// <summary>Modified file mtime in the moderated folder at detection time.</summary>
    public DateTimeOffset? DstMtimeUtc { get; init; }

    /// <summary>When the change was first detected.</summary>
    public required DateTimeOffset DetectedUtc { get; init; }

    /// <summary>Lifecycle status in the approval queue.</summary>
    public PendingStatus Status { get; init; } = PendingStatus.Pending;

    /// <summary>When the user decision was made.</summary>
    public DateTimeOffset? ResolvedUtc { get; init; }

    /// <summary>Relative or absolute path in .wfsversions where a safety copy was placed on revert, if versioning was enabled.</summary>
    public string? VersionPath { get; init; }
}
