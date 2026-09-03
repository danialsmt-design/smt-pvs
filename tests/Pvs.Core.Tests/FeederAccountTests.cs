using Pvs.Core.Inventory;
using Xunit;

namespace Pvs.Core.Tests;

/// <summary>
/// Tray-feeder accounting: operator keys in what was loaded, the machine consumes per board, and a recount
/// outside 0.2% needs a supervisor. Quantities used here are the real Canon tray sizes (638 / 2,871 pcs).
/// </summary>
public class FeederAccountTests
{
    private static readonly DateTime T0 = new(2026, 8, 7, 14, 0, 0);
    private static FeederAccount Tray(double tol = FeederAccount.DefaultTolerancePct) =>
        new(machine: 3, feeder: 502, part: "YH4-3212-008", tolerancePct: tol);

    [Fact]
    public void Load_adds_to_the_running_total_rather_than_replacing_it()
    {
        var a = Tray();
        a.Load(638, "9999-G071%", "CHIT", T0);
        a.Load(638, "9999-G071%", "CHIT", T0.AddHours(3));   // tray topped up
        Assert.Equal(1276, a.TotalLoaded);
        Assert.Equal(1276, a.Expected);
        Assert.Equal(2, a.Loads.Count);
    }

    [Fact]
    public void Mounting_draws_down_the_expected_remainder()
    {
        var a = Tray();
        a.Load(638, "9999-G071%", "CHIT", T0);
        for (int i = 0; i < 100; i++) a.OnBoardComplete(4);   // 100 panels x 4 per panel
        Assert.Equal(400, a.TotalMounted);
        Assert.Equal(238, a.Expected);
    }

    [Fact]
    public void A_recount_that_matches_is_accepted()
    {
        var a = Tray();
        a.Load(638, "9999-G071%", "CHIT", T0);
        a.AddMounted(400);
        var r = a.Recount(238);
        Assert.Equal(RecountVerdict.Ok, r.Verdict);
        Assert.Equal(0, r.Difference);
    }

    [Fact]
    public void A_shortfall_beyond_tolerance_requires_a_supervisor()
    {
        var a = Tray();
        a.Load(638, "9999-G071%", "CHIT", T0);
        a.AddMounted(400);
        var r = a.Recount(230);              // 8 pcs missing; 0.2% of 638 = 1 pc
        Assert.Equal(RecountVerdict.NeedsSupervisor, r.Verdict);
        Assert.Equal(-8, r.Difference);
        Assert.Contains("short", r.Message);
        Assert.Contains("Supervisor", r.Message);
    }

    [Fact]
    public void Counting_MORE_than_expected_also_escalates()
    {
        // Over is as much a signal as short - usually a load that was never keyed in.
        var a = Tray();
        a.Load(638, "9999-G071%", "CHIT", T0);
        a.AddMounted(400);
        var r = a.Recount(300);
        Assert.Equal(RecountVerdict.NeedsSupervisor, r.Verdict);
        Assert.Equal(62, r.Difference);
        Assert.Contains("over", r.Message);
    }

    // 0.2% of a 638-piece tray is 1 piece, so a single dropped part escalates. That is the configured
    // intent, recorded here so the behaviour is deliberate rather than discovered on the floor.
    [Fact]
    public void On_a_small_tray_the_tolerance_is_a_single_piece()
    {
        var a = Tray();
        a.Load(638, "9999-G071%", "CHIT", T0);
        a.AddMounted(400);
        Assert.Equal(RecountVerdict.Ok, a.Recount(237).Verdict);            // 1 short - inside 1 pc
        Assert.Equal(RecountVerdict.NeedsSupervisor, a.Recount(236).Verdict); // 2 short - outside
    }

    [Fact]
    public void On_a_larger_tray_the_tolerance_scales_with_the_percentage()
    {
        var a = new FeederAccount(4, 501, "YH4-3216-008");
        a.Load(2871, "9999-G071%", "CHIT", T0);   // 0.2% = 5 pcs
        a.AddMounted(1000);
        Assert.Equal(RecountVerdict.Ok, a.Recount(1866).Verdict);             // 5 short
        Assert.Equal(RecountVerdict.NeedsSupervisor, a.Recount(1865).Verdict); // 6 short
    }

    [Fact]
    public void A_missed_key_in_shows_up_as_a_negative_expected_and_is_not_hidden()
    {
        // Operator loaded a tray but never keyed it. Mounting continues, so expected goes below zero.
        var a = Tray();
        a.Load(638, "9999-G071%", "CHIT", T0);
        a.AddMounted(900);
        Assert.Equal(-262, a.Expected);
        var r = a.Recount(376);
        Assert.Equal(RecountVerdict.NeedsSupervisor, r.Verdict);
    }

    [Fact]
    public void Supervisor_correction_sets_expected_to_the_counted_figure_and_is_recorded()
    {
        var a = Tray();
        a.Load(638, "9999-G071%", "CHIT", T0);
        a.AddMounted(900);                       // missed key-in -> expected -262
        var e = a.ApplySupervisorCorrection(376, "4031-E222%", "Noraziah", T0.AddMinutes(5), "tray reloaded, not keyed");
        Assert.Equal(376, a.Expected);
        Assert.True(e.IsCorrection);
        Assert.Equal("Noraziah", e.ByName);
        Assert.Equal(638, e.Qty);                // the adjustment needed to reach 376
        Assert.Equal(2, a.Loads.Count);          // history preserved, never rewritten
        Assert.False(a.Loads[0].IsCorrection);
    }

    [Fact]
    public void Loss_is_visible_once_the_tray_is_emptied()
    {
        var a = Tray();
        a.Load(638, "9999-G071%", "CHIT", T0);
        a.AddMounted(600);
        Assert.Equal(38, a.LossWhenEmptied(0));   // 638 in, 600 placed, nothing left -> 38 lost
        Assert.Equal(0, a.LossWhenEmptied(38));   // 38 still in the tray -> nothing lost
    }

    [Fact]
    public void Loss_is_null_before_anything_has_been_loaded()
    {
        Assert.Null(Tray().LossWhenEmptied(0));
    }

    [Fact]
    public void Tolerance_is_configurable()
    {
        var loose = Tray(tol: 0.01);             // 1% of 638 = 6 pcs
        loose.Load(638, "9999-G071%", "CHIT", T0);
        loose.AddMounted(400);
        Assert.Equal(RecountVerdict.Ok, loose.Recount(232).Verdict);   // 6 short, inside 1%
    }

    [Fact]
    public void Rejects_nonsense_input()
    {
        var a = Tray();
        Assert.Throws<ArgumentOutOfRangeException>(() => a.Load(0, "b", "n", T0));
        Assert.Throws<ArgumentOutOfRangeException>(() => a.Load(-5, "b", "n", T0));
        Assert.Throws<ArgumentOutOfRangeException>(() => a.Recount(-1));
    }
}
