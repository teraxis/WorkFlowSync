using WorkFlowSync.Core.Model;

namespace WorkFlowSync.Core.Planning;

public enum SyncActionKind
{
    /// <summary>Create the directory in the target (new in source).</summary>
    CreateDirectory,
    /// <summary>Copy a file that is new in the source.</summary>
    CopyFile,
    /// <summary>Overwrite an unchanged local copy with a newer source version.</summary>
    UpdateFile,
    /// <summary>Retention: move an expired, untouched local file to the Recycle Bin; its row becomes a tombstone.</summary>
    RecycleFile,
    /// <summary>Retention: move a directory left empty by expired files to the Recycle Bin; its row is forgotten.</summary>
    RecycleEmptyDirectory,
}

/// <summary>A file-system action plus the state row to persist once it succeeds.</summary>
public sealed record SyncAction(SyncActionKind Kind, string RelativePath, StateEntry Proposed);

public sealed class PlanStats
{
    public int New { get; set; }
    public int Updated { get; set; }
    public int Adopted { get; set; }
    public int Tombstoned { get; set; }
    public int LocalModified { get; set; }
    public int Unchanged { get; set; }
    public int IgnoredLocalOnly { get; set; }
    public int Expired { get; set; }
    public int EmptyDirs { get; set; }
    public long BytesToCopy { get; set; }

    public override string ToString() =>
        $"new={New} update={Updated} adopt={Adopted} tombstone={Tombstoned} local_modified={LocalModified} unchanged={Unchanged} local_only={IgnoredLocalOnly} expired={Expired} empty_dirs={EmptyDirs} bytes={BytesToCopy}";
}

public sealed class SyncPlan
{
    /// <summary>File-system work, parents before children.</summary>
    public List<SyncAction> Actions { get; } = new();

    /// <summary>Bookkeeping-only state changes (adoptions, tombstones, local_modified) — applied without touching files.</summary>
    public List<StateEntry> StateUpdates { get; } = new();

    public List<string> Warnings { get; } = new();

    public PlanStats Stats { get; } = new();
}
