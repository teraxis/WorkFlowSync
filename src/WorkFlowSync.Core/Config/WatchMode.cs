namespace WorkFlowSync.Core.Config;

/// <summary>
/// Whether a pair watches its folders for changes instead of only checking them on the interval
/// (docs/plan-etap5.md §4.1). Watching never replaces the periodic pass — it only makes the pair
/// react sooner — so <see cref="Auto"/> turns it on wherever the subscription can be created.
/// </summary>
public enum WatchMode
{
    /// <summary>Decide from the storage the folders live on. The default, and right for almost everyone.</summary>
    Auto = 0,

    /// <summary>Always watch, even where the storage type suggests it will not help.</summary>
    On = 1,

    /// <summary>Never watch: check on the interval only.</summary>
    Off = 2,
}
