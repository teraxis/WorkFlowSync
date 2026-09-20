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
    /// <summary>Auto-clean: move an expired, untouched local file to the Recycle Bin; its row becomes a tombstone.</summary>
    RecycleFile,
    /// <summary>Auto-clean: move a directory left empty by expired files to the Recycle Bin; its row is forgotten.</summary>
    RecycleEmptyDirectory,
    /// <summary>Two-way: the item is gone on the other side — move this copy to the Recycle Bin and forget the row.</summary>
    DeleteFile,
    /// <summary>Two-way: the directory is gone on the other side — move it to the Recycle Bin and forget the row.</summary>
    DeleteDirectory,
}

/// <summary>Which of the pair's two roots an action writes to. Mirror mode only ever writes to the target.</summary>
public enum SyncSide
{
    Target = 0,
    Source = 1,
}

/// <summary>A file-system action plus the state row to persist once it succeeds.</summary>
public sealed record SyncAction(SyncActionKind Kind, string RelativePath, StateEntry Proposed, SyncSide Side = SyncSide.Target);

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

    /// <summary>Source files the intake window (maxAge) left behind: too old to copy, or to keep updating.</summary>
    public int TooOld { get; set; }

    /// <summary>Two-way: items removed on one side and therefore removed on the other.</summary>
    public int Deleted { get; set; }

    /// <summary>Two-way: items changed on both sides since the last pass; the newer one won.</summary>
    public int Conflicts { get; set; }

    /// <summary>Two-way: items frozen awaiting human approval in the pending queue.</summary>
    public int AwaitingApproval { get; set; }

    /// <summary>
    /// Destructive decisions a scoped pass declined to make (tombstone, delete, expire). They are not lost:
    /// the next full pass sees the same situation with the whole tree in front of it.
    /// </summary>
    public int DeferredToFullPass { get; set; }

    public override string ToString() =>
        $"new={New} update={Updated} adopt={Adopted} tombstone={Tombstoned} local_modified={LocalModified} unchanged={Unchanged} local_only={IgnoredLocalOnly} expired={Expired} empty_dirs={EmptyDirs}{(TooOld > 0 ? $" too_old={TooOld}" : "")}{(Deleted > 0 || Conflicts > 0 ? $" deleted={Deleted} conflicts={Conflicts}" : "")}{(AwaitingApproval > 0 ? $" awaiting_approval={AwaitingApproval}" : "")}{(DeferredToFullPass > 0 ? $" deferred={DeferredToFullPass}" : "")} bytes={BytesToCopy}";

}

public sealed class SyncPlan
{
    /// <summary>File-system work, parents before children.</summary>
    public List<SyncAction> Actions { get; } = new();

    /// <summary>Bookkeeping-only state changes (adoptions, tombstones, local_modified) — applied without touching files.</summary>
    public List<StateEntry> StateUpdates { get; } = new();

    /// <summary>Rows to drop without touching any file (two-way: the item is gone on both sides).</summary>
    public List<string> Forget { get; } = new();

    public List<string> Warnings { get; } = new();

    public PlanStats Stats { get; } = new();
}
