using WorkFlowSync.Core.Planning;

namespace WorkFlowSync.Core.Execution;

/// <summary>
/// The last thing standing between a wrong conclusion and lost files (docs/plan-etap5.md §4.2, §4.5).
///
/// On a root without a Recycle Bin — every network share — a deletion is permanent, and the customer chose
/// to keep it that way (2026-09-15). So a pass that suddenly wants to remove a large share of what it knows
/// is treated as a symptom, not as an instruction: the likely causes are a half-available source, a folder
/// renamed at the far end, or a scan that silently skipped what it could not read. None of them mean the
/// user deleted anything.
///
/// Refusing costs one interval: the next pass sees the same situation and, if it really was a deletion,
/// the numbers will be small enough to go through. Pure and I/O-free, so every threshold is unit-testable.
/// </summary>
public static class DeletionGuard
{
    /// <summary>Below this many removals nothing is ever questioned — ordinary tidying up.</summary>
    public const int AlwaysAllowed = 100;

    /// <summary>Above this share of everything the pair knows, removals are treated as a symptom.</summary>
    public const double SuspiciousShare = 0.20;

    public sealed record Verdict(bool Allowed, int Removals, int KnownEntries, string? Reason)
    {
        /// <summary>Message for the log; null when nothing was held back.</summary>
        public string? Warning => Allowed
            ? null
            : $"{Removals} removals out of {KnownEntries} known entries ({Reason}) — held back. " +
              "If this is correct, the next pass will carry it out once the numbers settle.";
    }

    /// <summary>
    /// Decides whether the removals in a plan may be carried out.
    /// </summary>
    /// <param name="removals">How many items the plan would delete or recycle.</param>
    /// <param name="knownEntries">How many rows the state has for this pair — the scale to judge against.</param>
    public static Verdict Check(int removals, int knownEntries)
    {
        if (removals <= AlwaysAllowed) return new Verdict(true, removals, knownEntries, null);

        // A pair we know nothing about yet cannot be judged by share, only by the absolute count.
        if (knownEntries <= 0) return new Verdict(false, removals, knownEntries, "nothing known about this pair yet");

        var share = (double)removals / knownEntries;
        return share > SuspiciousShare
            ? new Verdict(false, removals, knownEntries, $"{share:P0} of the pair at once")
            : new Verdict(true, removals, knownEntries, null);
    }

    /// <summary>Counts the actions in a plan that remove something from disk.</summary>
    public static int CountRemovals(SyncPlan plan) => plan.Actions.Count(IsRemoval);

    public static bool IsRemoval(SyncAction action) => action.Kind is
        SyncActionKind.DeleteFile or SyncActionKind.DeleteDirectory or
        SyncActionKind.RecycleFile or SyncActionKind.RecycleEmptyDirectory;

    /// <summary>
    /// Strips every removal from the plan, leaving the additions and updates in place: a pass that cannot
    /// be trusted to delete can still be trusted to copy.
    /// </summary>
    public static int StripRemovals(SyncPlan plan)
    {
        var removed = plan.Actions.RemoveAll(IsRemoval);
        plan.Stats.DeferredToFullPass += removed;
        return removed;
    }
}
