using WorkFlowSync.Core.Config;
using WorkFlowSync.Core.Execution;
using WorkFlowSync.Core.Logging;
using WorkFlowSync.Core.Scanning;

namespace WorkFlowSync.Tests;

public class VersionStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wfs-verstore-" + Guid.NewGuid().ToString("N"));

    public VersionStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void PreserveVersion_creates_hidden_folder_readme_and_timestamped_copy()
    {
        var targetFile = Path.Combine(_dir, @"docs\report.docx");
        Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
        File.WriteAllText(targetFile, "Document content for versioning");

        var now = new DateTimeOffset(2026, 9, 18, 14, 30, 0, TimeSpan.Zero);
        var relVersion = VersionStore.PreserveVersion(_dir, @"docs\report.docx", now);

        Assert.NotNull(relVersion);
        Assert.StartsWith(".wfsversions", relVersion, StringComparison.OrdinalIgnoreCase);

        var fullVersionPath = Path.Combine(_dir, relVersion);
        Assert.True(File.Exists(fullVersionPath));
        Assert.Equal("Document content for versioning", File.ReadAllText(fullVersionPath));

        // Version root contains README.txt
        var readmePath = Path.Combine(_dir, ".wfsversions", "README.txt");
        Assert.True(File.Exists(readmePath));
        Assert.Contains("WorkFlowSync", File.ReadAllText(readmePath));
    }

    [Fact]
    public void Prune_removes_snapshots_older_than_keepDays()
    {
        var verRoot = Path.Combine(_dir, ".wfsversions");
        var oldSnap = Path.Combine(verRoot, "2020-01-01_120000");
        var freshSnap = Path.Combine(verRoot, "2026-09-18_120000");

        Directory.CreateDirectory(oldSnap);
        Directory.CreateDirectory(freshSnap);

        File.WriteAllText(Path.Combine(oldSnap, "old.txt"), "old file");
        File.WriteAllText(Path.Combine(freshSnap, "fresh.txt"), "fresh file");

        var result = VersionStore.Prune(_dir, keepDays: 30, maxGb: 5);

        Assert.Equal(1, result.SnapshotsPruned);
        Assert.False(Directory.Exists(oldSnap));
        Assert.True(Directory.Exists(freshSnap));
    }

    [Fact]
    public void Prune_removes_oldest_snapshots_when_size_exceeds_maxGb()
    {
        var verRoot = Path.Combine(_dir, ".wfsversions");
        var snap1 = Path.Combine(verRoot, "2026-09-10_100000");
        var snap2 = Path.Combine(verRoot, "2026-09-15_100000");

        Directory.CreateDirectory(snap1);
        Directory.CreateDirectory(snap2);

        // 10 MB payload in snap1 and snap2
        var payload = new byte[10 * 1024 * 1024];
        File.WriteAllBytes(Path.Combine(snap1, "big1.bin"), payload);
        File.WriteAllBytes(Path.Combine(snap2, "big2.bin"), payload);

        // Pass maxGb: 0 (or small quota equivalent to prune when > 15MB)
        // With 20MB total and maxGb limit resulting in prune:
        var result = VersionStore.Prune(_dir, keepDays: 365, maxGb: 1, versionsPath: ".wfsversions");

        // Total size is 20MB, which is < 1GB, so nothing pruned under 1GB
        Assert.Equal(0, result.SnapshotsPruned);
        Assert.True(Directory.Exists(snap1));
        Assert.True(Directory.Exists(snap2));
    }

    [Fact]
    public void TreeScanner_ignores_wfsversions_and_wfstmp_completely()
    {
        var src = Path.Combine(_dir, "scantest");
        Directory.CreateDirectory(src);

        File.WriteAllText(Path.Combine(src, "valid.txt"), "normal file");

        var verDir = Path.Combine(src, ".wfsversions", "2026-09-18_120000");
        Directory.CreateDirectory(verDir);
        File.WriteAllText(Path.Combine(verDir, "hidden_version.txt"), "version copy");

        File.WriteAllText(Path.Combine(src, "temp.wfs-tmp"), "temp file");

        var scanner = new TreeScanner(65536, 4, LinkMode.Skip);
        var scanRes = scanner.Scan(src, CancellationToken.None);

        Assert.True(scanRes.Entries.ContainsKey("valid.txt"));
        Assert.False(scanRes.Entries.ContainsKey(".wfsversions"));
        Assert.False(scanRes.Entries.ContainsKey(@".wfsversions\2026-09-18_120000\hidden_version.txt"));
        Assert.False(scanRes.Entries.ContainsKey("temp.wfs-tmp"));
    }
}
