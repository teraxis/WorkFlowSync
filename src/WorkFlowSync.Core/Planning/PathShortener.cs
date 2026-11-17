using System.Text;
using WorkFlowSync.Core.Model;
using WorkFlowSync.Core.Scanning;

namespace WorkFlowSync.Core.Planning;

/// <summary>
/// Deterministic path shortening for Windows and cloud sync roots (e.g. OneDrive / SharePoint)
/// where the combined path length exceeds OS or cloud client limits (MAX_PATH / 260 chars).
/// Shortens long names while retaining readable text. Collisions receive numeric suffixes;
/// recorded target paths preserve file tracking across repeated passes.
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

    /// <summary>Maps a file path using the same readable naming rules as a sync pass.</summary>
    public static string ShortenFilePath(string targetRoot, string relativePath, int maxPathLength)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return relativePath;
        var source = new Dictionary<string, ScanEntry>(StringComparer.OrdinalIgnoreCase)
        {
            [relativePath] = new ScanEntry { RelativePath = relativePath, Kind = EntryKind.File }
        };
        return MapEntries(source, targetRoot, true, maxPathLength, null, null, out _).Keys.Single();
    }

    /// <summary>Maps a directory path using the same readable naming rules as a sync pass.</summary>
    public static string ShortenDirectoryPath(string targetRoot, string directoryRelativePath, int maxDirectoryLength)
    {
        if (string.IsNullOrWhiteSpace(directoryRelativePath)) return directoryRelativePath;
        var source = new Dictionary<string, ScanEntry>(StringComparer.OrdinalIgnoreCase)
        {
            [directoryRelativePath] = new ScanEntry { RelativePath = directoryRelativePath, Kind = EntryKind.Directory }
        };
        var mapped = MapEntries(source, targetRoot, true, maxDirectoryLength, null, null, out _);
        return mapped.Keys.Single();
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
        var nextDirectoryOrdinal = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
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

        // Reserve a minimum readable name for each deeper directory and enough room for the
        // longest file extension in that branch. A fixed allowance can strand a file whose
        // extension alone exceeds it, even when shortening its ancestors would make it fit.
        var requiredTail = sortedDirs.ToDictionary(d => d, _ => 0, StringComparer.OrdinalIgnoreCase);
        var preferredTail = sortedDirs.ToDictionary(d => d, _ => 0, StringComparer.OrdinalIgnoreCase);
        foreach (var (path, entry) in source)
        {
            if (entry.Kind != EntryKind.File) continue;
            var parent = Path.GetDirectoryName(path);
            if (parent is null || !requiredTail.ContainsKey(parent)) continue;
            var extension = Path.GetExtension(Path.GetFileName(path).Trim());
            requiredTail[parent] = Math.Max(requiredTail[parent], 1 + 3 + extension.Length);
            preferredTail[parent] = Math.Max(preferredTail[parent], 1 + 20 + extension.Length);
        }
        foreach (var dir in sortedDirs.AsEnumerable().Reverse())
        {
            var parent = Path.GetDirectoryName(dir);
            if (parent is not null && requiredTail.ContainsKey(parent))
            {
                requiredTail[parent] = Math.Max(requiredTail[parent], 1 + 8 + requiredTail[dir]);
                preferredTail[parent] = Math.Max(preferredTail[parent], 1 + 20 + preferredTail[dir]);
            }
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
                    if (enabled)
                    {
                        var parentLength = Path.Combine(targetRoot, mappedParent).Length;
                        var trimmedLength = baseDir.Trim().Length;
                        var minimumNameLength = trimmedLength == 0 ? 3 : Math.Min(trimmedLength, 8);
                        var minimumBudget = parentLength + 1 + minimumNameLength;
                        var reserved = Math.Max(requiredTail[dir],
                            Math.Min(preferredTail[dir], maxPathLength - minimumBudget));
                        var budget = Math.Clamp(maxPathLength - reserved, minimumBudget, maxPathLength);
                        dirMap[dir] = MapDirectory(targetRoot, candidate, origDir, budget);
                    }
                    else
                    {
                        dirMap[dir] = candidate;
                    }
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
                    var firstOrdinal = nextDirectoryOrdinal.GetValueOrDefault(collision, 2);
                    for (var ordinal = firstOrdinal; ordinal <= sortedDirs.Length + pinnedDirectories.Count + 1; ordinal++)
                    {
                        try
                        {
                            var alternativeParentLength = Path.Combine(targetRoot, mappedParent).Length;
                            var suffixLength = $" ({ordinal})".Length;
                            var alternativeBudget = Math.Min(maxPathLength,
                                Math.Max(maxPathLength - requiredTail[dir], alternativeParentLength + 1 + 3 + suffixLength));
                            var alternative = MapDirectory(targetRoot, candidate, origDir,
                                alternativeBudget, ordinal);
                            if (mappedDirOwners.ContainsKey(alternative)) continue;
                            dirMap[dir] = alternative;
                            nextDirectoryOrdinal[collision] = ordinal + 1;
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
        var nextFileOrdinal = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
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
                    var firstOrdinal = nextFileOrdinal.GetValueOrDefault(targetRel, 2);
                    for (var ordinal = firstOrdinal; ordinal <= source.Count + pinnedTargets.Count + 1; ordinal++)
                    {
                        var alternative = MapFile(targetRoot, candidate, originalSourcePath, maxPathLength,
                            ordinal);
                        if (!OccupiedByOther(alternative, originalSourcePath))
                        {
                            targetRel = alternative;
                            nextFileOrdinal[MapFile(targetRoot, candidate, originalSourcePath, maxPathLength)] = ordinal + 1;
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
        int ordinal = 1)
    {
        var parent = Path.GetDirectoryName(candidate) ?? "";
        var originalName = Path.GetFileName(candidate);
        var name = originalName.Trim();
        var parentLength = Path.Combine(root, parent).Length;
        var normalized = Path.Combine(parent, name);
        if (ordinal == 1 && name.Length > 0 && Encoding.UTF8.GetByteCount(name) <= MaxSegmentUtf8Bytes &&
            Path.Combine(root, normalized).Length <= budget)
            return normalized;
        var suffix = ordinal == 1 ? "" : $" ({ordinal})";
        var allowed = budget - parentLength - 1;
        if (allowed < suffix.Length + 3)
            throw new InvalidOperationException($"Directory cannot fit with a readable name: {sourcePath}");
        var stem = ReadableStem(name, allowed - suffix.Length,
            MaxSegmentUtf8Bytes - Encoding.UTF8.GetByteCount(suffix));
        return Path.Combine(parent, stem + suffix);
    }

    private static string MapFile(string root, string candidate, string sourcePath, int limit,
        int ordinal = 1)
    {
        var parent = Path.GetDirectoryName(candidate) ?? "";
        var name = Path.GetFileName(candidate).Trim();
        if (ordinal == 1 && Path.Combine(root, candidate).Length <= limit &&
            Encoding.UTF8.GetByteCount(name) <= MaxSegmentUtf8Bytes && name.Length > 0)
            return candidate;

        var ext = Path.GetExtension(name);
        var stem = Path.GetFileNameWithoutExtension(name);
        var suffix = ordinal == 1 ? "" : $" ({ordinal})";
        var allowed = limit - Path.Combine(root, parent).Length - (root.Length > 0 || parent.Length > 0 ? 1 : 0);
        if (allowed < suffix.Length + ext.Length + 3)
            throw new InvalidOperationException($"File cannot fit with a readable name: {sourcePath}");
        var shortStem = ReadableStem(stem, allowed - suffix.Length - ext.Length,
            MaxSegmentUtf8Bytes - Encoding.UTF8.GetByteCount(suffix + ext));
        return Path.Combine(parent, shortStem + suffix + ext);
    }

    private static string ReadableStem(string name, int maxChars, int maxBytes)
    {
        var trimmed = name.Trim().TrimEnd('.');
        if (trimmed.Length == 0) trimmed = "_";
        var keep = Math.Min(trimmed.Length, maxChars);
        var stem = TruncateToUtf8Bytes(trimmed[..keep], maxBytes).TrimEnd(' ', '.', '_');
        if (stem.Length < trimmed.Length && maxChars >= 16)
        {
            var lastBreak = trimmed.LastIndexOfAny(new[] { ' ', '_', '-' });
            var lastWord = lastBreak < 0 ? "" : trimmed[(lastBreak + 1)..];
            if (lastWord.Length >= 4 && lastWord.Length <= maxChars / 2)
            {
                var prefixLength = Math.Min(trimmed.Length, maxChars - lastWord.Length - 1);
                var prefix = trimmed[..prefixLength].TrimEnd(' ', '.', '_', '-');
                var withEnding = prefix + "…" + lastWord;
                if (prefix.Length >= 4 && Encoding.UTF8.GetByteCount(withEnding) <= maxBytes)
                    stem = withEnding;
            }
        }
        if (stem.Length == 0)
            throw new InvalidOperationException($"Name cannot fit: {name}");
        return stem;
    }
}
