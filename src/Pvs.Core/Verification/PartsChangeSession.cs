using Pvs.Core.People;

namespace Pvs.Core.Verification;

public enum ChangeState
{
    /// <summary>Parts-out raised; waiting for the operator to badge in.</summary>
    AwaitingBadge,
    /// <summary>Operator in; waiting for the empty (old) reel scan.</summary>
    AwaitingOldReel,
    /// <summary>Old reel verified; waiting for the new reel scan.</summary>
    AwaitingNewReel,
    /// <summary>New reel matches; waiting for the quantity to be confirmed/keyed.</summary>
    AwaitingQuantity,
    /// <summary>A mismatch stopped the change; a supervisor badge (L2+) must release it.</summary>
    Interlocked,
    /// <summary>Change completed (reel swapped, quantity recorded).</summary>
    Complete,
    /// <summary>Operator skipped — the parts-out was a false alarm (jam/pickup error), no swap.</summary>
    Skipped
}

public enum StepOutcome { Ok, Rejected, Interlocked, Completed, Skipped }

/// <summary>The result of one workflow step. Read the session's own State for where it is now.</summary>
public readonly record struct ChangeStep(StepOutcome Outcome, string Message);

/// <summary>
/// The guarded Mode B parts-change workflow for ONE parts-out, as a state machine. Pure logic:
/// the caller resolves the expected part (from the feeder map), the scanned badges (from Users),
/// and the pre-filled quantity (from StockIns) and passes them in.
///
/// Rules enforced (all validated in tests):
///  - Operator must badge in first (L1+); nothing proceeds without it.
///  - Old reel is verified against the feeder's expected part; new reel must match the old.
///  - ANY mismatch (old-vs-expected OR new-vs-old) STOPS the change (interlock).
///  - Only a supervisor (L2+) can release an interlock; the release + mismatch are recorded.
///  - The operator may Skip (after badging in) when the parts-out was a false alarm.
/// </summary>
public sealed class PartsChangeSession
{
    private ChangeState _returnAfterRelease;
    private ChangeState _rescanState;   // the scan step to return to when a supervisor clears a wrong scan

    public PartsChangeSession(int machine, int feeder, string expectedPart, bool fromPartsOut = false)
    {
        Machine = machine;
        Feeder = feeder;
        ExpectedPart = expectedPart ?? string.Empty;
        FromPartsOut = fromPartsOut;
    }

    public int Machine { get; }
    public int Feeder { get; }
    public string ExpectedPart { get; }
    /// <summary>True if this change was auto-triggered by a machine parts-out (vs a manual/planned change).</summary>
    public bool FromPartsOut { get; }

    public ChangeState State { get; private set; } = ChangeState.AwaitingBadge;
    public Badge? Operator { get; private set; }
    public Badge? ReleasedBy { get; private set; }
    public string? OldReelUid { get; private set; }
    public string? OldReelPart { get; private set; }
    public string? NewReelUid { get; private set; }
    public string? NewReelPart { get; private set; }
    public int? Quantity { get; private set; }
    /// <summary>The new reel's quantity pulled from the parts-control DB (StockOuts), shown for confirmation; null if none.</summary>
    public int? PrefillQuantity { get; private set; }
    public string? InterlockReason { get; private set; }

    /// <summary>Records the qty the parts-control DB holds for the new reel (for the operator to confirm/override).</summary>
    public void SetPrefillQuantity(int? qty) => PrefillQuantity = qty;
    /// <summary>True when the change completed only because a supervisor released a mismatch.</summary>
    public bool WasOverridden { get; private set; }

