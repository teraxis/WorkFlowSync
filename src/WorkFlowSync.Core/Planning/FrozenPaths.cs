using WorkFlowSync.Core.Model;

namespace WorkFlowSync.Core.Planning;

/// <summary>
/// Set of paths and directory subtrees that are awaiting approval and frozen from automatic sync (docs/plan-etap6.md §5.2).
/// Immutable, thread-safe, and pure (no I/O).
/// </summary>
public sealed class FrozenPaths
{
    public static FrozenPaths Empty { get; } = new(Enumerable.Empty<string>(), Enumerable.Empty<string>());

    private readonly HashSet<string> _explicitPaths;
    private readonly List<string> _dirPrefixes;

    public FrozenPaths(IEnumerable<string> explicitPaths, IEnumerable<string> dirPrefixes)
    {
        _explicitPaths = new HashSet<string>(
            explicitPaths.Select(NormalizePath),
            StringComparer.OrdinalIgnoreCase);

        _dirPrefixes = dirPrefixes
            .Select(NormalizePath)
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => p.EndsWith('\\') ? p : p + "\\")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Creates <see cref="FrozenPaths"/> from a collection of open <see cref="PendingEntry"/> records.
    /// </summary>
    public static FrozenPaths FromPending(IEnumerable<PendingEntry> pending)
    {
        var files = new List<string>();
        var dirs = new List<string>();

        foreach (var p in pending)
        {
            if (p.Status != PendingStatus.Pending) continue;
            if (p.Kind == EntryKind.Directory)
                dirs.Add(p.Path);
            else
                files.Add(p.Path);

            if (!string.IsNullOrEmpty(p.FromPath))
            {
                if (p.Kind == EntryKind.Directory)
                    dirs.Add(p.FromPath);
                else
                    files.Add(p.FromPath);
            }
        }

        return new FrozenPaths(files, dirs);
    }

    /// <summary>
    /// Returns true if the relative path or any of its parent directories is frozen.
    /// </summary>
    public bool IsFrozen(string relativePath)
    {
        var norm = NormalizePath(relativePath);
        if (string.IsNullOrEmpty(norm)) return false;

        if (_explicitPaths.Contains(norm)) return true;

        foreach (var prefix in _dirPrefixes)
        {
            if (norm.Equals(prefix.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return true;
            if (norm.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    public int Count => _explicitPaths.Count + _dirPrefixes.Count;

    private static string NormalizePath(string p) => p.Trim().Trim('\\', '/');
}
