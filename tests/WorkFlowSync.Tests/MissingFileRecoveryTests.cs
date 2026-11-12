using WorkFlowSync.Core.Config;
using WorkFlowSync.Core.Execution;
using WorkFlowSync.Core.Logging;
using WorkFlowSync.Core.Model;
using WorkFlowSync.Core.Scanning;
using WorkFlowSync.Core.State;

namespace WorkFlowSync.Tests;

public sealed class MissingFileRecoveryTests
{
    [Fact]
    public void Audit_distinguishes_missing_deleted_excluded_and_target_only_without_mutation()
    {
        var now = DateTimeOffset.UtcNow;
        var pair = new FolderPair { Name = "p", Source = @"C:\source", Target = @"C:\target" };
        var source = new Dictionary<string, ScanEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in new[] { "active.txt", "deleted.txt", "new.txt", "old.txt" })
            source[path] = new ScanEntry { RelativePath = path, Kind = EntryKind.File, Size = 4, MtimeUtc = now };
        var target = new Dictionary<string, ScanEntry>(StringComparer.OrdinalIgnoreCase)
        {
            ["local-only.txt"] = new() { RelativePath = "local-only.txt", Kind = EntryKind.File, Size = 9, MtimeUtc = now },
        };
        var state = new Dictionary<string, StateEntry>(StringComparer.OrdinalIgnoreCase)
        {
            ["active.txt"] = Row("active.txt", EntryStatus.Active),
            ["deleted.txt"] = Row("deleted.txt", EntryStatus.Tombstone),
            ["old.txt"] = Row("old.txt", EntryStatus.TooOld),
        };
        var plan = MissingFileRecovery.Build(pair, source, target, state);
        Assert.Equal(2, plan.Restorable);
        Assert.Equal(1, plan.Deleted);
        Assert.Equal(1, plan.Excluded);
        Assert.Equal(1, plan.TargetOnlyFiles);
        Assert.Contains(plan.Items, i => i.SourcePath == "active.txt" && i.Kind == MissingFileKind.ActiveMissing);
        Assert.Contains(plan.Items, i => i.SourcePath == "deleted.txt" && i.Kind == MissingFileKind.Tombstone);
        Assert.Contains(plan.Items, i => i.SourcePath == "new.txt" && i.Kind == MissingFileKind.New);

        StateEntry Row(string path, EntryStatus status) => new()
        {
            PairName = "p",
            RelativePath = path,
            TargetPath = path,
            Kind = EntryKind.File,
            Status = status,
            FirstSeenUtc = now,
            LastSeenUtc = now,
        };
    }

    [Fact]
    [Trait("Category", "E2E")]
    public void Restore_requires_deleted_opt_in_and_preserves_target_only_file()
    {
        var root = Path.Combine(Path.GetTempPath(), "wfs-recovery-" + Guid.NewGuid().ToString("N"));
        var sourceDir = Path.Combine(root, "source");
        var targetDir = Path.Combine(root, "target");
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(targetDir);
        try
        {
            File.WriteAllText(Path.Combine(sourceDir, "deleted.txt"), "original");
            File.WriteAllText(Path.Combine(targetDir, "local-only.txt"), "local");
            var now = DateTimeOffset.UtcNow;
            var pair = new FolderPair { Name = "p", Source = sourceDir, Target = targetDir };
            using var store = new StateStore(Path.Combine(root, "state.db"));
            store.Upsert(new[] { new StateEntry
            {
                PairName = "p", RelativePath = "deleted.txt", TargetPath = "deleted.txt",
                Kind = EntryKind.File, Status = EntryStatus.Tombstone,
                FirstSeenUtc = now.AddDays(-1), LastSeenUtc = now,
            } });
            var source = new TreeScanner(4096, 1, LinkMode.Skip).Scan(sourceDir).Entries;
            var target = new TreeScanner(4096, 1, LinkMode.Skip).Scan(targetDir).Entries;
            var plan = MissingFileRecovery.Build(pair, source, target, store.Load("p"));
            Assert.Equal(1, plan.Deleted);
            var log = new MemorySyncLog();
            MissingFileRecovery.Apply(pair, plan, store, log, restoreDeleted: false);
            Assert.False(File.Exists(Path.Combine(targetDir, "deleted.txt")));
            File.WriteAllText(Path.Combine(targetDir, "deleted.txt"), "local arrival");
            var blocked = MissingFileRecovery.Apply(pair, plan, store, log, restoreDeleted: true);
            Assert.True(blocked.Errors > 0);
            Assert.Equal("local arrival", File.ReadAllText(Path.Combine(targetDir, "deleted.txt")));
            Assert.Equal(EntryStatus.Tombstone, store.Load("p")["deleted.txt"].Status);
            File.Delete(Path.Combine(targetDir, "deleted.txt"));
            MissingFileRecovery.Apply(pair, plan, store, log, restoreDeleted: true);
            Assert.Equal("original", File.ReadAllText(Path.Combine(targetDir, "deleted.txt")));
            Assert.Equal("local", File.ReadAllText(Path.Combine(targetDir, "local-only.txt")));
            Assert.Equal(EntryStatus.Active, store.Load("p")["deleted.txt"].Status);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
