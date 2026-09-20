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
}
