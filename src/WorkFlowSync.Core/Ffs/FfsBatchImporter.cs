using System.Xml.Linq;
using WorkFlowSync.Core.Config;
using WorkFlowSync.Core.Logging;
using WorkFlowSync.Core.Model;
using WorkFlowSync.Core.State;

namespace WorkFlowSync.Core.Ffs;

/// <summary>Parsed subset of a FreeFileSync .ffs_batch / .ffs_gui file (docs/product/features/ffs-migration.md).</summary>
public sealed class FfsBatch
{
    public sealed record Pair(string Left, string Right, List<string> Excludes);

    public List<string> GlobalExcludes { get; } = new();
    public List<Pair> Pairs { get; } = new();

    public static FfsBatch Load(string path) => Parse(XDocument.Load(path));

    public static FfsBatch Parse(XDocument doc)
    {
        var batch = new FfsBatch();
        var rootEl = doc.Root ?? throw new InvalidDataException("Empty FreeFileSync document.");
        batch.GlobalExcludes.AddRange(ReadExcludes(rootEl.Element("Filter")));
        foreach (var pair in rootEl.Element("FolderPairs")?.Elements("Pair") ?? Enumerable.Empty<XElement>())
        {
            var left = (string?)pair.Element("Left") ?? "";
            var right = (string?)pair.Element("Right") ?? "";
            if (left.Length == 0) continue;
            batch.Pairs.Add(new Pair(left, right, ReadExcludes(pair.Element("Filter")).ToList()));
        }
        return batch;
    }

    private static IEnumerable<string> ReadExcludes(XElement? filter) =>
        filter?.Element("Exclude")?.Elements("Item").Select(i => (i.Value ?? "").Trim()).Where(v => v.Length > 0)
        ?? Enumerable.Empty<string>();
}

public sealed class ImportResult
{
    public string PairName { get; init; } = "";
    public int Patterns { get; set; }
    public int PatternsAlreadyPresent { get; set; }
    public int Tombstones { get; set; }
    public int TombstonesAlreadyPresent { get; set; }
    public int ActiveConverted { get; set; }
    public int Probed { get; set; }
    public List<StateEntry> Rows { get; } = new();
    public List<string> NewPatterns { get; } = new();
}

/// <summary>
/// Turns FreeFileSync exclude items into WorkFlowSync memory: wildcard items become pair excludes, concrete paths become
/// tombstones — so the first WorkFlowSync pass never brings back what the user had removed under FreeFileSync.
/// </summary>
public static class FfsBatchImporter
{
    public static string NormalizeRoot(string p) => Path.GetFullPath(p.Trim()).TrimEnd('\\', '/');

    /// <summary>Finds the config pair whose source equals the FFS left folder; null when there is none.</summary>
    public static FolderPair? MatchPair(SyncConfig config, FfsBatch.Pair ffsPair) =>
        config.Pairs.FirstOrDefault(p => NormalizeRoot(p.Source).Equals(NormalizeRoot(ffsPair.Left), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Computes the import for one pair. Mutates <paramref name="target"/>.Exclude (patterns) and returns the tombstone rows;
    /// the caller persists config and rows (or just reports, for dry-run).
    /// </summary>
    public static ImportResult Prepare(FfsBatch batch, FfsBatch.Pair ffsPair, FolderPair target,
        IReadOnlyDictionary<string, StateEntry> existingState, DateTimeOffset now, bool probeSource, ISyncLog? log = null)
    {
        var result = new ImportResult { PairName = target.Name };
        var items = batch.GlobalExcludes.Concat(ffsPair.Excludes)
            .Select(i => i.Replace('/', '\\'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var existingPatterns = new HashSet<string>(target.Exclude, StringComparer.OrdinalIgnoreCase);
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sourceRoot = NormalizeRoot(target.Source);
        var canProbe = probeSource && Directory.Exists(sourceRoot);
        var i = 0;

        foreach (var item in items)
        {
            if (item.Contains('*') || item.Contains('?'))
            {
                if (existingPatterns.Add(item)) { target.Exclude.Add(item); result.NewPatterns.Add(item); result.Patterns++; }
                else result.PatternsAlreadyPresent++;
                continue;
            }

            var rel = item.TrimStart('\\').TrimEnd('\\');
            if (rel.Length == 0 || !seenPaths.Add(rel)) continue;

            if (existingState.TryGetValue(rel, out var existing))
            {
                if (existing.Status == EntryStatus.Tombstone) { result.TombstonesAlreadyPresent++; continue; }
                result.ActiveConverted++;
                result.Rows.Add(existing with { Status = EntryStatus.Tombstone, StatusChangedUtc = now });
                result.Tombstones++;
                continue;
            }

            var kind = GuessKind(rel, sourceRoot, canProbe, ref result);
            result.Rows.Add(new StateEntry
            {
                PairName = target.Name,
                RelativePath = rel,
                Kind = kind,
                Status = EntryStatus.Tombstone,
                FirstSeenUtc = now,
                LastSeenUtc = now,
                StatusChangedUtc = now,
            });
            result.Tombstones++;
            if (++i % 5000 == 0) log?.Info($"import: {i:N0} items processed…");
        }
        return result;
    }

    private static EntryKind GuessKind(string rel, string sourceRoot, bool canProbe, ref ImportResult result)
    {
        if (canProbe)
        {
            var full = Path.Combine(sourceRoot, rel);
            result.Probed++;
            if (Directory.Exists(full)) return EntryKind.Directory;
            if (File.Exists(full)) return EntryKind.File;
        }
        // Not in the source any more (or not probing): an extension usually means a file.
        return Path.HasExtension(rel) ? EntryKind.File : EntryKind.Directory;
    }
}
