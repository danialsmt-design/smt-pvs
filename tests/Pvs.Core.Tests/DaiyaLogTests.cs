using Pvs.Core.Runtime;

namespace Pvs.Core.Tests;

public class DaiyaLogTests
{
    private static DateTime T(int h, int m) => new(2026, 8, 22, h, m, 0);

    [Fact]
    public void Buckets_by_half_hour_slot_and_tracks_first_last()
    {
        var d = new DaiyaLog();
        d.OnBoard(1, T(17, 45));   // slot 17
        d.OnBoard(1, T(17, 50));   // slot 17
        d.OnBoard(1, T(18, 10));   // 18:10 -> min<30 -> slot 17
        d.OnBoard(1, T(18, 40));   // slot 18
        var pbh = d.PanelByHour();
        Assert.Equal(3, pbh[17]);
        Assert.Equal(1, pbh[18]);
        Assert.Equal(4, d.PanelsTotal);
        Assert.Equal(T(17, 45), d.FirstBoard);
        Assert.Equal(T(18, 40), d.LastBoard);
    }

    [Fact]
    public void Slot_is_half_hour_aligned()
    {
        Assert.Equal(17, DaiyaLog.SlotHour(T(17, 45)));
        Assert.Equal(16, DaiyaLog.SlotHour(T(17, 20)));
    }

    [Fact]
    public void Operators_dedupe_by_user_skip_unknown_and_leader_first()
    {
        var d = new DaiyaLog();
        d.AddOperator("u1", "Lae", 1, T(8, 0));
        d.AddOperator("u1", "Lae", 1, T(9, 0));   // same user -> dedupe
        d.AddOperator("u2", "Yadi", 2, T(8, 5));  // supervisor
        d.AddOperator("?", "x", 0, T(8, 6));      // not-found badge -> skip
        d.AddOperator("u3", "", 1, T(8, 7));      // empty name -> skip
        var ops = d.Operators();
        Assert.Equal(2, ops.Count);
        Assert.Equal("Yadi", ops[0].Name);        // highest level first (line leader)
    }
}
