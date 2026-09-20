namespace WorkFlowSync.Core.Planning;

/// <summary>
/// The part of a pair a pass is allowed to reason about: one directory and everything under it
/// (docs/plan-etap5.md §4.2). Absent scope = the whole pair, which is the ordinary full pass.
///
/// A scoped pass exists so a change can be picked up in seconds instead of on the interval. It sees only
/// a slice of the tree, so it may only ADD and UPDATE: anything destructive — tombstones, deletions,
/// retention — is inferred from absence, and absence inside a slice proves nothing about the whole pair.
/// Those decisions stay with the full pass, which remains the source of truth.
/// </summary>
public sealed class PlanScope
{
    private readonly string _prefix;

    private PlanScope(string relativeDir)
    {
        RelativeDir = relativeDir;
        _prefix = relativeDir + "\\";
    }

    /// <summary>Directory the pass is limited to, relative to the pair root, without a trailing separator.</summary>
    public string RelativeDir { get; }

    /// <summary>
    /// Builds a scope, or null for a full pass. Null, empty or a lone separator all mean "the whole pair",
    /// so a caller that has nothing to narrow by does not have to special-case it.
    /// </summary>
    public static PlanScope? For(string? relativeDir)
    {
        var dir = relativeDir?.Trim().Trim('\\', '/');
        return string.IsNullOrEmpty(dir) ? null : new PlanScope(dir);
    }

    /// <summary>True when the path is the scope directory itself or lies beneath it.</summary>
    public bool Contains(string relativePath) =>
        relativePath.Equals(RelativeDir, StringComparison.OrdinalIgnoreCase) ||
        relativePath.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase);

    public override string ToString() => RelativeDir;
}
