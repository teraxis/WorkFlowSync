using System.Security.Cryptography;
using System.Text;
using WorkFlowSync.Core.Model;
using WorkFlowSync.Core.Scanning;

namespace WorkFlowSync.Core.Planning;

/// <summary>
/// Deterministic path shortening for Windows and cloud sync roots (e.g. OneDrive / SharePoint)
/// where the combined path length exceeds OS or cloud client limits (MAX_PATH / 260 chars).
/// Shortens file names first, then directory segments if necessary, appending a deterministic
/// hash tag to avoid collisions and preserve file tracking across repeated passes.
/// </summary>
public static class PathShortener
{
    public const int DefaultMaxPathLength = 240;
    public const int MinMaxPathLength = 80;
    public const int MaxMaxPathLength = 1000;

    /// <summary>
    /// Computes an 8-character lowercase hexadecimal hash of the relative path
    /// (normalized to lowercase invariant and backslashes) for collision-free shortening.
    /// </summary>
    public static string ComputeHash(string path, int length = 8)
    {
        var normalized = (path ?? "").Trim().Replace('/', '\\').ToLowerInvariant();
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        var hex = Convert.ToHexString(bytes).ToLowerInvariant();
        return hex[..Math.Clamp(length, 4, hex.Length)];
    }

    /// <summary>
    /// Shortens a relative file path so that <c>Path.Combine(targetRoot, shortenedPath).Length &lt;= maxPathLength</c>.
    /// Preserves file extension. Shortens base file name first; if that is not enough, shortens parent directories.
    /// </summary>
    public static string ShortenFilePath(string targetRoot, string relativePath, int maxPathLength)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return relativePath;
        var root = (targetRoot ?? "").TrimEnd('\\', '/');
        var fullTarget = string.IsNullOrEmpty(root) ? relativePath : Path.Combine(root, relativePath);
        if (fullTarget.Length <= maxPathLength) return relativePath;

