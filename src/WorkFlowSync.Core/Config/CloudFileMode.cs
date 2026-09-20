namespace WorkFlowSync.Core.Config;

/// <summary>
/// What to do with a file that lives only in the cloud (OneDrive Files On-Demand) when the pair needs to
/// copy it somewhere else — docs/plan-etap5.md §4.6. Reading such a file downloads it in full, so on a big
/// tree the wrong default would quietly pull back everything the user deliberately freed up.
/// </summary>
public enum CloudFileMode
{
    /// <summary>Leave it in the cloud and log a warning. The default, and the safe one.</summary>
    Skip = 0,

    /// <summary>Download it and copy it across, accepting the disk space and the transfer.</summary>
    Hydrate = 1,
}
