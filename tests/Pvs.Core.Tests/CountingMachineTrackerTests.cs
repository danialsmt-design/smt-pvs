using Pvs.Core.Runtime;
using Xunit;

namespace Pvs.Core.Tests;

/// <summary>
/// Counting follows the LAST AVAILABLE machine, because a line gets reconfigured onto fewer machines when one
/// has a problem. The lot count must survive that handover unbroken — machines' own totals are unrelated
/// (M3 might be at 40,000 while M4 is at 2,600), so a naive switch would make the lot count leap or collapse.
/// </summary>
public class CountingMachineTrackerTests
{
    private static Dictionary<int, long> Totals(params (int m, long t)[] xs) => xs.ToDictionary(x => x.m, x => x.t);
    private static HashSet<int> Online(params int[] ms) => new(ms);

    [Fact]
    public void Counts_at_the_last_machine_when_all_four_are_up()
    {
        var t = new CountingMachineTracker();
        var r = t.Evaluate(Totals((1, 24351), (2, 37373), (3, 6998), (4, 2626)), Online(1, 2, 3, 4));
        Assert.Equal(4, r.Machine);
        Assert.Equal(CountingSourceChange.Initial, r.Change);
        Assert.Equal(0, t.LotBoards);
    }

    [Fact]
    public void The_lot_count_advances_with_the_counting_machine()
    {
        var t = new CountingMachineTracker();
        t.Evaluate(Totals((4, 2626)), Online(4));
        t.Evaluate(Totals((4, 2650)), Online(4));
        Assert.Equal(24, t.LotBoards);
    }

    [Fact]
    public void Falling_back_to_M3_holds_the_lot_count_despite_wildly_different_totals()
    {
        var t = new CountingMachineTracker();
        t.Evaluate(Totals((3, 6998), (4, 2626)), Online(3, 4));
        t.Evaluate(Totals((3, 7048), (4, 2676)), Online(3, 4));      // 50 boards on the lot
        Assert.Equal(50, t.LotBoards);

        // M4 drops out; M3's own total is 7,048 - nearly 3x M4's. A naive switch would report thousands.
        var r = t.Evaluate(Totals((3, 7048), (4, 2676)), Online(3));
        Assert.Equal(3, r.Machine);
        Assert.Equal(CountingSourceChange.FellBack, r.Change);
        Assert.Equal(50, t.LotBoards);                              // unchanged across the handover
        Assert.Contains("fell back", r.Message);

        t.Evaluate(Totals((3, 7060), (4, 2676)), Online(3));         // M3 carries on
        Assert.Equal(62, t.LotBoards);
    }

    [Fact]
    public void Recovering_M4_moves_counting_forward_without_a_jump()
    {
        var t = new CountingMachineTracker();
        t.Evaluate(Totals((3, 7000), (4, 2600)), Online(3, 4));
        t.Evaluate(Totals((3, 7000), (4, 2600)), Online(3));          // fell back
        t.Evaluate(Totals((3, 7040), (4, 2600)), Online(3));          // 40 counted at M3
        Assert.Equal(40, t.LotBoards);

        var r = t.Evaluate(Totals((3, 7040), (4, 2610)), Online(3, 4));
        Assert.Equal(4, r.Machine);
        Assert.Equal(CountingSourceChange.Recovered, r.Change);
        Assert.Equal(40, t.LotBoards);                               // no leap when moving back to M4
    }

    [Fact]
    public void A_line_reconfigured_onto_two_machines_counts_at_M2()
    {
        var t = new CountingMachineTracker();
        var r = t.Evaluate(Totals((1, 100), (2, 200), (3, 300), (4, 400)), Online(1, 2));
        Assert.Equal(2, r.Machine);
    }

    [Fact]
    public void Everything_offline_holds_the_count_rather_than_resetting_it()
    {
        var t = new CountingMachineTracker();
        t.Evaluate(Totals((4, 2600)), Online(4));
        t.Evaluate(Totals((4, 2640)), Online(4));
        Assert.Equal(40, t.LotBoards);

        var r = t.Evaluate(Totals((4, 2640)), Online());
        Assert.Null(r.Machine);
        Assert.Equal(40, t.LotBoards);                               // held, not lost
        Assert.Contains("held", r.Message);
    }

    // Operators reset every machine's counter at end of lot (written SOP). If that happens mid-lot the
    // machine total drops; the lot count must not follow it down.
    [Fact]
    public void A_machine_counter_reset_under_us_does_not_drag_the_lot_count_backwards()
    {
        var t = new CountingMachineTracker();
        t.Evaluate(Totals((4, 2600)), Online(4));
        t.Evaluate(Totals((4, 2680)), Online(4));
        Assert.Equal(80, t.LotBoards);

        t.Evaluate(Totals((4, 0)), Online(4));                       // operator cleared it
        Assert.Equal(80, t.LotBoards);

        t.Evaluate(Totals((4, 12)), Online(4));                      // and it carries on from zero
        Assert.Equal(92, t.LotBoards);
    }

    [Fact]
    public void Starting_a_new_lot_restarts_the_count()
    {
        var t = new CountingMachineTracker();
        t.Evaluate(Totals((4, 2600)), Online(4));
        t.Evaluate(Totals((4, 2700)), Online(4));
        Assert.Equal(100, t.LotBoards);

        t.StartLot(Totals((4, 2700)));
        Assert.Equal(0, t.LotBoards);
        t.Evaluate(Totals((4, 2716)), Online(4));
        Assert.Equal(16, t.LotBoards);
    }

    // The Line 2 incident: the anchor was set early and 20 panels of the previous run were attributed to
    // this lot. A supervisor corrects it; counting must then continue from the corrected figure.
    [Fact]
    public void A_supervisor_correction_sets_the_count_and_counting_continues_from_it()
    {
        var t = new CountingMachineTracker();
        t.Evaluate(Totals((4, 1234)), Online(4));
        t.Evaluate(Totals((4, 1479)), Online(4));
        Assert.Equal(245, t.LotBoards);                              // over-counted

        t.SetLotBoards(225, Totals((4, 1479)));
        Assert.Equal(225, t.LotBoards);

        t.Evaluate(Totals((4, 1484)), Online(4));
        Assert.Equal(230, t.LotBoards);                              // continues from the corrected value
    }

    [Fact]
    public void An_unconfigured_machine_is_never_chosen_even_if_online()
    {
        var t = new CountingMachineTracker();
        // M5 is online but has no total (not part of this line's configuration).
        var r = t.Evaluate(Totals((1, 10), (2, 20)), Online(1, 2, 5));
        Assert.Equal(2, r.Machine);
    }
}
