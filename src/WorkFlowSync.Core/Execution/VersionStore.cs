using System.Globalization;
using WorkFlowSync.Core.Logging;

namespace WorkFlowSync.Core.Execution;

public sealed record VersionPruneResult
{
    public int SnapshotsPruned { get; set; }
    public long BytesPruned { get; set; }
}

/// <summary>
/// Manages file version snapshots in <c>.wfsversions</c> inside the target folder (docs/plan-etap6.md §5.6 & §13.3).
/// Creates hidden timestamped snapshots before reverts or irrecoverable deletions, and prunes old snapshots by age and GB quota.
/// </summary>
public static class VersionStore
{
    public const string DefaultFolderName = ".wfsversions";

    private const string ReadmeText = """
        Це резервні копії файлів, які WorkFlowSync зберегла перед відкатом або видаленням.
        Вони зберігаються локально у вигляді часових знімків і можуть бути безпечно видалені
        ручно або автоматично в процесі ротації (типово через 30 днів чи при перевищенні лиміту 5 ГБ).
        """;

    /// <summary>
    /// Preserves a copy of the target file into <c>\.wfsversions\&lt;yyyy-MM-dd_HHmmss&gt;\&lt;relative_path&gt;</c>.
    /// Creates the hidden version root and README.txt on demand. Returns the relative version path.
    /// </summary>
    public static string? PreserveVersion(string targetRoot, string relativePath, DateTimeOffset timestamp, string versionsPath = DefaultFolderName)
    {
        var root = Path.GetFullPath(targetRoot).TrimEnd('\\', '/');
        var sourceFile = Path.Combine(root, relativePath);

        if (!File.Exists(sourceFile)) return null;

        var versionFolder = Path.Combine(root, versionsPath);
        EnsureVersionRootCreated(versionFolder);

        var snapshotName = timestamp.ToUniversalTime().ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture);
        var snapshotDir = Path.Combine(versionFolder, snapshotName);
        var versionTargetFile = Path.Combine(snapshotDir, relativePath);

        var targetDir = Path.GetDirectoryName(versionTargetFile);
        if (!string.IsNullOrEmpty(targetDir)) Directory.CreateDirectory(targetDir);

        File.Copy(sourceFile, versionTargetFile, overwrite: true);

        // Return relative path from target root (e.g. .wfsversions\2026-09-18_120000\doc.docx)
        return Path.Combine(versionsPath, snapshotName, relativePath);
    }

    /// <summary>
    /// Prunes whole timestamp directories older than <paramref name="keepDays"/> or exceeding <paramref name="maxGb"/>.
    /// </summary>
    public static VersionPruneResult Prune(string targetRoot, int keepDays = 30, int maxGb = 5, string versionsPath = DefaultFolderName, ISyncLog? log = null)
    {
        var result = new VersionPruneResult();
        var root = Path.GetFullPath(targetRoot).TrimEnd('\\', '/');
        var versionFolder = Path.Combine(root, versionsPath);

        if (!Directory.Exists(versionFolder)) return result;

        var snapshotDirs = Directory.GetDirectories(versionFolder)
            .Select(d => new { Path = d, Name = Path.GetFileName(d), Date = TryParseSnapshotDate(Path.GetFileName(d)) })
            .Where(x => x.Date.HasValue)
            .OrderBy(x => x.Date!.Value)
            .ToList();

        if (snapshotDirs.Count == 0) return result;

        var cutoff = DateTimeOffset.UtcNow.AddDays(-Math.Max(1, keepDays));
        var maxBytes = (long)Math.Max(1, maxGb) * 1024L * 1024L * 1024L;

        var activeSnapshots = new List<(string DirPath, DateTimeOffset Date, long SizeBytes)>();

        // 1. Prune snapshots older than keepDays
        foreach (var snap in snapshotDirs)
        {
            var size = CalculateDirectorySize(snap.Path);
            if (snap.Date!.Value < cutoff)
            {
                DeleteSnapshotDirectory(snap.Path);
                result.SnapshotsPruned++;
                result.BytesPruned += size;
            }
            else
            {
                activeSnapshots.Add((snap.Path, snap.Date.Value, size));
            }
        }

        // 2. Prune oldest remaining snapshots if total size exceeds maxGb
        var totalSize = activeSnapshots.Sum(s => s.SizeBytes);
        foreach (var snap in activeSnapshots.OrderBy(s => s.Date))
        {
            if (totalSize <= maxBytes) break;

            DeleteSnapshotDirectory(snap.DirPath);
            result.SnapshotsPruned++;
            result.BytesPruned += snap.SizeBytes;
            totalSize -= snap.SizeBytes;
        }

        if (result.SnapshotsPruned > 0 && log is not null)
        {
            log.Info($"versions pruned={result.SnapshotsPruned} bytes={result.BytesPruned}");
        }

        return result;
    }

    private static void EnsureVersionRootCreated(string versionFolder)
    {
        if (!Directory.Exists(versionFolder))
        {
            var info = Directory.CreateDirectory(versionFolder);
            if (OperatingSystem.IsWindows())
            {
                try { File.SetAttributes(versionFolder, info.Attributes | FileAttributes.Hidden); } catch { }
            }
        }

        var readmePath = Path.Combine(versionFolder, "README.txt");
        if (!File.Exists(readmePath))
        {
            try { File.WriteAllText(readmePath, ReadmeText + Environment.NewLine); } catch { }
        }
    }

    private static DateTimeOffset? TryParseSnapshotDate(string dirName)
    {
        if (DateTimeOffset.TryParseExact(dirName, "yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt))
        {
            return dt;
        }
        return null;
    }

    private static long CalculateDirectorySize(string dirPath)
    {
        try
        {
            return new DirectoryInfo(dirPath)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(f => f.Length);
        }
        catch
        {
            return 0;
        }
    }

    private static void DeleteSnapshotDirectory(string dirPath)
    {
        try
        {
            if (Directory.Exists(dirPath))
            {
                Directory.Delete(dirPath, recursive: true);
            }
        }
        catch { }
    }
}
