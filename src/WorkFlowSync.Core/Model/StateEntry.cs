namespace WorkFlowSync.Core.Model;

/// <summary>
/// One row of the state database: what we know about a single relative path inside a folder pair.
/// Key = (PairName, RelativePath). Times are UTC.
/// </summary>
public sealed record StateEntry
{
    public required string PairName { get; init; }

    /// <summary>Logical path relative to the pair root (as seen through links), backslash separated.</summary>
    public required string RelativePath { get; init; }

    public required EntryKind Kind { get; init; }

    public EntryStatus Status { get; init; } = EntryStatus.Active;

    /// <summary>Moment the item was first observed in the source. Drives retention. Not ctime/mtime.</summary>
    public required DateTimeOffset FirstSeenUtc { get; init; }

    public DateTimeOffset LastSeenUtc { get; init; }

    /// <summary>Source attributes at the time of the last copy (files only).</summary>
    public long? SourceSize { get; init; }
    public DateTimeOffset? SourceMtimeUtc { get; init; }

    /// <summary>Attributes of the local copy right after we wrote it; used to detect local edits.</summary>
    public long? CopiedSize { get; init; }
    public DateTimeOffset? CopiedMtimeUtc { get; init; }

    /// <summary>True when the item was reached through a symlink/junction (diagnostics only).</summary>
    public bool ViaLink { get; init; }

    public DateTimeOffset? StatusChangedUtc { get; init; }
}
