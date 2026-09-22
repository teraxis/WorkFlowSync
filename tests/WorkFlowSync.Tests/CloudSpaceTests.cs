using WorkFlowSync.Core;
using WorkFlowSync.Core.Config;
using WorkFlowSync.Core.Execution;
using WorkFlowSync.Core.Logging;
using WorkFlowSync.Core.Model;
using WorkFlowSync.Core.Planning;

namespace WorkFlowSync.Tests;

public sealed class CloudSpaceTests : IDisposable
{
    private const long Gb = 1024L * 1024 * 1024;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wfs-cloud-" + Guid.NewGuid().ToString("N"));
    private readonly string _source;
    private readonly string _target;

    public CloudSpaceTests()
    {
        _source = Path.Combine(_root, "source");
        _target = Path.Combine(_root, "target");
        Directory.CreateDirectory(_source);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Online_only_attributes_set_unpinned_and_clear_pinned_without_losing_other_flags()
    {
        var original = FileAttributes.Archive | FileAttributes.Hidden | CloudFiles.Pinned;

        var changed = CloudFiles.OnlineOnlyAttributes(original);

        Assert.True(changed.HasFlag(CloudFiles.Unpinned));
        Assert.False(changed.HasFlag(CloudFiles.Pinned));
        Assert.True(changed.HasFlag(FileAttributes.Archive));
        Assert.True(changed.HasFlag(FileAttributes.Hidden));
    }

    [Fact]
    public void Ordinary_temp_folder_is_confirmed_as_not_a_cloud_sync_root()
    {
        if (!OperatingSystem.IsWindows()) return;

        Assert.Equal(CloudRootStatus.NotCloud, new WindowsCloudFilePlatform().GetSyncRootStatus(_root));
    }

    [Fact]
    public void Windows_disk_space_query_accepts_a_folder_path()
    {
        if (!OperatingSystem.IsWindows()) return;

        var space = new WindowsCloudFilePlatform().GetDiskSpace(_root);

        Assert.True(space.AvailableBytes > 0);
        Assert.True(space.TotalBytes >= space.AvailableBytes);
    }

    [Fact]
    public void Enabled_pair_requests_online_only_for_every_successful_target_copy()
    {
        WriteSource("a.txt");
        var platform = new FakeCloudPlatform(new CloudDiskSpace(10 * Gb, 20 * Gb));
        var executor = Executor(platform);

        var result = executor.Execute(Plan("a.txt"), "[p]");

        Assert.Equal(1, result.FilesCopied);
        Assert.Equal(1, result.OnlineOnlyRequested);
        Assert.Equal(Path.Combine(_target, "a.txt"), Assert.Single(platform.Requests));
        Assert.NotNull(Assert.Single(result.Completed).CopiedAtUtc);
        Assert.True(File.Exists(Path.Combine(_target, "a.txt")));
    }

    [Fact]
    public void Low_space_waits_for_provider_and_checkpoints_completed_rows_before_waiting()
    {
        WriteSource("a.txt");
        WriteSource("b.txt");
        var platform = new FakeCloudPlatform(
            new CloudDiskSpace(10 * Gb, 20 * Gb), // first file fits
            new CloudDiskSpace(Gb / 4, 20 * Gb), // second file must wait
            new CloudDiskSpace(10 * Gb, 20 * Gb));
        var executor = Executor(platform, TimeSpan.Zero);
        var checkpoints = new List<int>();

        var result = executor.Execute(Plan("a.txt", "b.txt"), "[p]", checkpoint: rows => checkpoints.Add(rows.Count));

        Assert.Equal(2, result.FilesCopied);
        Assert.Equal(2, result.OnlineOnlyRequested);
        Assert.Equal(1, result.CloudSpaceWaits);
        Assert.Equal(new[] { 1 }, checkpoints);
        Assert.Equal(1, result.CheckpointedRows);
    }

    [Fact]
    public void Enabled_pair_refuses_to_fill_a_non_cloud_target()
    {
        WriteSource("a.txt");
        var platform = new FakeCloudPlatform(new CloudDiskSpace(10 * Gb, 20 * Gb)) { SyncRoot = false };
        var executor = Executor(platform);

        var result = executor.Execute(Plan("a.txt"), "[p]");

        Assert.Equal(1, result.Errors);
        Assert.Equal(1, result.Skipped);
        Assert.Equal(0, result.FilesCopied);
        Assert.Empty(platform.Requests);
        Assert.False(File.Exists(Path.Combine(_target, "a.txt")));
    }

    [Fact]
    public void Failed_online_only_request_keeps_completed_copy_but_pauses_following_copies()
    {
        WriteSource("a.txt");
        WriteSource("b.txt");
        var platform = new FakeCloudPlatform(new CloudDiskSpace(10 * Gb, 20 * Gb)) { FailRequests = true };
        var executor = Executor(platform);

        var result = executor.Execute(Plan("a.txt", "b.txt"), "[p]");

        Assert.Equal(1, result.FilesCopied);
        Assert.Equal(1, result.Errors);
        Assert.Equal(1, result.Skipped);
        Assert.Single(result.Completed);
        Assert.True(File.Exists(Path.Combine(_target, "a.txt")));
        Assert.False(File.Exists(Path.Combine(_target, "b.txt")));
    }

    [Fact]
    public void Reverse_two_way_copy_does_not_apply_the_target_space_policy()
    {
        Directory.CreateDirectory(_target);
        File.WriteAllText(Path.Combine(_target, "back.txt"), "x");
        var platform = new FakeCloudPlatform(new CloudDiskSpace(10 * Gb, 20 * Gb)) { SyncRoot = false };
        var plan = Plan("back.txt");
        var original = plan.Actions[0];
        plan.Actions[0] = original with { Side = SyncSide.Source };
        var executor = Executor(platform);

        var result = executor.Execute(plan, "[p]");

        Assert.Equal(1, result.FilesCopied);
        Assert.Equal(0, result.Errors);
        Assert.Empty(platform.Requests);
        Assert.True(File.Exists(Path.Combine(_source, "back.txt")));
    }

    [Fact]
    public void Pair_pass_is_skipped_when_it_starts_below_the_configured_reserve()
    {
        WriteSource("a.txt");
        var platform = new FakeCloudPlatform(new CloudDiskSpace(Gb / 4, 20 * Gb));
        var config = new SyncConfig
        {
            StatePath = Path.Combine(_root, "state.db"),
            Pairs =
            {
                new FolderPair
                {
                    Name = "p", Source = _source, Target = _target,
                    DiskQuotaEnabled = true, FreeUpSpaceAfterCopy = true, MinFreeSpaceGb = 1,
                },
            },
        };
        var runner = new SyncRunner(config, Path.Combine(_root, "config.json"), new MemorySyncLog(), platform);

        var pass = runner.Run(dryRun: false);

        var pair = Assert.Single(pass.Pairs);
        Assert.True(pair.Skipped);
        Assert.Equal("target free-space reserve", pair.SkipReason);
        Assert.False(File.Exists(Path.Combine(_target, "a.txt")));
        Assert.Empty(platform.Requests);
    }

    [Fact]
    public void Disk_quota_stops_a_non_cloud_target_without_cloud_offloading()
    {
        WriteSource("a.txt");
        var platform = new FakeCloudPlatform(new CloudDiskSpace(Gb / 4, 20 * Gb)) { SyncRoot = false };
        var executor = new SyncExecutor(_source, _target, new MemorySyncLog(), dryRun: false,
            diskQuotaEnabled: true, freeUpSpaceAfterCopy: false, minFreeSpaceGb: 1, cloudPlatform: platform);

        var result = executor.Execute(Plan("a.txt"), "[p]");

        Assert.Equal(1, result.DiskQuotaStops);
        Assert.Equal(1, result.Skipped);
        Assert.False(File.Exists(Path.Combine(_target, "a.txt")));
    }

    [Fact]
    public void Cloud_offloading_does_not_enable_disk_quota_implicitly()
    {
        WriteSource("a.txt");
        var platform = new FakeCloudPlatform(new CloudDiskSpace(1, 20 * Gb));
        var executor = new SyncExecutor(_source, _target, new MemorySyncLog(), dryRun: false,
            diskQuotaEnabled: false, freeUpSpaceAfterCopy: true, minFreeSpaceGb: 1, cloudPlatform: platform);

        var result = executor.Execute(Plan("a.txt"), "[p]");

        Assert.Equal(1, result.FilesCopied);
        Assert.Equal(1, result.OnlineOnlyRequested);
        Assert.Equal(0, result.CloudSpaceWaits);
    }

    private SyncExecutor Executor(ICloudFilePlatform platform, TimeSpan? pollInterval = null) =>
        new(_source, _target, new MemorySyncLog(), dryRun: false, CloudFileMode.Skip,
            diskQuotaEnabled: true, freeUpSpaceAfterCopy: true, minFreeSpaceGb: 1, cloudPlatform: platform)
        {
            CloudSpacePollInterval = pollInterval ?? TimeSpan.FromSeconds(2),
        };

    private void WriteSource(string name) => File.WriteAllText(Path.Combine(_source, name), "x");

    private static SyncPlan Plan(params string[] names)
    {
        var plan = new SyncPlan();
        foreach (var name in names)
        {
            plan.Actions.Add(new SyncAction(SyncActionKind.CopyFile, name, new StateEntry
            {
                PairName = "p",
                RelativePath = name,
                Kind = EntryKind.File,
                Status = EntryStatus.Active,
                FirstSeenUtc = DateTimeOffset.UtcNow,
                LastSeenUtc = DateTimeOffset.UtcNow,
                SourceSize = 1,
            }));
        }
        return plan;
    }

    private sealed class FakeCloudPlatform(params CloudDiskSpace[] disk) : ICloudFilePlatform
    {
        private readonly Queue<CloudDiskSpace> _disk = new(disk);
        private CloudDiskSpace _last = disk.Last();

        public bool SyncRoot { get; init; } = true;
        public bool FailRequests { get; init; }
        public List<string> Requests { get; } = new();

        public bool IsSyncRoot(string path) => SyncRoot;

        public CloudDiskSpace GetDiskSpace(string path)
        {
            if (_disk.Count > 0) _last = _disk.Dequeue();
            return _last;
        }

        public void RequestOnlineOnly(string path)
        {
            if (FailRequests) throw new IOException("simulated Files On-Demand failure");
            Requests.Add(path);
        }
    }
}
