namespace WorkFlowSync.Core.Config;

/// <summary>
/// How much of the machine a pass may take (docs/product/features/scanner-performance.md).
/// Scanning a huge tree is cheap per item but there are millions of them, so on a weak PC the
/// default settings make everything else crawl. This caps both the number of parallel listings
/// and the scheduling priority of the threads doing them.
/// </summary>
public enum CpuLoad
{
    /// <summary>Balanced (default): half the cores at most, worker threads below normal priority.</summary>
    Balanced = 0,

    /// <summary>Everything the settings allow, at normal priority — fastest, least polite.</summary>
    Full = 1,

    /// <summary>Two listings at lowest priority: the pass takes longer but the machine stays usable.</summary>
    Low = 2,
}
