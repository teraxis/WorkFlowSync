using System.Text;
using System.Text.RegularExpressions;

namespace WorkFlowSync.Core.Scanning;

/// <summary>
/// FreeFileSync-compatible exclude patterns (docs/product/features/configuration.md):
/// leading '\' anchors the pattern at the root; otherwise it may match at any path segment boundary.
/// '*' matches any run of characters including '\', '?' matches one character. Case-insensitive.
/// A matching directory excludes its whole subtree.
/// </summary>
public sealed class ExcludeMatcher
{
    private readonly List<Regex> _anchored = new();
    private readonly List<Regex> _floating = new();

    public static ExcludeMatcher Empty { get; } = new(Array.Empty<string>());

    public int Count => _anchored.Count + _floating.Count;

    public ExcludeMatcher(IEnumerable<string> patterns)
    {
        foreach (var raw in patterns)
        {
            var p = raw.Trim().Replace('/', '\\');
            if (p.Length == 0) continue;
            if (p.StartsWith('\\'))
                _anchored.Add(ToRegex(p.TrimStart('\\'), anchoredAtRoot: true));
            else
                _floating.Add(ToRegex(p, anchoredAtRoot: false));
        }
    }

    /// <summary>True when the relative path (or any of its ancestors) is excluded.</summary>
    public bool IsExcluded(string relativePath)
    {
        if (Count == 0) return false;
        foreach (var r in _anchored) if (r.IsMatch(relativePath)) return true;
        foreach (var r in _floating) if (r.IsMatch(relativePath)) return true;
        return false;
    }

    private static Regex ToRegex(string pattern, bool anchoredAtRoot)
    {
        var sb = new StringBuilder();
        // A floating pattern starts at the root or right after a separator.
        sb.Append(anchoredAtRoot ? "^" : @"(?:^|(?<=\\))");
        foreach (var ch in pattern.TrimEnd('\\'))
        {
            sb.Append(ch switch
            {
                '*' => ".*",
                '?' => ".",
                _ => Regex.Escape(ch.ToString()),
            });
        }
        // Match the item itself or anything below it.
        sb.Append(@"(?:$|\\)");
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }
}
