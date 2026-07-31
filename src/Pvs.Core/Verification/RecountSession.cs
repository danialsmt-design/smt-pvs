using Pvs.Core.People;

namespace Pvs.Core.Verification;

/// <summary>
/// Decides whether a Mode D re-count differs from the system estimate by enough to need supervisor
/// approval. "Large" = an absolute gap OR a percentage gap, whichever triggers first (the exact
/// thresholds are configurable at setup; these defaults are provisional pending the final call).
/// </summary>
public sealed record DiscrepancyPolicy(int AbsoluteThreshold, double PercentThreshold)
{
    public static DiscrepancyPolicy Default { get; } = new(AbsoluteThreshold: 500, PercentThreshold: 0.10);

    public bool IsLarge(int counted, int estimate)
    {
        int diff = Math.Abs(counted - estimate);
        if (diff >= AbsoluteThreshold) return true;
        if (estimate > 0 && (double)diff / estimate >= PercentThreshold) return true;
        return false;
    }
}

public enum RecountState { AwaitingBadge, AwaitingFeeder, AwaitingReel, AwaitingQuantity, AwaitingSupervisor, Complete }

/// <summary>
/// Mode D — operator-initiated feeder re-count, any time. Operator badges in, picks a feeder, removes
/// the reel and counts it on the component counter, scans part+UID, and keys the counted quantity.
/// A LARGE discrepancy vs the system estimate needs a supervisor (L2+) to approve; small ones are
/// operator-only. The corrected count becomes the new baseline and the reel goes back on the feeder.
/// Pure logic: the caller injects the feeder's expected part + current system estimate and resolves badges.
/// </summary>
public sealed class RecountSession
{
    private readonly DiscrepancyPolicy _policy;

    public RecountSession(DiscrepancyPolicy? policy = null) => _policy = policy ?? DiscrepancyPolicy.Default;

    public RecountState State { get; private set; } = RecountState.AwaitingBadge;
    public Badge? Operator { get; private set; }
    public Badge? Approver { get; private set; }
    public int? Feeder { get; private set; }
    public string? ExpectedPart { get; private set; }
    public string? ScannedPart { get; private set; }
    public string? ReelUid { get; private set; }
    public int SystemEstimate { get; private set; }
    public int? CountedQty { get; private set; }
    public bool NeededApproval { get; private set; }

    public ChangeStep ScanBadge(Badge badge)
    {
        switch (State)
        {
            case RecountState.AwaitingBadge:
                if (!badge.CanOperate)
                    return Step(StepOutcome.Rejected, $"{badge.Name}: not a recognised operator.");
                Operator = badge;
                State = RecountState.AwaitingFeeder;
                return Step(StepOutcome.Ok, $"Operator {badge.Name}. Select the feeder to re-count.");

            case RecountState.AwaitingSupervisor:
                if (!badge.CanReleaseInterlock)
                    return Step(StepOutcome.Rejected, $"{badge.Name}: a supervisor badge is required to approve a large adjustment.");
                Approver = badge;
                State = RecountState.Complete;
                return Step(StepOutcome.Completed, $"Adjustment approved by {badge.Name}. Feeder {Feeder} set to {CountedQty}.");

            default:
                return Step(StepOutcome.Rejected, "No badge is expected at this step.");
        }
    }

    /// <summary>Choose the feeder to re-count; caller supplies its expected part and current estimate.</summary>
    public ChangeStep SelectFeeder(int feeder, string expectedPart, int systemEstimate)
    {
        if (State != RecountState.AwaitingFeeder)
            return Step(StepOutcome.Rejected, "Badge in before selecting a feeder.");
        Feeder = feeder;
        ExpectedPart = expectedPart;
        SystemEstimate = systemEstimate;
        State = RecountState.AwaitingReel;
        return Step(StepOutcome.Ok, $"Feeder {feeder}. Count the reel, then scan its part number and UID.");
    }

    public ChangeStep ScanReel(string partNumber, string uid)
    {
        if (State != RecountState.AwaitingReel)
            return Step(StepOutcome.Rejected, "No reel scan is expected at this step.");
        ScannedPart = (partNumber ?? string.Empty).Trim();
        ReelUid = (uid ?? string.Empty).Trim();
        State = RecountState.AwaitingQuantity;
        return Step(StepOutcome.Ok, "Enter the counted quantity.");
    }

    public ChangeStep EnterCount(int countedQty)
    {
        if (State != RecountState.AwaitingQuantity)
            return Step(StepOutcome.Rejected, "No count is expected at this step.");
        if (countedQty < 0)
            return Step(StepOutcome.Rejected, "Count cannot be negative.");

        CountedQty = countedQty;
        NeededApproval = _policy.IsLarge(countedQty, SystemEstimate);

        if (NeededApproval)
        {
            State = RecountState.AwaitingSupervisor;
            int diff = countedQty - SystemEstimate;
            return Step(StepOutcome.Interlocked,
                $"Counted {countedQty} vs estimate {SystemEstimate} (off by {diff:+#;-#;0}). Supervisor approval required.");
        }

        State = RecountState.Complete;
        return Step(StepOutcome.Completed, $"Feeder {Feeder} set to {countedQty}.");
    }

    private ChangeStep Step(StepOutcome outcome, string message) => new(outcome, message);
}
