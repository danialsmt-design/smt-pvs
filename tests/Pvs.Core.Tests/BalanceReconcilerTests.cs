using Pvs.Core.Inventory;
using Pvs.Core.Runtime;
using Xunit;

namespace Pvs.Core.Tests;

public class BalanceReconcilerTests
{
    private static readonly DateTime T0 = new(2026, 9, 24, 8, 0, 0);

    [Fact]
    public void L5_M3_F125_case_the_line_clock_re_derives_the_balance_the_silent_machine_lost()
    {
        // Reel loaded at 7,000, list 8 per panel, line completed 867 panels; PVS had deducted only 632 panels' worth.
        int expected = BalanceReconciler.Expected(7000, 8, 867);
        Assert.Equal(64, expected);
        int tol = BalanceReconciler.Tolerance(7000, 8);
        Assert.Equal(70, tol);                                          // 1 % of 7,000 beats 3 panels × 8
        Assert.True(BalanceReconciler.NeedsAdjust(tracked: 1944, expected, tol));
        Assert.False(BalanceReconciler.NeedsAdjust(tracked: 100, expected, tol));
    }

    [Fact]
    public void Expected_never_goes_below_zero_or_above_the_load()
    {
        Assert.Equal(0, BalanceReconciler.Expected(3000, 16, 300));   // over-run: reel must be empty
        Assert.Equal(3000, BalanceReconciler.Expected(3000, 16, 0));
        Assert.Equal(3000, BalanceReconciler.Expected(3000, 16, -5)); // clock went backwards: untouched
        Assert.Equal(0, BalanceReconciler.Expected(0, 16, 50));
    }

    [Fact]
    public void The_list_count_is_always_the_rate_a_learned_rate_is_never_applied()
    {
        Assert.Equal(8, BalanceReconciler.RateFor(8, null));
        var two = new PartCalibration("P", 4.0, 2, 4.0, T0);
        Assert.Equal(8, BalanceReconciler.RateFor(8, two));               // the product's placement count is fixed
        var overCount = new PartCalibration("P", -2.0, 3, -2.0, T0);
        Assert.Equal(8, BalanceReconciler.RateFor(8, overCount));
    }

    [Fact]
    public void Real_rate_from_a_confirmed_exhaust_and_the_calibration_learns_over_count()
    {
        Assert.Equal(7000 / 867.0, BalanceReconciler.RealRate(7000, 867));
        Assert.Null(BalanceReconciler.RealRate(7000, 10));                // too short a run
        var cal = new ExhaustCalibration(alpha: 0.5, minBoards: 20);
        var c1 = cal.RecordRate("WA2-2419-000", loadQty: 3000, panelsSinceLoad: 212, mountedPerBoard: 16, T0);
        Assert.NotNull(c1);
        Assert.True(c1!.LastErrorPerBoard < 0);                           // real 14.15 < list 16 → over-count learned
        Assert.InRange(c1.CorrectedPerBoard(16), 14.0, 14.3);
    }

    [Fact]
    public void Attrition_row_keeps_a_negative_shortage_so_over_count_is_visible()
    {
        var l = new AttritionLedger(2.0, 20);
        var r = l.Evaluate(T0, "L", 1, 112, "WA2-2419-000", "U", 3000, 212, 16, -392);
        Assert.Equal(-392, r.Shortage);
        Assert.True(r.Over);                                              // −13 % is a count error too
        Assert.True(r.Escalate);
    }

    [Fact]
    public void Silent_machine_watch_flags_a_stopped_tally_while_the_line_moves_and_recovers()
    {
        var w = new SilentMachineWatch();
        Assert.Equal(SilentVerdict.Ok, w.Observe(3, 65, 65, true, T0));                    // first sample
        Assert.Equal(SilentVerdict.Ok, w.Observe(3, 70, 65, true, T0.AddMinutes(3)));      // 5 panels: too few to judge
        Assert.Equal(SilentVerdict.WentSilent, w.Observe(3, 85, 65, true, T0.AddMinutes(6)));
        Assert.Equal(15, w.Silent[3].GapPanels);
        Assert.Equal(SilentVerdict.StillSilent, w.Observe(3, 100, 65, true, T0.AddMinutes(9)));
        Assert.Equal(30, w.Silent[3].GapPanels);
        Assert.Equal(SilentVerdict.Recovered, w.Observe(3, 112, 80, true, T0.AddMinutes(12)));
        Assert.Empty(w.Silent);
        Assert.Equal(SilentVerdict.Ok, w.Observe(1, 0, 0, true, T0));
        Assert.Equal(SilentVerdict.Ok, w.Observe(1, 50, 50, true, T0.AddMinutes(3)));      // moving with the line
        Assert.Equal(SilentVerdict.NotEligible, w.Observe(4, 50, 0, false, T0));            // skipped / offline
    }

    [Fact]
    public void Inventory_keeps_the_load_base_across_a_recount()
    {
        var inv = new MachineInventory(1);
        inv.Configure(125, "VS1-9540-005", 8);
        inv.LoadReel(125, "540005-J089%", 7000);
        inv.StampLoad(125, 7000, 1000);
        for (int i = 0; i < 100; i++) inv.OnBoardComplete();
        Assert.Equal(6200, inv.Get(125)!.Remaining);
        inv.SetRemaining(125, 64);                       // reconcile sets the balance...
        inv.StampLoad(125, 7000, 1000);                  // ...and keeps the base
        var f = inv.Get(125)!;
        Assert.Equal(64, f.Remaining);
        Assert.Equal(7000, f.LoadQty);
        Assert.Equal(1000, f.LoadClock);
    }
}
