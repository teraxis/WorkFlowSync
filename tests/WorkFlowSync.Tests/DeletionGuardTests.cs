using WorkFlowSync.Core.Execution;
using WorkFlowSync.Core.Model;
using WorkFlowSync.Core.Planning;

namespace WorkFlowSync.Tests;

/// <summary>
/// The ceiling on removals (docs/plan-etap5.md §4.2). On a network root a deletion cannot be undone —
/// the customer chose to keep it that way — so a pass that wants to remove a large share of the pair is
/// treated as a symptom (half-available source, a folder renamed at the far end, an unreadable subtree)
/// rather than as an instruction. Refusing costs one interval; being wrong costs the files.
/// </summary>
public class DeletionGuardTests
{
    [Theory]
    [InlineData(0, 1000)]
    [InlineData(1, 1)]                                    // a tiny pair losing its only file is still fine
    [InlineData(DeletionGuard.AlwaysAllowed, 120)]        // at the threshold, not over it
    [InlineData(100, 0)]                                  // nothing known yet, but still within the flat allowance
    public void Ordinary_tidying_up_is_never_questioned(int removals, int known) =>
        Assert.True(DeletionGuard.Check(removals, known).Allowed);

    [Theory]
    [InlineData(201, 1000)]                               // 20.1% — just over the share
    [InlineData(5000, 10000)]
    [InlineData(101, 0)]                                  // over the allowance with nothing to judge against
    public void A_suspicious_number_of_removals_is_held_back(int removals, int known)
    {
        var verdict = DeletionGuard.Check(removals, known);

        Assert.False(verdict.Allowed);
        Assert.NotNull(verdict.Warning);
        Assert.Contains(removals.ToString(), verdict.Warning!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_large_pair_may_still_lose_a_lot_in_absolute_terms()
    {
        // 1000 out of a million is a normal clear-out, not a symptom: the share is what matters at scale.
        Assert.True(DeletionGuard.Check(1_000, 1_000_000).Allowed);
    }

    [Fact]
    public void Holding_back_removals_leaves_the_copying_alone()
    {
        var plan = new SyncPlan();
        plan.Actions.Add(Action(SyncActionKind.CopyFile, "новий.txt"));
        plan.Actions.Add(Action(SyncActionKind.CreateDirectory, "тека"));
        plan.Actions.Add(Action(SyncActionKind.DeleteFile, "зник.txt"));
        plan.Actions.Add(Action(SyncActionKind.RecycleFile, "старий.txt"));
        plan.Actions.Add(Action(SyncActionKind.DeleteDirectory, "стара тека"));
        plan.Actions.Add(Action(SyncActionKind.RecycleEmptyDirectory, "порожня"));

        Assert.Equal(4, DeletionGuard.CountRemovals(plan));
        var stripped = DeletionGuard.StripRemovals(plan);

        Assert.Equal(4, stripped);
        Assert.Equal(4, plan.Stats.DeferredToFullPass);
        Assert.Equal(2, plan.Actions.Count);
        Assert.All(plan.Actions, a => Assert.False(DeletionGuard.IsRemoval(a)));
    }

    [Fact]
    public void The_warning_says_what_was_held_back_and_that_it_is_not_final()
    {
        var verdict = DeletionGuard.Check(500, 1000);

        Assert.Contains("500", verdict.Warning!, StringComparison.Ordinal);
        Assert.Contains("held back", verdict.Warning!, StringComparison.Ordinal);
        Assert.Contains("next pass", verdict.Warning!, StringComparison.Ordinal);
    }

    [Fact]
    public void An_allowed_verdict_has_nothing_to_say()
    {
        Assert.Null(DeletionGuard.Check(3, 100).Warning);
    }

    private static SyncAction Action(SyncActionKind kind, string path) => new(kind, path, new StateEntry
    {
        PairName = "p", RelativePath = path, Kind = EntryKind.File, Status = EntryStatus.Active,
        FirstSeenUtc = DateTimeOffset.UnixEpoch, LastSeenUtc = DateTimeOffset.UnixEpoch,
    });
}
