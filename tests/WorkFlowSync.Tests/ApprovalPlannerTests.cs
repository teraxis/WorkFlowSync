using WorkFlowSync.Core.Model;
using WorkFlowSync.Core.Planning;
using WorkFlowSync.Core.Scanning;

namespace WorkFlowSync.Tests;

public class ApprovalPlannerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Frozen_file_path_generates_no_actions_and_increments_AwaitingApproval()
    {
        var frozen = new FrozenPaths(new[] { @"docs\frozen.txt" }, Enumerable.Empty<string>());
        var planner = new TwoWayPlanner("pair1", T0, scope: null, frozenPaths: frozen);

        var sideA = new Dictionary<string, ScanEntry>(StringComparer.OrdinalIgnoreCase)
        {
            ["docs\\frozen.txt"] = new ScanEntry { RelativePath = @"docs\frozen.txt", Kind = EntryKind.File, Size = 10, MtimeUtc = T0 },
            ["normal.txt"] = new ScanEntry { RelativePath = @"normal.txt", Kind = EntryKind.File, Size = 20, MtimeUtc = T0 },
        };
        var sideB = new Dictionary<string, ScanEntry>(StringComparer.OrdinalIgnoreCase);
        var state = new Dictionary<string, StateEntry>(StringComparer.OrdinalIgnoreCase);

        var plan = planner.Plan(sideA, sideB, state);

        Assert.Equal(1, plan.Stats.AwaitingApproval);
        Assert.Equal(1, plan.Stats.New); // normal.txt is new
        Assert.DoesNotContain(plan.Actions, a => a.RelativePath.Equals(@"docs\frozen.txt", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.Actions, a => a.RelativePath.Equals(@"normal.txt", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Frozen_directory_freezes_all_its_descendants()
    {
        var frozen = new FrozenPaths(Enumerable.Empty<string>(), new[] { @"frozen_dir" });
        var planner = new TwoWayPlanner("pair1", T0, scope: null, frozenPaths: frozen);

        var sideA = new Dictionary<string, ScanEntry>(StringComparer.OrdinalIgnoreCase);
        var sideB = new Dictionary<string, ScanEntry>(StringComparer.OrdinalIgnoreCase)
        {
            [@"frozen_dir"] = new ScanEntry { RelativePath = @"frozen_dir", Kind = EntryKind.Directory, MtimeUtc = T0 },
            [@"frozen_dir\child.txt"] = new ScanEntry { RelativePath = @"frozen_dir\child.txt", Kind = EntryKind.File, Size = 100, MtimeUtc = T0 },
            [@"other_dir\file.txt"] = new ScanEntry { RelativePath = @"other_dir\file.txt", Kind = EntryKind.File, Size = 50, MtimeUtc = T0 },
        };
        var state = new Dictionary<string, StateEntry>(StringComparer.OrdinalIgnoreCase);

        var plan = planner.Plan(sideA, sideB, state);

        Assert.Equal(2, plan.Stats.AwaitingApproval); // frozen_dir and frozen_dir\child.txt
        Assert.Equal(1, plan.Stats.New); // other_dir\file.txt
    }

    [Fact]
    public void RequireApproval_new_folder_and_file_in_target_adds_to_PendingUpdates_and_no_source_actions()
    {
        var planner = new TwoWayPlanner("pair1", T0, requireApproval: true);

        var sideA = new Dictionary<string, ScanEntry>(StringComparer.OrdinalIgnoreCase);
        var sideB = new Dictionary<string, ScanEntry>(StringComparer.OrdinalIgnoreCase)
        {
            [@"ВКЗ"] = new ScanEntry { RelativePath = @"ВКЗ", Kind = EntryKind.Directory, MtimeUtc = T0 },
            [@"ВКЗ\audit.docx"] = new ScanEntry { RelativePath = @"ВКЗ\audit.docx", Kind = EntryKind.File, Size = 1024, MtimeUtc = T0 },
        };
        var state = new Dictionary<string, StateEntry>(StringComparer.OrdinalIgnoreCase);

        var plan = planner.Plan(sideA, sideB, state);

        Assert.Equal(2, plan.Stats.AwaitingApproval);
        Assert.Equal(2, plan.PendingUpdates.Count);

        var dirPending = plan.PendingUpdates.First(p => p.Path == @"ВКЗ");
        Assert.Equal(PendingChange.Added, dirPending.Change);
        Assert.Equal(EntryKind.Directory, dirPending.Kind);

        var filePending = plan.PendingUpdates.First(p => p.Path == @"ВКЗ\audit.docx");
        Assert.Equal(PendingChange.Added, filePending.Change);
        Assert.Equal(EntryKind.File, filePending.Kind);
        Assert.Equal(1024, filePending.DstSize);

        // Crucial invariant: no actions writing to Source (Side A) without human approval!
        Assert.DoesNotContain(plan.Actions, a => a.Side == SyncSide.Source);
    }

    [Fact]
    public void RequireApproval_modified_file_in_target_adds_to_PendingUpdates()
    {
        var planner = new TwoWayPlanner("pair1", T0, requireApproval: true);

        var sideA = new Dictionary<string, ScanEntry>(StringComparer.OrdinalIgnoreCase)
        {
            [@"file.txt"] = new ScanEntry { RelativePath = @"file.txt", Kind = EntryKind.File, Size = 100, MtimeUtc = T0 },
        };
        var sideB = new Dictionary<string, ScanEntry>(StringComparer.OrdinalIgnoreCase)
        {
            [@"file.txt"] = new ScanEntry { RelativePath = @"file.txt", Kind = EntryKind.File, Size = 200, MtimeUtc = T0.AddHours(1) },
        };
        var state = new Dictionary<string, StateEntry>(StringComparer.OrdinalIgnoreCase)
        {
            [@"file.txt"] = new StateEntry
            {
                PairName = "pair1",
                RelativePath = @"file.txt",
                Kind = EntryKind.File,
                FirstSeenUtc = T0,
                Status = EntryStatus.Active,
                SourceSize = 100,
                SourceMtimeUtc = T0,
                CopiedSize = 100,
                CopiedMtimeUtc = T0,
            },
        };

        var plan = planner.Plan(sideA, sideB, state);

        Assert.Equal(1, plan.Stats.AwaitingApproval);
        Assert.Single(plan.PendingUpdates);
        var pending = plan.PendingUpdates[0];
        Assert.Equal(@"file.txt", pending.Path);
        Assert.Equal(PendingChange.Modified, pending.Change);
        Assert.Equal(100, pending.SrcSize);
        Assert.Equal(200, pending.DstSize);
        Assert.DoesNotContain(plan.Actions, a => a.Side == SyncSide.Source);
    }

    [Fact]
    public void RequireApproval_deleted_file_in_target_adds_to_PendingUpdates()
    {
        var planner = new TwoWayPlanner("pair1", T0, requireApproval: true);

        var sideA = new Dictionary<string, ScanEntry>(StringComparer.OrdinalIgnoreCase)
        {
            [@"file.txt"] = new ScanEntry { RelativePath = @"file.txt", Kind = EntryKind.File, Size = 100, MtimeUtc = T0 },
        };
        var sideB = new Dictionary<string, ScanEntry>(StringComparer.OrdinalIgnoreCase);
        var state = new Dictionary<string, StateEntry>(StringComparer.OrdinalIgnoreCase)
        {
            [@"file.txt"] = new StateEntry
            {
                PairName = "pair1",
                RelativePath = @"file.txt",
                Kind = EntryKind.File,
                FirstSeenUtc = T0,
                Status = EntryStatus.Active,
                SourceSize = 100,
                SourceMtimeUtc = T0,
                CopiedSize = 100,
                CopiedMtimeUtc = T0,
            },
        };

        var plan = planner.Plan(sideA, sideB, state);

        Assert.Equal(1, plan.Stats.AwaitingApproval);
        Assert.Single(plan.PendingUpdates);
        var pending = plan.PendingUpdates[0];
        Assert.Equal(@"file.txt", pending.Path);
        Assert.Equal(PendingChange.Deleted, pending.Change);
        Assert.DoesNotContain(plan.Actions, a => a.Side == SyncSide.Source);
    }

    [Fact]
    public void RequireApproval_renamed_file_in_target_detects_rename()
    {
        var planner = new TwoWayPlanner("pair1", T0, requireApproval: true);

        var sideA = new Dictionary<string, ScanEntry>(StringComparer.OrdinalIgnoreCase)
        {
            [@"old.txt"] = new ScanEntry { RelativePath = @"old.txt", Kind = EntryKind.File, Size = 100, MtimeUtc = T0 },
        };
        var sideB = new Dictionary<string, ScanEntry>(StringComparer.OrdinalIgnoreCase)
        {
            [@"renamed.txt"] = new ScanEntry { RelativePath = @"renamed.txt", Kind = EntryKind.File, Size = 100, MtimeUtc = T0 },
        };
        var state = new Dictionary<string, StateEntry>(StringComparer.OrdinalIgnoreCase)
        {
            [@"old.txt"] = new StateEntry
            {
                PairName = "pair1",
                RelativePath = @"old.txt",
                Kind = EntryKind.File,
                FirstSeenUtc = T0,
                Status = EntryStatus.Active,
                SourceSize = 100,
                SourceMtimeUtc = T0,
                CopiedSize = 100,
                CopiedMtimeUtc = T0,
            },
        };

        var plan = planner.Plan(sideA, sideB, state);

        Assert.True(plan.Stats.AwaitingApproval >= 1);
        Assert.Single(plan.PendingUpdates);
        var pending = plan.PendingUpdates[0];
        Assert.Equal(@"renamed.txt", pending.Path);
        Assert.Equal(@"old.txt", pending.FromPath);
        Assert.Equal(PendingChange.Renamed, pending.Change);
        Assert.DoesNotContain(plan.Actions, a => a.Side == SyncSide.Source);
    }
}