    public ChangeStep ScanBadge(Badge badge)
    {
        switch (State)
        {
            case ChangeState.AwaitingBadge:
                if (!badge.CanOperate)
                    return Step(StepOutcome.Rejected, $"{badge.Name}: not a recognised operator.");
                Operator = badge;
                State = ChangeState.AwaitingOldReel;
                return Step(StepOutcome.Ok, $"Operator {badge.Name}. Scan the empty reel on feeder {Feeder}.");

            case ChangeState.Interlocked:
                if (!badge.CanReleaseInterlock)
                    return Step(StepOutcome.Rejected, $"{badge.Name}: a supervisor badge is required to clear the wrong scan.");
                // Supervisor CLEARS the wrong scan and requires a re-scan (does NOT accept the mismatch).
                ReleasedBy = badge;
                InterlockReason = null;
                if (_rescanState == ChangeState.AwaitingOldReel) { OldReelUid = null; OldReelPart = null; }
                else if (_rescanState == ChangeState.AwaitingNewReel) { NewReelUid = null; NewReelPart = null; }
                State = _rescanState;
                return Step(StepOutcome.Ok, $"Cleared by {badge.Name} — " +
                    (_rescanState == ChangeState.AwaitingOldReel ? "scan the EMPTY reel again." : "scan the new reel again."));

            default:
                return Step(StepOutcome.Rejected, "No badge is expected at this step.");
        }
    }

    public ChangeStep ScanReel(string partNumber, string uid)
    {
        partNumber = (partNumber ?? string.Empty).Trim();
        uid = (uid ?? string.Empty).Trim();

        switch (State)
        {
            case ChangeState.AwaitingOldReel:
                OldReelUid = uid;
                OldReelPart = partNumber;
                if (!PartsMatch(partNumber, ExpectedPart))
                {
                    _rescanState = ChangeState.AwaitingOldReel;
                    return Interlock(ChangeState.AwaitingNewReel,
                        $"Old reel is {partNumber}, but feeder {Feeder} expects {ExpectedPart}.");
                }
                State = ChangeState.AwaitingNewReel;
                return Step(StepOutcome.Ok, "Old reel verified. Scan the new reel.");

            case ChangeState.AwaitingNewReel:
                NewReelUid = uid;
                NewReelPart = partNumber;
                if (!PartsMatch(partNumber, OldReelPart ?? ExpectedPart))
                {
                    _rescanState = ChangeState.AwaitingNewReel;
                    return Interlock(ChangeState.AwaitingQuantity,
                        $"New reel is {partNumber}, does not match the removed reel {OldReelPart}.");
                }
                State = ChangeState.AwaitingQuantity;
                return Step(StepOutcome.Ok, "New reel matches. Confirm the quantity.");

            case ChangeState.AwaitingBadge:
                return Step(StepOutcome.Rejected, "Scan your badge first.");

            default:
                return Step(StepOutcome.Rejected, "No reel scan is expected at this step.");
        }
    }

    public ChangeStep EnterQuantity(int qty)
    {
        if (State != ChangeState.AwaitingQuantity)
            return Step(StepOutcome.Rejected, "No quantity is expected at this step.");
        if (qty < 0)
            return Step(StepOutcome.Rejected, "Quantity cannot be negative.");
        Quantity = qty;
        State = ChangeState.Complete;
        return Step(StepOutcome.Completed,
            WasOverridden ? "Change complete (supervisor-released)." : "Change complete.");
    }

    public ChangeStep Skip()
    {
        if (State is not (ChangeState.AwaitingOldReel or ChangeState.AwaitingNewReel))
            return Step(StepOutcome.Rejected, "Badge in before skipping.");
        State = ChangeState.Skipped;
        return Step(StepOutcome.Skipped, $"Parts-out on feeder {Feeder} skipped as a false alarm.");
    }

    private ChangeStep Interlock(ChangeState resumeState, string reason)
    {
        InterlockReason = reason;
        _returnAfterRelease = resumeState;
        State = ChangeState.Interlocked;
        return Step(StepOutcome.Interlocked, reason + " Wrong scan — a supervisor badge is required to clear it, then rescan.");
    }

    private string ResumeHint() => _returnAfterRelease switch
    {
        ChangeState.AwaitingNewReel => "Scan the new reel.",
        ChangeState.AwaitingQuantity => "Confirm the quantity.",
        _ => string.Empty
    };

    // Substitute-aware match: a slash in a BOM/feeder part number lists approved substitutes (see PartNumber).
    private static bool PartsMatch(string a, string b) => PartNumber.Matches(a, b);

    private ChangeStep Step(StepOutcome outcome, string message) => new(outcome, message);
}
