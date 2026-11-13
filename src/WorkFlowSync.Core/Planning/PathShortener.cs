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
    public const int DefaultMaxPathLength = 259;
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
        if (availableForBase >= 3)
        {
            var keepLen = Math.Min(baseName.Length, availableForBase);
            var shortenedBase = TruncateToUtf8Bytes(baseName[..keepLen], maxBaseUtf8Bytes).TrimEnd(' ', '.', '_');
            if (shortenedBase.Length == 0) shortenedBase = baseName[..1];
            var newFileName = shortenedBase + hashTag + ext;
            return string.IsNullOrEmpty(targetDir) ? newFileName : Path.Combine(targetDir, newFileName);
        }

        throw new InvalidOperationException($"Path cannot fit with an eight-character hash: {relativePath}");
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
                var segHash = "_#" + ComputeHash(cumulative[i], 8);
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

            var segHash = "_#" + ComputeHash(cumulative[i], 8);
            var canReduce = seg.Length - 3 - segHash.Length;
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

        if (excess > 0)
            throw new InvalidOperationException($"Directory cannot fit with eight-character hashes: {directoryRelativePath}");
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
        out Dictionary<string, string> targetToSourceMap,
        Action<string, string>? onConflict = null)
    {
        targetToSourceMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (existingTargetToSourceMap != null)
        {
            foreach (var (k, v) in existingTargetToSourceMap)
            {
                targetToSourceMap[k] = v;
            }
        }

        if (source.Count == 0 || maxPathLength <= 0 ||
            (!enabled && (state is null || state.Count == 0)))
        {
            return source;
        }

        // Every recorded copy keeps its physical target path, including copies made before shortening
        // existed (those rows have no RemoteId). Changing the limit must not remap either kind.
        var stateRemoteToTarget = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pinnedTargets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pinnedDirectories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (state != null)
        {
            foreach (var (targetRel, entry) in state)
            {
                var sourcePath = entry.RemoteId ?? entry.RelativePath;
                var physicalPath = entry.TargetPath ?? targetRel;
                stateRemoteToTarget[sourcePath] = physicalPath;
                pinnedTargets.TryAdd(physicalPath, sourcePath);
                if (entry.Kind == EntryKind.Directory)
                    pinnedDirectories.TryAdd(physicalPath, sourcePath);
            }
        }

        var dirMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var invalidDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var invalidDirReasons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var mappedDirOwners = new Dictionary<string, string>(pinnedDirectories, StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, ScanEntry>(source.Count, StringComparer.OrdinalIgnoreCase);

        // Pre-map all parent directories deterministically from shallowest to deepest
        var allDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, entry) in source)
        {
            if (entry.Kind == EntryKind.Directory)
                allDirs.Add(path);
            var p = Path.GetDirectoryName(path);
            while (!string.IsNullOrEmpty(p))
            {
                allDirs.Add(p);
                p = Path.GetDirectoryName(p);
            }
        }

        var sortedDirs = allDirs
            .OrderBy(d => d.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries).Length)
            .ThenBy(d => d, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d, StringComparer.Ordinal).ToArray();

        // Reserve enough space for the deepest descendant before mapping any directory. Otherwise
        // a directory mapped for a short child can leave no room for a later long file name.
        var requiredTail = sortedDirs.ToDictionary(d => d, _ => 0, StringComparer.OrdinalIgnoreCase);
        foreach (var (path, entry) in source)
        {
            if (entry.Kind == EntryKind.Directory) continue;
            var parent = Path.GetDirectoryName(path);
            if (parent is null || !requiredTail.ContainsKey(parent)) continue;
            var extension = Path.GetExtension(path);
            requiredTail[parent] = Math.Max(requiredTail[parent], 1 + 3 + 2 + 16 + extension.Length);
        }
        foreach (var dir in sortedDirs.AsEnumerable().Reverse())
        {
            var parent = Path.GetDirectoryName(dir);
            if (parent is not null && requiredTail.ContainsKey(parent))
                requiredTail[parent] = Math.Max(requiredTail[parent], 1 + 3 + 2 + 16 + requiredTail[dir]);
        }

        foreach (var dir in sortedDirs)
        {
            var parent = Path.GetDirectoryName(dir);
            if (parent is not null && invalidDirs.Contains(parent))
            {
                invalidDirs.Add(dir);
                invalidDirReasons[dir] = invalidDirReasons[parent];
                continue;
            }
            var origDir = existingTargetToSourceMap != null && existingTargetToSourceMap.TryGetValue(dir, out var od) ? od : dir;
            try
            {
                if (stateRemoteToTarget.TryGetValue(origDir, out var previouslyMappedDir))
                {
                    dirMap[dir] = previouslyMappedDir;
                }
                else
                {
                    var baseDir = Path.GetFileName(dir);
                    string mappedParent = "";
                    if (!string.IsNullOrEmpty(parent) && dirMap.TryGetValue(parent, out var mp))
                        mappedParent = mp;
                    else if (!string.IsNullOrEmpty(parent))
                        mappedParent = parent;

                    var candidate = string.IsNullOrEmpty(mappedParent) ? baseDir : Path.Combine(mappedParent, baseDir);
                    dirMap[dir] = enabled ? MapDirectory(targetRoot, candidate, origDir,
                        maxPathLength - Math.Max(requiredTail[dir], 32)) : candidate;
                }
                if (mappedDirOwners.TryGetValue(dirMap[dir], out var owner) &&
                    !owner.Equals(origDir, StringComparison.OrdinalIgnoreCase))
                {
                    var collision = dirMap[dir];
                    if (stateRemoteToTarget.ContainsKey(origDir))
                        throw new InvalidOperationException(
                            $"Directory shortening collision: {owner} and {origDir} -> {collision}");
                    var mappedParent = parent is not null && dirMap.TryGetValue(parent, out var mp)
                        ? mp : parent ?? "";
                    var candidate = string.IsNullOrEmpty(mappedParent)
                        ? Path.GetFileName(dir) : Path.Combine(mappedParent, Path.GetFileName(dir));
                    foreach (var hashLength in new[] { 12, 16 })
                    {
                        try
                        {
                            var alternative = MapDirectory(targetRoot, candidate, origDir,
                                maxPathLength - Math.Max(requiredTail[dir], 32), hashLength, forceShorten: true);
                            if (mappedDirOwners.ContainsKey(alternative)) continue;
                            dirMap[dir] = alternative;
                            break;
                        }
                        catch (InvalidOperationException) { }
                    }
                    if (mappedDirOwners.ContainsKey(dirMap[dir]))
                        throw new InvalidOperationException(
                            $"Directory shortening collision: {owner} and {origDir} -> {collision}");
                }
                mappedDirOwners[dirMap[dir]] = origDir;
            }
            catch (Exception ex) when (onConflict is not null &&
                                       ex is InvalidOperationException or ArgumentException or PathTooLongException)
            {
                invalidDirs.Add(dir);
                invalidDirReasons[dir] = ex.Message;
            }
        }

        // Process entries ordered by depth
        var ordered = source
            .OrderBy(kvp => kvp.Value.Kind == EntryKind.Directory ? 0 : 1)
            .ThenBy(kvp => kvp.Key.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries).Length)
            .ThenBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
            .ThenBy(kvp => kvp.Key, StringComparer.Ordinal);
        bool OccupiedByOther(string targetPath, string sourcePath) =>
            result.ContainsKey(targetPath) ||
            (pinnedTargets.TryGetValue(targetPath, out var owner) &&
             !owner.Equals(sourcePath, StringComparison.OrdinalIgnoreCase));

        foreach (var (path, entry) in ordered)
        {
            var sourceDir = entry.Kind == EntryKind.Directory ? path : Path.GetDirectoryName(path);
            if (sourceDir is not null && invalidDirs.Contains(sourceDir))
            {
                onConflict?.Invoke(path, invalidDirReasons[sourceDir]);
                continue;
            }
            var originalSourcePath = existingTargetToSourceMap != null && existingTargetToSourceMap.TryGetValue(path, out var orig)
                ? orig
                : path;

            string targetRel;
            try
            {

                // 1. Check if state already has a recorded mapping for this source path
                if (stateRemoteToTarget.TryGetValue(originalSourcePath, out var previouslyMapped))
                {
                    targetRel = previouslyMapped;
                }
                else if (entry.Kind == EntryKind.Directory)
                {
                    targetRel = dirMap.TryGetValue(path, out var mapped) ? mapped : path;
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
                    targetRel = enabled ? MapFile(targetRoot, candidate, originalSourcePath, maxPathLength) : candidate;
                }

                if (OccupiedByOther(targetRel, originalSourcePath) && entry.Kind == EntryKind.File && enabled &&
                    !stateRemoteToTarget.ContainsKey(originalSourcePath))
                {
                    var parent = Path.GetDirectoryName(targetRel) ?? "";
                    var candidate = string.IsNullOrEmpty(parent)
                        ? Path.GetFileName(path) : Path.Combine(parent, Path.GetFileName(path));
                    foreach (var hashLength in new[] { 12, 16, 24, 32, 64 })
                    {
                        var alternative = MapFile(targetRoot, candidate, originalSourcePath, maxPathLength,
                            hashLength, forceShorten: true, exactCaseHash: true);
                        if (!OccupiedByOther(alternative, originalSourcePath))
                        {
                            targetRel = alternative;
                            break;
                        }
                    }
                }
                if (OccupiedByOther(targetRel, originalSourcePath))
                {
                    var otherSource = targetToSourceMap.GetValueOrDefault(targetRel) ??
                        pinnedTargets.GetValueOrDefault(targetRel) ?? result[targetRel].RelativePath;
                    throw new InvalidOperationException(
                        $"Path shortening collision: {otherSource} and {originalSourcePath} -> {targetRel}");
                }
                if (!stateRemoteToTarget.ContainsKey(originalSourcePath) &&
                    (Path.Combine(targetRoot, targetRel).Length > maxPathLength ||
                    targetRel.Split('\\', '/').Any(s => s.EndsWith(' ') || s.EndsWith('.')))
                    )
                    throw new InvalidOperationException($"Invalid mapped target path: {originalSourcePath} -> {targetRel}");
            }
            catch (Exception ex) when (onConflict is not null &&
                                       ex is InvalidOperationException or ArgumentException or PathTooLongException)
            {
                onConflict(path, ex.Message);
                continue;
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

    private static string MapDirectory(string root, string candidate, string sourcePath, int budget,
        int hashLength = 8, bool forceShorten = false)
    {
        var parent = Path.GetDirectoryName(candidate) ?? "";
        var originalName = Path.GetFileName(candidate);
        var name = originalName.Trim();
        var parentLength = Path.Combine(root, parent).Length;
        var normalized = Path.Combine(parent, name);
        if (!forceShorten && name.Length > 0 && Encoding.UTF8.GetByteCount(name) <= MaxSegmentUtf8Bytes &&
            Path.Combine(root, normalized).Length <= budget)
            return normalized;
        if (!forceShorten && Path.Combine(root, candidate).Length <= budget &&
            Encoding.UTF8.GetByteCount(name) <= MaxSegmentUtf8Bytes && name.Length > 0 && name == originalName)
            return candidate;

        var tag = "_#" + (forceShorten
            ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourcePath.Replace('/', '\\'))))
                .ToLowerInvariant()[..hashLength]
            : ComputeHash(sourcePath, hashLength));
        var allowed = budget - parentLength - 1;
        if (allowed < tag.Length + 3)
            throw new InvalidOperationException($"Directory cannot fit with an eight-character hash: {sourcePath}");
        var keep = Math.Max(3, Math.Min(40, allowed - tag.Length));
        var stem = TruncateToUtf8Bytes(name[..Math.Min(name.Length, keep)], MaxSegmentUtf8Bytes - tag.Length)
            .TrimEnd(' ', '.', '_');
        if (stem.Length == 0) stem = "_";
        var mapped = Path.Combine(parent, stem + tag);
        return mapped;
    }

    private static string MapFile(string root, string candidate, string sourcePath, int limit,
        int hashLength = 8, bool forceShorten = false, bool exactCaseHash = false)
    {
        var parent = Path.GetDirectoryName(candidate) ?? "";
        var name = Path.GetFileName(candidate).Trim();
        if (!forceShorten && Path.Combine(root, candidate).Length <= limit &&
            Encoding.UTF8.GetByteCount(name) <= MaxSegmentUtf8Bytes && name.Length > 0)
            return candidate;

        var ext = Path.GetExtension(name);
        var stem = Path.GetFileNameWithoutExtension(name);
        var tag = "_#" + (exactCaseHash
            ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourcePath.Replace('/', '\\'))))
                .ToLowerInvariant()[..hashLength]
            : ComputeHash(sourcePath, hashLength));
        var allowed = limit - Path.Combine(root, parent).Length - (root.Length > 0 || parent.Length > 0 ? 1 : 0);
        if (allowed < tag.Length + ext.Length + 3)
            throw new InvalidOperationException($"File cannot fit with an eight-character hash: {sourcePath}");
        var keep = Math.Max(3, Math.Min(40, allowed - tag.Length - ext.Length));
        var shortStem = TruncateToUtf8Bytes(stem[..Math.Min(stem.Length, keep)],
            MaxSegmentUtf8Bytes - Encoding.UTF8.GetByteCount(tag + ext)).TrimEnd(' ', '.', '_');
        if (shortStem.Length == 0) shortStem = "_";
        var mapped = Path.Combine(parent, shortStem + tag + ext);
        return mapped;
    }
}
