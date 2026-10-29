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
    public const int MaxSegmentUtf8Bytes = 240;

    /// <summary>
    /// Truncates a string so that its UTF-8 byte count does not exceed <paramref name="maxBytes"/>,
    /// without splitting surrogate pairs.
    /// </summary>
    public static string TruncateToUtf8Bytes(string text, int maxBytes)
    {
        if (string.IsNullOrEmpty(text) || maxBytes <= 0) return string.Empty;
        if (Encoding.UTF8.GetByteCount(text) <= maxBytes) return text;

        var len = Math.Min(text.Length, maxBytes);
        while (len > 0 && Encoding.UTF8.GetByteCount(text.AsSpan(0, len)) > maxBytes)
        {
            len--;
        }
        if (len > 0 && char.IsHighSurrogate(text[len - 1]))
        {
            len--;
        }
        return text[..len];
    }

    /// <summary>
    /// Checks whether any segment in the relative path has leading/trailing spaces
    /// or exceeds <see cref="MaxSegmentUtf8Bytes"/> in UTF-8 (which Google DriveFS and Linux/NAS reject).
    /// </summary>
    public static bool HasInvalidOrLongSegment(string? relativePath)
    {
        if (string.IsNullOrEmpty(relativePath)) return false;
        var segments = relativePath.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var seg in segments)
        {
            if (seg.Length > 0 && (seg[0] == ' ' || seg[^1] == ' '))
                return true;
            if (Encoding.UTF8.GetByteCount(seg) > MaxSegmentUtf8Bytes)
                return true;
        }
        return false;
    }

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
        if (fullTarget.Length <= maxPathLength && !HasInvalidOrLongSegment(relativePath)) return relativePath;

        var dir = Path.GetDirectoryName(relativePath) ?? "";
        var fileName = Path.GetFileName(relativePath).Trim();
        var ext = Path.GetExtension(fileName);
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrEmpty(baseName))
        {
            baseName = fileName;
            ext = "";
        }

        if (!string.IsNullOrEmpty(dir) && HasInvalidOrLongSegment(dir))
        {
            dir = ShortenDirectoryPath(root, dir, maxPathLength);
        }

        var candidateRel = string.IsNullOrEmpty(dir) ? fileName : Path.Combine(dir, fileName);
        fullTarget = string.IsNullOrEmpty(root) ? candidateRel : Path.Combine(root, candidateRel);
        if (fullTarget.Length <= maxPathLength && Encoding.UTF8.GetByteCount(fileName) <= MaxSegmentUtf8Bytes)
        {
            return candidateRel;
        }

        var hash = ComputeHash(relativePath, 8);
        var hashTag = "_#" + hash; // 10 chars
        var suffixUtf8Bytes = Encoding.UTF8.GetByteCount(hashTag + ext);
        var maxBaseUtf8Bytes = Math.Max(3, MaxSegmentUtf8Bytes - suffixUtf8Bytes);
        var excess = Math.Max(0, fullTarget.Length - maxPathLength);

        // Try shortening only the file name first (leaving at least 3 chars of base name)
        var maxBaseLen = baseName.Length - excess - (excess > 0 ? hashTag.Length : 0);
        if (maxBaseLen >= 3)
        {
            var shortenedBase = baseName[..Math.Min(baseName.Length, maxBaseLen)];
            shortenedBase = TruncateToUtf8Bytes(shortenedBase, maxBaseUtf8Bytes).TrimEnd(' ', '.', '_');
            if (shortenedBase.Length == 0) shortenedBase = TruncateToUtf8Bytes(baseName, Math.Max(2, maxBaseUtf8Bytes));
            var newFileName = shortenedBase + hashTag + ext;
            var candidate = string.IsNullOrEmpty(dir) ? newFileName : Path.Combine(dir, newFileName);
            var candidateFull = string.IsNullOrEmpty(root) ? candidate : Path.Combine(root, candidate);
            if (candidateFull.Length <= maxPathLength)
            {
                return candidate;
            }
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
            var shortenedBase = TruncateToUtf8Bytes(baseName[..keepLen], maxBaseUtf8Bytes).TrimEnd(' ', '.', '_');
            if (shortenedBase.Length == 0) shortenedBase = baseName[..1];
            var newFileName = shortenedBase + hashTag + ext;
            return string.IsNullOrEmpty(targetDir) ? newFileName : Path.Combine(targetDir, newFileName);
        }

        // Extreme length constraint: drop hashTag prefix if strictly necessary or truncate ext
        var emergencyBase = TruncateToUtf8Bytes(baseName[..Math.Min(baseName.Length, Math.Max(1, availableForFileName - 6))], MaxSegmentUtf8Bytes - 10);
        var emergencyFileName = emergencyBase + "_#" + hash[..4];
        return string.IsNullOrEmpty(targetDir) ? emergencyFileName : Path.Combine(targetDir, emergencyFileName);
    }

    /// <summary>
    /// Shortens a relative directory path so that <c>Path.Combine(targetRoot, shortenedDir).Length &lt;= maxDirectoryLength</c>
    /// and every directory segment fits within <see cref="MaxSegmentUtf8Bytes"/> UTF-8 bytes without leading/trailing spaces.
    /// Shortens individual directory segments starting from the deepest subfolder.
    /// </summary>
    public static string ShortenDirectoryPath(string targetRoot, string directoryRelativePath, int maxDirectoryLength)
    {
        if (string.IsNullOrWhiteSpace(directoryRelativePath)) return directoryRelativePath;
        var root = (targetRoot ?? "").TrimEnd('\\', '/');
        var fullTarget = string.IsNullOrEmpty(root) ? directoryRelativePath : Path.Combine(root, directoryRelativePath);
        if (fullTarget.Length <= maxDirectoryLength && !HasInvalidOrLongSegment(directoryRelativePath)) return directoryRelativePath;

        var segments = directoryRelativePath.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return directoryRelativePath;

        // Cumulative path to compute stable segment hashes
        var cumulative = new string[segments.Length];
        for (var i = 0; i < segments.Length; i++)
        {
            cumulative[i] = i == 0 ? segments[0] : cumulative[i - 1] + "\\" + segments[i];
        }

        // Normalize leading/trailing spaces and enforce UTF-8 byte limit per segment
        for (var i = 0; i < segments.Length; i++)
        {
            var seg = segments[i].Trim();
            if (seg.Length == 0) seg = "_#" + ComputeHash(cumulative[i], 6);

            if (Encoding.UTF8.GetByteCount(seg) > MaxSegmentUtf8Bytes)
            {
                var segHash = "_#" + ComputeHash(cumulative[i], 6); // 8 ASCII bytes
                var maxBaseBytes = Math.Max(4, MaxSegmentUtf8Bytes - segHash.Length);
                var shortenedBase = TruncateToUtf8Bytes(seg, maxBaseBytes).TrimEnd(' ', '.', '_');
                if (shortenedBase.Length == 0) shortenedBase = seg[..1];
                seg = shortenedBase + segHash;
            }
            segments[i] = seg;
        }

        var normalizedRel = string.Join("\\", segments);
        fullTarget = string.IsNullOrEmpty(root) ? normalizedRel : Path.Combine(root, normalizedRel);
        if (fullTarget.Length <= maxDirectoryLength) return normalizedRel;

        var excess = fullTarget.Length - maxDirectoryLength;

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
                targetRel = ShortenDirectoryPath(targetRoot, candidate, maxPathLength);
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
                targetRel = ShortenFilePath(targetRoot, candidate, maxPathLength);
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
