using WorkFlowSync.Core.Execution;
using WorkFlowSync.Core.Logging;
using WorkFlowSync.Core.Model;
using WorkFlowSync.Core.State;

namespace WorkFlowSync.Tests;

public class ApprovalServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wfs-apprservice-" + Guid.NewGuid().ToString("N"));
    private readonly string _src;
    private readonly string _dst;
    private readonly string _dbPath;

    public ApprovalServiceTests()
    {
        _src = Path.Combine(_dir, "src");
        _dst = Path.Combine(_dir, "dst");
        Directory.CreateDirectory(_src);
        Directory.CreateDirectory(_dst);
        _dbPath = Path.Combine(_dir, "state.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Approve_added_file_copies_target_to_source_and_updates_stores()
    {
        var targetFile = Path.Combine(_dst, "new_doc.txt");
        File.WriteAllText(targetFile, "Hello World");
        var now = DateTimeOffset.UtcNow;

        using var store = new StateStore(_dbPath);
        var pendingId = store.Pending.Upsert(new PendingEntry
        {
            Pair = "pair1",
            Path = "new_doc.txt",
            Change = PendingChange.Added,
            Kind = EntryKind.File,
            DstSize = 11,
            DstMtimeUtc = now,
            DetectedUtc = now,
        });

        var service = new ApprovalService(store, new MemorySyncLog());
        var res = await service.ApproveAsync(pendingId, _src, _dst, now);

        Assert.True(res.IsSuccess);
        Assert.Equal(1, res.ApprovedCount);

        // File copied to source
        var sourceFile = Path.Combine(_src, "new_doc.txt");
        Assert.True(File.Exists(sourceFile));
        Assert.Equal("Hello World", File.ReadAllText(sourceFile));

        // StateStore updated to Active
        var state = store.Load("pair1");
        Assert.True(state.ContainsKey("new_doc.txt"));
        Assert.Equal(EntryStatus.Active, state["new_doc.txt"].Status);

        // PendingStore entry resolved
        Assert.Equal(0, store.Pending.CountOpen("pair1"));
    }

    [Fact]
    public async Task Dismiss_added_file_sets_LocalOnly_status_without_file_io()
    {
        var targetFile = Path.Combine(_dst, "local_only.txt");
        File.WriteAllText(targetFile, "Local content");
        var now = DateTimeOffset.UtcNow;

        using var store = new StateStore(_dbPath);
        var pendingId = store.Pending.Upsert(new PendingEntry
        {
            Pair = "pair1",
            Path = "local_only.txt",
            Change = PendingChange.Added,
            Kind = EntryKind.File,
            DstSize = 13,
            DstMtimeUtc = now,
            DetectedUtc = now,
        });

        var service = new ApprovalService(store, new MemorySyncLog());
        var res = await service.DismissAsync(pendingId, _src, _dst, now);

        Assert.True(res.IsSuccess);
        Assert.Equal(1, res.DismissedCount);

        // File in source is NOT created
        Assert.False(File.Exists(Path.Combine(_src, "local_only.txt")));

        // StateStore entry status is LocalOnly
        var state = store.Load("pair1");
        Assert.True(state.ContainsKey("local_only.txt"));
        Assert.Equal(EntryStatus.LocalOnly, state["local_only.txt"].Status);
    }

    [Fact]
    public async Task Revert_modified_file_copies_source_to_target()
    {
        var sourceFile = Path.Combine(_src, "report.docx");
        File.WriteAllText(sourceFile, "Original Version");
        var targetFile = Path.Combine(_dst, "report.docx");
        File.WriteAllText(targetFile, "Modified Version (edited by user)");

        var now = DateTimeOffset.UtcNow;

        using var store = new StateStore(_dbPath);
        var pendingId = store.Pending.Upsert(new PendingEntry
        {
            Pair = "pair1",
            Path = "report.docx",
            Change = PendingChange.Modified,
            Kind = EntryKind.File,
            SrcSize = 16,
            DstSize = 33,
            DetectedUtc = now,
        });

        var service = new ApprovalService(store, new MemorySyncLog());
        var res = await service.RevertAsync(pendingId, _src, _dst, now);

        Assert.True(res.IsSuccess);
        Assert.Equal(1, res.RevertedCount);

        // Target file restored to original content from source
        Assert.Equal("Original Version", File.ReadAllText(targetFile));

        // StateStore entry status is Active
        var state = store.Load("pair1");
        Assert.Equal(EntryStatus.Active, state["report.docx"].Status);
    }

    [Fact]
    public async Task Stale_detection_triggers_if_target_changed_again()
    {
        var targetFile = Path.Combine(_dst, "changing.txt");
        File.WriteAllText(targetFile, "Initial edit");
        var now = DateTimeOffset.UtcNow;

        using var store = new StateStore(_dbPath);
        var pendingId = store.Pending.Upsert(new PendingEntry
        {
            Pair = "pair1",
            Path = "changing.txt",
            Change = PendingChange.Modified,
            Kind = EntryKind.File,
            DstSize = 12, // Recorded size
            DetectedUtc = now.AddMinutes(-5),
        });

        // User edits the file again before approving
        File.WriteAllText(targetFile, "Second edit with different size");

        var service = new ApprovalService(store, new MemorySyncLog());
        var res = await service.ApproveAsync(pendingId, _src, _dst, now);

        Assert.False(res.IsSuccess);
        Assert.Equal(1, res.StaleCount);

        // Old entry marked Stale, new entry created
        var oldEntry = store.Pending.GetById(pendingId);
        Assert.Equal(PendingStatus.Stale, oldEntry!.Status);

        var openEntries = store.Pending.GetOpen("pair1");
        Assert.Single(openEntries);
        Assert.Equal(31, openEntries[0].DstSize); // size of "Second edit with different size"
    }

    [Fact]
    public async Task Unavailable_source_returns_unavailable_count_and_prevents_execution()
    {
        var targetFile = Path.Combine(_dst, "offline.txt");
        File.WriteAllText(targetFile, "Content");
        var now = DateTimeOffset.UtcNow;

        using var store = new StateStore(_dbPath);
        var pendingId = store.Pending.Upsert(new PendingEntry
        {
            Pair = "pair1",
            Path = "offline.txt",
            Change = PendingChange.Added,
            Kind = EntryKind.File,
            DstSize = 7,
            DetectedUtc = now,
        });

        var missingSourceDir = Path.Combine(_dir, "non_existent_share");
        var service = new ApprovalService(store, new MemorySyncLog());
        var res = await service.ApproveAsync(pendingId, missingSourceDir, _dst, now);

        Assert.False(res.IsSuccess);
        Assert.Equal(1, res.UnavailableCount);

        // Pending entry remains open for when the share comes back online
        Assert.Equal(1, store.Pending.CountOpen("pair1"));
    }
}
