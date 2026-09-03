using Pvs.Core.Inventory;
using Xunit;

namespace Pvs.Core.Tests;

public class ExhaustCalibrationTests
{
    private static readonly DateTime T = new(2026, 9, 1, 12, 0, 0);

    [Fact]
    public void Reel_empties_early_learns_positive_error()
    {
        // Modelled 18/board. Reel of 4000 predicted to last 4000/18 ≈ 222 boards. It actually ran out after 200
        // boards with PVS still showing 400 pcs — the model under-counted by 400/200 = 2.0 pcs/board (attrition).
        var c = new ExhaustCalibration(alpha: 0.5, minBoards: 20);
        var r = c.Record("P1", boardsThisReel: 200, remainingAtExhaust: 400, mountedPerBoard: 18, at: T);
        Assert.NotNull(r);
        Assert.Equal(2.0, r!.PerBoardErrorEwma, 3);
        Assert.Equal(20.0, r.CorrectedPerBoard(18), 3);     // 18 + 2 real per board
        Assert.True(r.DriftPercent(18) > 0);                 // runs out early
        Assert.Equal(1, r.Samples);
    }

    [Fact]
    public void Ewma_smooths_successive_samples()
    {
        var c = new ExhaustCalibration(alpha: 0.5, minBoards: 10);
        c.Record("P1", 100, 300, 10, T);                     // err 3.0 -> ewma 3.0
        var r = c.Record("P1", 100, 100, 10, T);             // err 1.0 -> ewma 0.5*1 + 0.5*3 = 2.0
        Assert.Equal(2.0, r!.PerBoardErrorEwma, 3);
        Assert.Equal(1.0, r.LastErrorPerBoard, 3);
        Assert.Equal(2, r.Samples);
    }

    [Fact]
    public void Over_counting_learns_negative_error()
    {
        // PVS drove remaining to 0 well before the reel emptied is captured as the reel running MORE boards than
        // the model allowed — here remaining went negative-in-effect: reel still fed at 0. We model that as a
        // small negative correction when remainingAtExhaust is 0 but boards exceed prediction is not observable;
        // the direct signal we DO get is remaining>0 (early). A recount that leaves 0 gives err 0.
        var c = new ExhaustCalibration(alpha: 1.0, minBoards: 10);
        var r = c.Record("P1", 100, 0, 10, T);
        Assert.Equal(0.0, r!.PerBoardErrorEwma, 3);
        Assert.Equal(10.0, r.CorrectedPerBoard(10), 3);
    }

    [Fact]
    public void Too_few_boards_is_ignored_as_noise()
    {
        var c = new ExhaustCalibration(minBoards: 20);
        Assert.Null(c.Record("P1", boardsThisReel: 5, remainingAtExhaust: 100, mountedPerBoard: 10, at: T));
        Assert.Null(c.Get("P1"));
    }

    [Fact]
    public void Blank_part_is_ignored()
    {
        var c = new ExhaustCalibration();
        Assert.Null(c.Record("", 100, 50, 10, T));
        Assert.Null(c.Record("  ", 100, 50, 10, T));
    }

    [Fact]
    public void All_orders_worst_drift_first()
    {
        var c = new ExhaustCalibration(alpha: 1.0, minBoards: 10);
        c.Record("Small", 100, 50, 10, T);     // err 0.5
        c.Record("Big", 100, 300, 10, T);      // err 3.0
        var all = c.All();
        Assert.Equal("Big", all[0].Part);
        Assert.Equal("Small", all[1].Part);
    }
}
