using Pvs.Core.Runtime;
using Xunit;

namespace Pvs.Core.Tests;

public class ProductionCountLedgerSlotTests
{
    private static DateTime Slot(DateTime t)
    {
        var morning = t.Date.AddHours(7).AddMinutes(35);
        var night = t.Date.AddHours(19).AddMinutes(35);
        return t >= night ? night : t >= morning ? morning : t.Date;
    }

    [Fact]
    public void Boards_across_the_shift_change_land_in_two_buckets_and_two_rows()
    {
        var l = new ProductionCountLedger { SlotOf = Slot };
        var key = new ProductionKey("HC1", "L307", "B");
        var d = new DateTime(2026, 9, 7);
        l.AddPanel(key, d.AddHours(19).AddMinutes(33));   // Morning
        l.AddPanel(key, d.AddHours(19).AddMinutes(34));   // Morning
        l.AddPanel(key, d.AddHours(19).AddMinutes(36));   // Night
        l.AddPanel(key, d.AddHours(19).AddMinutes(37));   // Night
        Assert.Equal(4, l.PanelsFor(key));

        var due = l.Due(d.AddHours(19).AddMinutes(38));
        Assert.Equal(2, due.Count);
        Assert.Equal(2, due[0].Panels); Assert.Equal(d.AddHours(19).AddMinutes(33), due[0].Start);   // the Morning row
        Assert.Equal(2, due[1].Panels); Assert.Equal(d.AddHours(19).AddMinutes(36), due[1].Start);   // the Night row

        l.Commit(due[1], 2, d.AddHours(19).AddMinutes(38));   // Night row written first (Morning insert failed)
        Assert.Equal(2, l.PanelsFor(key));
        var again = l.Due(d.AddHours(19).AddMinutes(43));
        Assert.Single(again);
        Assert.Equal(d.AddHours(19).AddMinutes(33), again[0].Start);   // the Morning bucket is the one left
    }

    [Fact]
    public void Midnight_splits_the_night_into_two_days()
    {
        var l = new ProductionCountLedger { SlotOf = Slot };
        var key = new ProductionKey("HC1", "L307", "B");
        var d = new DateTime(2026, 9, 7);
        l.AddPanel(key, d.AddHours(23).AddMinutes(58));
        l.AddPanel(key, d.AddDays(1).AddMinutes(2));
        var due = l.Due(d.AddDays(1).AddMinutes(5));
        Assert.Equal(2, due.Count);
        Assert.Equal(d, due[0].Start.Date);
        Assert.Equal(d.AddDays(1), due[1].Start.Date);
    }

    [Fact]
    public void Restore_merges_persisted_buckets_into_their_slots()
    {
        var l = new ProductionCountLedger { SlotOf = Slot };
        var key = new ProductionKey("HC1", "L307", "B");
        var d = new DateTime(2026, 9, 7);
        l.Restore(new[]
        {
            new ProductionBucket(key, 3, d.AddHours(10)),
            new ProductionBucket(key, 2, d.AddHours(11)),   // same Morning slot → merged, earliest FirstAt kept
            new ProductionBucket(key, 1, d.AddHours(20)),   // Night slot
        }, d.AddHours(21));
        var due = l.Due(d.AddHours(21));
        Assert.Equal(2, due.Count);
        Assert.Equal(5, due[0].Panels); Assert.Equal(d.AddHours(10), due[0].Start);
        Assert.Equal(1, due[1].Panels);
    }

    [Fact]
    public void Without_a_slot_function_behaviour_is_unchanged()
    {
        var l = new ProductionCountLedger();
        var key = new ProductionKey("HC1", "L307", "B");
        var d = new DateTime(2026, 9, 7);
        l.AddPanel(key, d.AddHours(19).AddMinutes(33));
        l.AddPanel(key, d.AddHours(19).AddMinutes(36));
        Assert.Single(l.Due(d.AddHours(19).AddMinutes(38)));
    }
}
