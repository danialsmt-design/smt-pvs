using Pvs.Core.Inventory;
using Xunit;

namespace Pvs.Core.Tests;

public class AttritionLedgerTests
{
    private static readonly DateTime T0 = new(2026, 9, 14, 10, 0, 0);

    [Fact]
    public void Percent_is_shortage_over_start_qty_and_the_limit_is_strict()
    {
        var l = new AttritionLedger(limitPct: 2.0, minBoards: 20);
        var r = l.Evaluate(T0, "LOT1", 2, 113, "P1", "U1", startQty: 5000, boardsThisReel: 300, mountedPerBoard: 4, remainingAtExhaust: 100);
        Assert.Equal(100, r.Shortage);
        Assert.Equal(2.0, r.Percent);
        Assert.False(r.Over);          // exactly 2 % is within the limit
        Assert.False(r.Escalate);
        var r2 = l.Evaluate(T0, "LOT1", 2, 113, "P1", "U2", 5000, 300, 4, 101);
        Assert.True(r2.Over);
        Assert.True(r2.Escalate);
        Assert.Equal(2.02, r2.Percent);
    }

    [Fact]
    public void Short_run_over_the_limit_is_reported_but_not_escalated()
    {
        var l = new AttritionLedger(2.0, 20);
        var r = l.Evaluate(T0, "LOT1", 1, 5, "P", "U", 1000, boardsThisReel: 10, 1, remainingAtExhaust: 500);
        Assert.True(r.Over);
        Assert.False(r.Escalate);
    }

    [Fact]
    public void Negative_remaining_and_zero_start_never_produce_a_percent()
    {
        var l = new AttritionLedger();
        var a = l.Evaluate(T0, "L", 1, 1, "P", "U", 1000, 100, 1, -5);
        Assert.Equal(0, a.Shortage); Assert.False(a.Over);
        var b = l.Evaluate(T0, "L", 1, 1, "P", "U", 0, 100, 1, 300);
        Assert.Equal(0.0, b.Percent); Assert.False(b.Over);
    }

    [Fact]
    public void One_sample_per_reel_and_escalations_are_taken_once_per_lot()
    {
        var l = new AttritionLedger(2.0, 20);
        Assert.NotNull(l.Record(T0, "LOT1", 1, 7, "P", "U1", 1000, 50, 2, 100));
        Assert.Null(l.Record(T0.AddMinutes(1), "LOT1", 1, 7, "P", "U1", 1000, 50, 2, 100));   // repeat parts-out frame
        Assert.NotNull(l.Record(T0, "LOT1", 1, 8, "P2", "U2", 1000, 50, 2, 5));                 // within limit
        Assert.NotNull(l.Record(T0, "LOT2", 2, 9, "P3", "U3", 1000, 50, 2, 200));               // other lot
        var due = l.TakeEscalations("LOT1");
        Assert.Single(due);
        Assert.Equal("U1", due[0].ReelUid);
        Assert.True(due[0].Alerted);
        Assert.Empty(l.TakeEscalations("LOT1"));                                               // second finalise: nothing re-sent
        Assert.Single(l.TakeEscalations("LOT2"));
        Assert.True(l.All().All(r => r.Alerted || !r.Escalate));
    }

    [Fact]
    public void Alerted_flag_survives_a_reload_through_the_seed()
    {
        var l = new AttritionLedger(2.0, 20);
        l.Record(T0, "LOT1", 1, 7, "P", "U1", 1000, 50, 2, 100);
        l.TakeEscalations("LOT1");
        var reloaded = new AttritionLedger(2.0, 20, seed: l.All());
        Assert.Empty(reloaded.TakeEscalations("LOT1"));
        Assert.Single(reloaded.ForLot("LOT1"));
    }

    [Fact]
    public void Message_lists_each_reel_with_shortage_and_percent()
    {
        var l = new AttritionLedger(2.0, 20);
        var r = l.Evaluate(T0, "HC1", 2, 113, "1-234", "880100-G429", 5000, 300, 4, 250);
        string m = AttritionLedger.ComposeMessage("Line 1", "HC1", "L261", new[] { r }, 2.0);
        Assert.Contains("Line 1 · Lot HC1 · L261", m);
        Assert.Contains("M2 F113 1-234 · reel 880100-G429 · short 250 of 5,000 (5.0%) after 300 boards", m);
        Assert.Contains("1 reel(s)", m);
    }
}
