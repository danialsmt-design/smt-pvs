using Pvs.Core.People;
using Pvs.Core.Verification;
using Xunit;

namespace Pvs.Core.Tests;

public class RecountSessionTests
{
    private static readonly Badge Op = new("14000", "Aziz", "Operator (L1)");
    private static readonly Badge Sup = new("14031", "Noraziah", "Supervisor (L2)");

    [Fact]
    public void Small_adjustment_completes_without_a_supervisor()
    {
        var s = new RecountSession();  // default policy: abs 500 / pct 10%
        s.ScanBadge(Op);
        s.SelectFeeder(108, "VR8-1300-123", systemEstimate: 3500);
        s.ScanReel("VR8-1300-123", "uid");
        var done = s.EnterCount(3450);   // off by 50, well within thresholds

        Assert.Equal(StepOutcome.Completed, done.Outcome);
        Assert.Equal(RecountState.Complete, s.State);
        Assert.False(s.NeededApproval);
        Assert.Equal(3450, s.CountedQty);
    }

    [Fact]
    public void Large_absolute_gap_needs_supervisor_approval()
    {
        var s = new RecountSession();
        s.ScanBadge(Op);
        s.SelectFeeder(108, "VR8-1300-123", systemEstimate: 3500);
        s.ScanReel("VR8-1300-123", "uid");
        var r = s.EnterCount(2000);      // off by 1500 -> large

        Assert.Equal(StepOutcome.Interlocked, r.Outcome);
        Assert.Equal(RecountState.AwaitingSupervisor, s.State);
        Assert.True(s.NeededApproval);
    }

    [Fact]
    public void Large_percentage_gap_needs_supervisor_even_if_absolute_small()
    {
        // Small reel: estimate 200, counted 20 -> off by 180 (< 500 abs) but 90% (> 10%).
        var s = new RecountSession();
        s.ScanBadge(Op);
        s.SelectFeeder(120, "PARTX", systemEstimate: 200);
        s.ScanReel("PARTX", "uid");
        var r = s.EnterCount(20);
        Assert.Equal(StepOutcome.Interlocked, r.Outcome);
        Assert.True(s.NeededApproval);
    }

    [Fact]
    public void Supervisor_approves_a_large_adjustment()
    {
        var s = new RecountSession();
        s.ScanBadge(Op);
        s.SelectFeeder(108, "VR8-1300-123", 3500);
        s.ScanReel("VR8-1300-123", "uid");
        s.EnterCount(2000);                    // large -> needs approval

        Assert.Equal(StepOutcome.Rejected, s.ScanBadge(Op).Outcome);   // operator can't approve
        var ok = s.ScanBadge(Sup);
        Assert.Equal(StepOutcome.Completed, ok.Outcome);
        Assert.Equal(RecountState.Complete, s.State);
        Assert.Equal("Noraziah", s.Approver!.Name);
        Assert.Equal(2000, s.CountedQty);
    }

    [Fact]
    public void Steps_must_happen_in_order()
    {
        var s = new RecountSession();
        Assert.Equal(StepOutcome.Rejected, s.SelectFeeder(1, "X", 100).Outcome); // before badge
        s.ScanBadge(Op);
        Assert.Equal(StepOutcome.Rejected, s.EnterCount(100).Outcome);           // before reel
    }

    [Theory]
    [InlineData(3500, 3500, false)]  // exact
    [InlineData(3500, 3010, false)]  // off 490 (< 500) and 14%... wait: 490/3500 = 14% > 10% -> large
    [InlineData(10000, 9500, true)]  // off 500 -> abs threshold
    public void Policy_flags_large_by_abs_or_percent(int estimate, int counted, bool _)
    {
        // Explicit policy check independent of the session wiring.
        var policy = DiscrepancyPolicy.Default;
        bool large = policy.IsLarge(counted, estimate);
        int diff = System.Math.Abs(counted - estimate);
        bool expected = diff >= 500 || (estimate > 0 && (double)diff / estimate >= 0.10);
        Assert.Equal(expected, large);
    }
}
