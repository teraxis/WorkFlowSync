using WorkFlowSync.Core;
using WorkFlowSync.Core.Config;
using WorkFlowSync.Core.Execution;
using WorkFlowSync.Core.Logging;
using WorkFlowSync.Core.Model;
using WorkFlowSync.Core.State;

namespace WorkFlowSync.Tests;

public sealed class DiskQuotaRotatorTests : IDisposable
{
    private const long Gb = 1024L * 1024 * 1024;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wfs-rotation-" + Guid.NewGuid().ToString("N"));
    private readonly string _target;
    private readonly string _db;

    public DiskQuotaRotatorTests()
    {
        _target = Path.Combine(_root, "target");
        _db = Path.Combine(_root, "state.db");
        Directory.CreateDirectory(_target);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Deletes_oldest_verified_copy_permanently_and_tombstones_it()
    {
        var old = Write("old.txt", "old", new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));
        var newer = Write("new.txt", "new", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var rows = new Dictionary<string, StateEntry>(StringComparer.OrdinalIgnoreCase)
        {
            ["old.txt"] = Row("old.txt", old, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            ["new.txt"] = Row("new.txt", newer, new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero)),
        };
        using var store = new StateStore(_db);
        store.Upsert(rows.Values);
        var platform = new FakePlatform(CloudRootStatus.NotCloud,
            new CloudDiskSpace(10, 100), new CloudDiskSpace(80, 100));

        var result = new DiskQuotaRotator(platform, new MemorySyncLog())
            .Rotate("p", _target, 50, rows, store);

        Assert.True(result.ReserveSatisfied);
        Assert.Equal(1, result.DeletedFiles);
        Assert.False(File.Exists(Path.Combine(_target, "old.txt")));
        Assert.True(File.Exists(Path.Combine(_target, "new.txt")));
        Assert.Equal(EntryStatus.Tombstone, store.Load("p")["old.txt"].Status);
    }

    [Theory]
    [InlineData(CloudRootStatus.Cloud)]
    [InlineData(CloudRootStatus.Unknown)]
    public void Rotates_cloud_or_indeterminate_targets_when_explicitly_enabled(CloudRootStatus status)
    {
        var info = Write("keep.txt", "x", DateTime.UtcNow.AddDays(-1));
        var rows = new Dictionary<string, StateEntry>(StringComparer.OrdinalIgnoreCase)
        {
            ["keep.txt"] = Row("keep.txt", info, DateTimeOffset.UtcNow.AddDays(-10)),
        };
        using var store = new StateStore(_db);
        store.Upsert(rows.Values);

        var result = new DiskQuotaRotator(new FakePlatform(status,
                new CloudDiskSpace(1, 100), new CloudDiskSpace(80, 100)), new MemorySyncLog())
            .Rotate("p", _target, 50, rows, store);

        Assert.True(result.ReserveSatisfied);
        Assert.Equal(1, result.DeletedFiles);
        Assert.False(File.Exists(Path.Combine(_target, "keep.txt")));
        Assert.Equal(EntryStatus.Tombstone, store.Load("p")["keep.txt"].Status);
    }

    [Fact]
    public void Never_deletes_a_copy_that_was_modified_after_copying()
    {
        var info = Write("changed.txt", "original", DateTime.UtcNow.AddDays(-2));
        var row = Row("changed.txt", info, DateTimeOffset.UtcNow.AddDays(-30));
        File.AppendAllText(info.FullName, " changed");
        var rows = new Dictionary<string, StateEntry>(StringComparer.OrdinalIgnoreCase) { [row.RelativePath] = row };
        using var store = new StateStore(_db);
        store.Upsert(rows.Values);

        var result = new DiskQuotaRotator(
                new FakePlatform(CloudRootStatus.NotCloud, new CloudDiskSpace(1, 100)), new MemorySyncLog())
            .Rotate("p", _target, 50, rows, store);

        Assert.Equal(0, result.DeletedFiles);
        Assert.True(File.Exists(info.FullName));
    }

    [Fact]
    public void Equal_copy_times_are_ordered_by_modified_time_then_creation_time()
    {
        var copyTime = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var modified = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        var olderCreation = Write("created-first.txt", "a", modified);
        var newerCreation = Write("created-second.txt", "b", modified);
        File.SetCreationTimeUtc(olderCreation.FullName, new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetCreationTimeUtc(newerCreation.FullName, new DateTime(2025, 2, 1, 0, 0, 0, DateTimeKind.Utc));
        olderCreation.Refresh();
        newerCreation.Refresh();
        var rows = new Dictionary<string, StateEntry>(StringComparer.OrdinalIgnoreCase)
        {
            [olderCreation.Name] = Row(olderCreation.Name, olderCreation, copyTime),
            [newerCreation.Name] = Row(newerCreation.Name, newerCreation, copyTime),
        };
        using var store = new StateStore(_db);
        store.Upsert(rows.Values);
        var platform = new FakePlatform(CloudRootStatus.NotCloud,
            new CloudDiskSpace(10, 100), new CloudDiskSpace(80, 100));

        var result = new DiskQuotaRotator(platform, new MemorySyncLog())
            .Rotate("p", _target, 50, rows, store);

        Assert.Equal(1, result.DeletedFiles);
        Assert.False(File.Exists(olderCreation.FullName));
        Assert.True(File.Exists(newerCreation.FullName));
    }

    [Fact]
    public void Runner_rotates_before_planning_and_does_not_copy_the_tombstoned_file_back()
    {
        var source = Path.Combine(_root, "source");
        Directory.CreateDirectory(source);
        var targetInfo = Write("archive.txt", "data", DateTime.UtcNow.AddDays(-1));
        var sourcePath = Path.Combine(source, targetInfo.Name);
        File.WriteAllText(sourcePath, "data");
        File.SetLastWriteTimeUtc(sourcePath, targetInfo.LastWriteTimeUtc);
        using (var seed = new StateStore(_db))
            seed.Upsert(new[] { Row(targetInfo.Name, targetInfo, DateTimeOffset.UtcNow.AddDays(-20)) });
        var platform = new FakePlatform(CloudRootStatus.NotCloud,
            new CloudDiskSpace(10, 2 * Gb), new CloudDiskSpace(10, 2 * Gb), new CloudDiskSpace(2 * Gb, 2 * Gb));
        var config = new SyncConfig
        {
            StatePath = _db,
            Pairs =
            {
                new FolderPair
                {
                    Name = "p", Source = source, Target = _target,
                    Mode = SyncMode.TwoWay, DiskQuotaEnabled = true, MinFreeSpaceGb = 1, RotateOnLowSpace = true,
                },
            },
        };

        var pass = new SyncRunner(config, Path.Combine(_root, "config.json"), new MemorySyncLog(), platform)
            .Run(dryRun: false);

        var pair = Assert.Single(pass.Pairs);
        Assert.False(pair.Skipped);
        Assert.Equal(1, pair.RotatedFiles);
        Assert.True(File.Exists(sourcePath));
        Assert.False(File.Exists(targetInfo.FullName));
        using var verify = new StateStore(_db);
        Assert.Equal(EntryStatus.Tombstone, verify.Load("p")[targetInfo.Name].Status);
    }

    private FileInfo Write(string name, string content, DateTime modifiedUtc)
    {
        var path = Path.Combine(_target, name);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, modifiedUtc);
        return new FileInfo(path);
    }

    private static StateEntry Row(string relativePath, FileInfo info, DateTimeOffset copiedAt) => new()
    {
        PairName = "p",
        RelativePath = relativePath,
        Kind = EntryKind.File,
        Status = EntryStatus.Active,
        FirstSeenUtc = copiedAt,
        LastSeenUtc = copiedAt,
        SourceSize = info.Length,
        SourceMtimeUtc = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
        CopiedSize = info.Length,
        CopiedMtimeUtc = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
        CopiedAtUtc = copiedAt,
    };

    private sealed class FakePlatform(CloudRootStatus status, params CloudDiskSpace[] spaces) : ICloudFilePlatform
    {
        private readonly Queue<CloudDiskSpace> _spaces = new(spaces);
        private CloudDiskSpace _last = spaces.Last();

        public bool IsSyncRoot(string path) => status == CloudRootStatus.Cloud;
        public CloudRootStatus GetSyncRootStatus(string path) => status;
        public CloudDiskSpace GetDiskSpace(string path)
        {
            if (_spaces.Count > 0) _last = _spaces.Dequeue();
            return _last;
        }
        public void RequestOnlineOnly(string path) { }
    }
}
