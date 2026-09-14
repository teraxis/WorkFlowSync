using WorkFlowSync.Core.Model;

namespace WorkFlowSync.Core.Scanning;

/// <summary>One file or directory observed by <see cref="TreeScanner"/>. Attributes come from the
/// directory listing only — no per-file calls (see docs/product/features/scanner-performance.md).</summary>
public sealed record ScanEntry
{
    /// <summary>Logical path relative to the scanned root, '\' separated, no leading separator.</summary>
    public required string RelativePath { get; init; }

    public required EntryKind Kind { get; init; }

    /// <summary>File size; 0 for directories.</summary>
    public long Size { get; init; }

    public DateTimeOffset MtimeUtc { get; init; }

    public DateTimeOffset CtimeUtc { get; init; }

    /// <summary>Reached through a followed symlink/junction.</summary>
    public bool ViaLink { get; init; }
}

/// <summary>Result of scanning one root.</summary>
public sealed class ScanResult
{
    public Dictionary<string, ScanEntry> Entries { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Warnings { get; } = new();
    public int DirectoryCount { get; set; }
    public int FileCount { get; set; }
    public int LinkCount { get; set; }
    public TimeSpan Elapsed { get; set; }
}

/// <summary>Thrown when the root itself cannot be listed (share offline, drive not mapped).</summary>
public sealed class RootUnavailableException : IOException
{
    public string Root { get; }
    public RootUnavailableException(string root, Exception? inner = null)
        : base($"Root is not available: {root}", inner) => Root = root;
}
