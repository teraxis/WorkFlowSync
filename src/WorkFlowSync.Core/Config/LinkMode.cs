namespace WorkFlowSync.Core.Config;

/// <summary>How reparse points (symlinks, junctions, DFS links) in the source are handled.</summary>
public enum LinkMode
{
    /// <summary>Traverse the link and mirror its content as a real folder (default; OneDrive-safe).</summary>
    Follow = 0,

    /// <summary>Recreate the link locally instead of copying content.</summary>
    Recreate = 1,

    /// <summary>Ignore the link entirely.</summary>
    Skip = 2,
}