        var dir = Path.GetDirectoryName(relativePath) ?? "";
        var fileName = Path.GetFileName(relativePath);
        var ext = Path.GetExtension(fileName);
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrEmpty(baseName))
        {
            baseName = fileName;
            ext = "";
        }

        var hash = ComputeHash(relativePath, 8);
        var hashTag = "_#" + hash; // 10 chars
        var excess = fullTarget.Length - maxPathLength;

        // Try shortening only the file name first (leaving at least 3 chars of base name)
        var maxBaseLen = baseName.Length - excess - hashTag.Length;
        if (maxBaseLen >= 3)
        {
            var shortenedBase = baseName[..maxBaseLen].TrimEnd(' ', '.', '_');
            if (shortenedBase.Length == 0) shortenedBase = baseName[..Math.Min(3, baseName.Length)];
            var newFileName = shortenedBase + hashTag + ext;
            return string.IsNullOrEmpty(dir) ? newFileName : Path.Combine(dir, newFileName);
        }

        // If file name shortening alone is not enough, shorten directory segments first
        var targetDir = dir;
        if (!string.IsNullOrEmpty(dir))
        {
            // Reserve at least 3 chars base + 10 chars hash + ext.Length for file name
            var minFileNameLen = 3 + hashTag.Length + ext.Length;
            var maxDirFullLen = maxPathLength - minFileNameLen - 1; // 1 for separating backslash
            targetDir = ShortenDirectoryPath(root, dir, maxDirFullLen);
        }

        // Recompute available characters for the file name under targetDir
        var currentDirFull = string.IsNullOrEmpty(root)
            ? targetDir
            : (string.IsNullOrEmpty(targetDir) ? root : Path.Combine(root, targetDir));
        var availableForFileName = maxPathLength - currentDirFull.Length - 1;

        var availableForBase = availableForFileName - hashTag.Length - ext.Length;
        if (availableForBase >= 1)
        {
            var keepLen = Math.Min(baseName.Length, availableForBase);
            var shortenedBase = baseName[..keepLen].TrimEnd(' ', '.', '_');
            if (shortenedBase.Length == 0) shortenedBase = baseName[..1];
            var newFileName = shortenedBase + hashTag + ext;
            return string.IsNullOrEmpty(targetDir) ? newFileName : Path.Combine(targetDir, newFileName);
        }

        // Extreme length constraint: drop hashTag prefix if strictly necessary or truncate ext
        var emergencyFileName = baseName[..Math.Min(baseName.Length, Math.Max(1, availableForFileName - 6))] + "_#" + hash[..4];
        return string.IsNullOrEmpty(targetDir) ? emergencyFileName : Path.Combine(targetDir, emergencyFileName);
    }

    /// <summary>
    /// Shortens a relative directory path so that <c>Path.Combine(targetRoot, shortenedDir).Length &lt;= maxDirectoryLength</c>.
    /// Shortens individual directory segments starting from the deepest subfolder.
    /// </summary>
    public static string ShortenDirectoryPath(string targetRoot, string directoryRelativePath, int maxDirectoryLength)
    {
        if (string.IsNullOrWhiteSpace(directoryRelativePath)) return directoryRelativePath;
        var root = (targetRoot ?? "").TrimEnd('\\', '/');
        var fullTarget = string.IsNullOrEmpty(root) ? directoryRelativePath : Path.Combine(root, directoryRelativePath);
        if (fullTarget.Length <= maxDirectoryLength) return directoryRelativePath;

        var segments = directoryRelativePath.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return directoryRelativePath;

        var excess = fullTarget.Length - maxDirectoryLength;

        // Cumulative path to compute stable segment hashes
        var cumulative = new string[segments.Length];
        for (var i = 0; i < segments.Length; i++)
        {
            cumulative[i] = i == 0 ? segments[0] : cumulative[i - 1] + "\\" + segments[i];
        }

        // First pass: shorten segments longer than 12 chars from deepest to shallowest
        for (var i = segments.Length - 1; i >= 0 && excess > 0; i--)
        {
            var seg = segments[i];
            if (seg.Length <= 12) continue;

            var segHash = "_#" + ComputeHash(cumulative[i], 6); // 8 chars
            var canReduce = seg.Length - 4 - segHash.Length; // keep at least 4 chars of folder name
            if (canReduce <= 0) continue;

            var toReduce = Math.Min(excess, canReduce);
            var keepLen = seg.Length - toReduce - segHash.Length;
            var newSeg = seg[..keepLen].TrimEnd(' ', '.', '_') + segHash;
            var achieved = seg.Length - newSeg.Length;
            if (achieved > 0)
            {
                segments[i] = newSeg;
                excess -= achieved;
            }
        }

        // Second pass: if still excess, reduce any segment down to 2 chars + hash
        if (excess > 0)
        {
            for (var i = segments.Length - 1; i >= 0 && excess > 0; i--)
            {
                var seg = segments[i];
                if (seg.Length <= 8) continue;

                var segHash = "_#" + ComputeHash(cumulative[i], 4); // 6 chars
                var canReduce = seg.Length - 2 - segHash.Length;
                if (canReduce <= 0) continue;

                var toReduce = Math.Min(excess, canReduce);
                var keepLen = Math.Max(2, seg.Length - toReduce - segHash.Length);
                var newSeg = seg[..keepLen].TrimEnd(' ', '.', '_') + segHash;
                var achieved = seg.Length - newSeg.Length;
                if (achieved > 0)
                {
                    segments[i] = newSeg;
                    excess -= achieved;
                }
            }
        }

        return string.Join("\\", segments);
    }

    /// <summary>
    /// Projects source entries so that long file and directory paths are represented by their shortened
    /// relative target paths. Uses <paramref name="state"/> to preserve existing mappings for already synchronized files.
    /// Produces a target-to-source mapping dictionary for the planner and executor.
    /// </summary>
    public static IReadOnlyDictionary<string, ScanEntry> MapEntries(
        IReadOnlyDictionary<string, ScanEntry> source,
        string targetRoot,
        bool enabled,
        int maxPathLength,
        IReadOnlyDictionary<string, StateEntry>? state,
        Dictionary<string, string>? existingTargetToSourceMap,
        out Dictionary<string, string> targetToSourceMap)
    {
        targetToSourceMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (existingTargetToSourceMap != null)
        {
            foreach (var (k, v) in existingTargetToSourceMap)
            {
                targetToSourceMap[k] = v;
            }
        }

        if (!enabled || source.Count == 0 || maxPathLength <= 0)
        {
            return source;
        }

        // Build reverse lookup from state: remote_id (original source path) -> relativePath (target path)
        var stateRemoteToTarget = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (state != null)
        {
            foreach (var (targetRel, entry) in state)
            {
                if (!string.IsNullOrEmpty(entry.RemoteId))
                {
                    stateRemoteToTarget[entry.RemoteId] = targetRel;
                }
            }
        }

        var dirMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, ScanEntry>(source.Count, StringComparer.OrdinalIgnoreCase);

        // Process directories first, ordered by depth so parent directories are shortened before children
        var ordered = source
            .OrderBy(kvp => kvp.Value.Kind == EntryKind.Directory ? 0 : 1)
            .ThenBy(kvp => kvp.Key.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries).Length)
            .ThenBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var (path, entry) in ordered)
        {
            var originalSourcePath = existingTargetToSourceMap != null && existingTargetToSourceMap.TryGetValue(path, out var orig)
                ? orig
                : path;

            string targetRel;

            // 1. Check if state already has a recorded mapping for this source path
            if (stateRemoteToTarget.TryGetValue(originalSourcePath, out var previouslyMapped))
            {
                targetRel = previouslyMapped;
                if (entry.Kind == EntryKind.Directory)
                {
                    dirMap[path] = targetRel;
                }
            }
            else if (entry.Kind == EntryKind.Directory)
            {
                // Map directory
                var parent = Path.GetDirectoryName(path);
                var baseDir = Path.GetFileName(path);
                string mappedParent = "";
                if (!string.IsNullOrEmpty(parent) && dirMap.TryGetValue(parent, out var mp))
                    mappedParent = mp;
                else if (!string.IsNullOrEmpty(parent))
                    mappedParent = parent;

                var candidate = string.IsNullOrEmpty(mappedParent) ? baseDir : Path.Combine(mappedParent, baseDir);
                var fullTarget = string.IsNullOrEmpty(targetRoot) ? candidate : Path.Combine(targetRoot, candidate);
                if (fullTarget.Length > maxPathLength)
                {
                    targetRel = ShortenDirectoryPath(targetRoot, candidate, maxPathLength);
                }
                else
                {
                    targetRel = candidate;
                }
                dirMap[path] = targetRel;
            }
            else
            {
                // Map file
                var parent = Path.GetDirectoryName(path);
                var fileName = Path.GetFileName(path);
                string mappedParent = "";
                if (!string.IsNullOrEmpty(parent) && dirMap.TryGetValue(parent, out var mp))
                    mappedParent = mp;
                else if (!string.IsNullOrEmpty(parent))
                    mappedParent = parent;

                var candidate = string.IsNullOrEmpty(mappedParent) ? fileName : Path.Combine(mappedParent, fileName);
                var fullTarget = string.IsNullOrEmpty(targetRoot) ? candidate : Path.Combine(targetRoot, candidate);
                if (fullTarget.Length > maxPathLength)
                {
                    targetRel = ShortenFilePath(targetRoot, candidate, maxPathLength);
                }
                else
                {
                    targetRel = candidate;
                }
            }

            if (!string.Equals(targetRel, path, StringComparison.OrdinalIgnoreCase))
            {
                targetToSourceMap[targetRel] = originalSourcePath;
                result[targetRel] = entry with { RelativePath = targetRel };
            }
            else
            {
                if (existingTargetToSourceMap != null && existingTargetToSourceMap.TryGetValue(path, out var origSource))
                {
                    targetToSourceMap[path] = origSource;
                }
                result[path] = entry;
            }
        }

        return result;
    }
}
