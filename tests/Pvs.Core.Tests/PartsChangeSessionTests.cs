using Pvs.Core.People;
using Pvs.Core.Verification;
using Xunit;

namespace Pvs.Core.Tests;

public class PartsChangeSessionTests
{
    private static readonly Badge Op = new("14000", "Aziz", "Operator (L1)");
    private static readonly Badge Sup = new("14031", "Noraziah", "Supervisor (L2)");
    private static readonly Badge Stranger = new("99999", "Visitor", "Contractor");

    private static PartsChangeSession NewSession() => new(machine: 2, feeder: 124, expectedPart: "VC8-8380-106");

    [Fact]
    public void Happy_path_completes_the_swap()
    {
        var s = NewSession();
        Assert.Equal(StepOutcome.Ok, s.ScanBadge(Op).Outcome);
        Assert.Equal(ChangeState.AwaitingOldReel, s.State);

        Assert.Equal(StepOutcome.Ok, s.ScanReel("VC8-8380-106", "old-uid").Outcome);   // old = expected
        Assert.Equal(StepOutcome.Ok, s.ScanReel("VC8-8380-106", "new-uid").Outcome);   // new = old
        var done = s.EnterQuantity(4000);

        Assert.Equal(StepOutcome.Completed, done.Outcome);
        Assert.Equal(ChangeState.Complete, s.State);
        Assert.False(s.WasOverridden);
        Assert.Equal("new-uid", s.NewReelUid);
        Assert.Equal(4000, s.Quantity);
    }

    [Fact]
    public void Nothing_proceeds_without_an_operator_badge()
    {
        var s = NewSession();
        var r = s.ScanReel("VC8-8380-106", "uid");
        Assert.Equal(StepOutcome.Rejected, r.Outcome);
        Assert.Equal(ChangeState.AwaitingBadge, s.State);
    }

    [Fact]
    public void Unknown_person_cannot_badge_in()
    {
        var s = NewSession();
        Assert.Equal(StepOutcome.Rejected, s.ScanBadge(Stranger).Outcome);
        Assert.Equal(ChangeState.AwaitingBadge, s.State);
    }

    [Fact]
    public void Old_reel_not_matching_the_feeder_interlocks()
    {
        var s = NewSession();
        s.ScanBadge(Op);
        var r = s.ScanReel("WRONG-PART", "old-uid");
        Assert.Equal(StepOutcome.Interlocked, r.Outcome);
        Assert.Equal(ChangeState.Interlocked, s.State);
        Assert.Contains("expects VC8-8380-106", s.InterlockReason);
    }

    [Fact]
    public void New_reel_not_matching_old_interlocks()
    {
        var s = NewSession();
        s.ScanBadge(Op);
        s.ScanReel("VC8-8380-106", "old-uid");            // old ok
        var r = s.ScanReel("SOME-OTHER-PART", "new-uid"); // new != old
        Assert.Equal(StepOutcome.Interlocked, r.Outcome);
        Assert.Equal(ChangeState.Interlocked, s.State);
    }

    [Fact]
    public void Operator_badge_cannot_release_an_interlock()
    {
        var s = NewSession();
        s.ScanBadge(Op);
        s.ScanReel("WRONG-PART", "old-uid");   // interlock
        var r = s.ScanBadge(Op);               // operator tries to clear it
        Assert.Equal(StepOutcome.Rejected, r.Outcome);
        Assert.Equal(ChangeState.Interlocked, s.State);
    }

    [Fact]
    public void Supervisor_clears_wrong_new_reel_then_operator_rescans()
    {
        var s = NewSession();
        s.ScanBadge(Op);
        s.ScanReel("VC8-8380-106", "old-uid");   // old ok
        s.ScanReel("MISMATCH", "new-uid");        // new != old -> interlock

        var clr = s.ScanBadge(Sup);               // supervisor CLEARS the wrong scan
        Assert.Equal(StepOutcome.Ok, clr.Outcome);
        Assert.Equal(ChangeState.AwaitingNewReel, s.State);   // back to rescan the new reel
        Assert.Null(s.NewReelPart);                           // wrong scan cleared
        Assert.Equal("Noraziah", s.ReleasedBy!.Name);

        Assert.Equal(StepOutcome.Ok, s.ScanReel("VC8-8380-106", "new-uid2").Outcome);  // correct new reel
        Assert.Equal(ChangeState.AwaitingQuantity, s.State);
        var done = s.EnterQuantity(4000);
        Assert.Equal(StepOutcome.Completed, done.Outcome);
        Assert.False(s.WasOverridden);            // not overridden — the operator rescanned correctly
    }

    [Fact]
    public void Clear_after_old_mismatch_returns_to_rescan_old_reel()
    {
        var s = NewSession();
        s.ScanBadge(Op);
        s.ScanReel("WRONG-PART", "old-uid");   // old != feeder -> interlock
        s.ScanBadge(Sup);                       // supervisor clears the wrong scan
        Assert.Equal(ChangeState.AwaitingOldReel, s.State);  // back to rescan the OLD reel
        Assert.Null(s.OldReelPart);
    }

    [Fact]
    public void Operator_can_skip_a_false_alarm_after_badging_in()
    {
        var s = NewSession();
        s.ScanBadge(Op);
        var r = s.Skip();
        Assert.Equal(StepOutcome.Skipped, r.Outcome);
        Assert.Equal(ChangeState.Skipped, s.State);
        Assert.Equal("Aziz", s.Operator!.Name);   // skip is attributed to the operator
    }

    [Fact]
    public void Cannot_skip_before_badging_in()
    {
        var s = NewSession();
        Assert.Equal(StepOutcome.Rejected, s.Skip().Outcome);
    }
}
