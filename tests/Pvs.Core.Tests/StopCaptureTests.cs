using System;
using System.Linq;
using Pvs.Core.Runtime;
using Xunit;

namespace Pvs.Core.Tests;

public class ReasonStopLogTests
{
    private static DateTime T(int h, int m) => new(2026, 8, 18, h, m, 0);

    [Fact]
    public void AutoStart_opens_a_span_with_the_machine_reason()
    {
        var log = new ReasonStopLog();
        Assert.Null(log.Open);
        log.AutoStart("estop", new[] { 3, 4 }, T(10, 0));
        Assert.NotNull(log.Open);
        Assert.Equal("estop", log.Open!.MachineReason);
        Assert.Equal("estop", log.Open!.Reason);              // effective = machine reason until commented
        Assert.Equal(new[] { 3, 4 }, log.Open!.Machines);
    }

    [Fact]
    public void Ends_when_the_line_produces_again()
    {
        var log = new ReasonStopLog();
        log.AutoStart("stopped", null, T(10, 0));
        log.OnProduced(T(10, 12));
        Assert.Null(log.Open);
        var byReason = log.ByReason(T(11, 0));
        Assert.Equal(TimeSpan.FromMinutes(12), byReason.Single().Total);
        Assert.Equal(1, byReason.Single().Count);
    }

    [Fact]
    public void If_the_line_never_runs_again_it_ends_at_shift_end()
    {
        var log = new ReasonStopLog();
        log.AutoStart("stopped", null, T(18, 40));
        log.CloseAtShiftEnd(T(19, 35));
        Assert.Null(log.Open);
        Assert.Equal(TimeSpan.FromMinutes(55), log.Stops.Single().Duration(T(20, 0)));
    }

    [Fact]
    public void Operator_comment_sets_the_effective_reason_without_moving_the_timing()
    {
        var log = new ReasonStopLog();
        log.AutoStart("stopped", null, T(10, 0));
        Assert.True(log.AddComment("waiting_part", "short on VC9-6547", T(10, 3)));
        Assert.Equal("stopped", log.Open!.MachineReason);      // machine reason preserved
        Assert.Equal("waiting_part", log.Open!.OperatorReason);
        Assert.Equal("waiting_part", log.Open!.Reason);        // effective = operator comment
        Assert.Equal("short on VC9-6547", log.Open!.Note);
        Assert.Equal(T(10, 0), log.Open!.Start);               // timing unchanged
    }

    [Fact]
    public void AutoStart_retag_keeps_the_open_span_and_its_comment()
    {
        var log = new ReasonStopLog();
        log.AutoStart("stopped", null, T(10, 0));
        log.AddComment("rest", null, T(10, 1));
        log.AutoStart("starved", new[] { 2 }, T(10, 5));       // machine reason evolves; same open span
        Assert.Empty(log.Stops);                               // nothing closed
        Assert.Equal("starved", log.Open!.MachineReason);
        Assert.Equal("rest", log.Open!.OperatorReason);        // operator comment preserved
        Assert.Equal(T(10, 0), log.Open!.Start);               // original start preserved
    }

    [Fact]
    public void Open_span_counts_up_to_now_under_the_effective_reason()
    {
        var log = new ReasonStopLog();
        log.AutoStart("starved", null, T(10, 0));
        var byReason = log.ByReason(T(10, 30));
        Assert.Equal("starved", byReason.Single().Reason);
        Assert.Equal(TimeSpan.FromMinutes(30), byReason.Single().Total);
    }

    [Fact]
    public void Tallies_group_time_under_the_effective_reason()
    {
        var log = new ReasonStopLog();
        log.AutoStart("stopped", null, T(10, 0)); log.AddComment("rest", null, T(10, 0)); log.OnProduced(T(10, 15));
        log.AutoStart("stopped", null, T(13, 0)); log.AddComment("rest", null, T(13, 0)); log.OnProduced(T(13, 45));
        log.AutoStart("stopped", null, T(14, 0)); log.AddComment("no_air", null, T(14, 0)); log.OnProduced(T(14, 6));
        var byReason = log.ByReason(T(15, 0));
        Assert.Equal(TimeSpan.FromMinutes(60), byReason.First(r => r.Reason == "rest").Total);
        Assert.Equal(2, byReason.First(r => r.Reason == "rest").Count);
        Assert.Equal(TimeSpan.FromMinutes(6), byReason.First(r => r.Reason == "no_air").Total);
    }

    [Fact]
    public void Comment_with_no_open_span_annotates_the_most_recent()
    {
        var log = new ReasonStopLog();
        log.AutoStart("stopped", null, T(10, 0)); log.OnProduced(T(10, 5));
        Assert.True(log.AddComment("scheduled", null, T(10, 6)));
        Assert.Equal("scheduled", log.Stops.Single().Reason);
        Assert.Null(log.Open);
    }

    [Fact]
    public void Reason_vocabularies_validate()
    {
        Assert.True(ReasonStopLog.IsMachineReason("estop"));
        Assert.False(ReasonStopLog.IsMachineReason("teatime"));
        Assert.True(ReasonStopLog.IsOperatorReason("no_air"));
        Assert.False(ReasonStopLog.IsOperatorReason("estop"));
        Assert.False(ReasonStopLog.IsOperatorReason(null));
    }
}

public class BreakWindowsTests
{
    private static readonly BreakWindows Lunch = new(new[]
    {
        (new TimeOnly(12, 0), new TimeOnly(12, 45)),
        (new TimeOnly(12, 45), new TimeOnly(13, 30)),
        (new TimeOnly(15, 30), new TimeOnly(15, 45)),
    });
    private static DateTime T(int h, int m) => new(2026, 8, 18, h, m, 0);

    [Fact]
    public void IsBreak_is_true_inside_a_window_false_outside()
    {
        Assert.True(Lunch.IsBreak(T(12, 30)));
        Assert.True(Lunch.IsBreak(T(13, 0)));    // second lunch slot
        Assert.True(Lunch.IsBreak(T(15, 40)));
        Assert.False(Lunch.IsBreak(T(11, 59)));
        Assert.False(Lunch.IsBreak(T(14, 0)));
    }

    [Fact]
    public void Overlap_counts_only_the_minutes_inside_a_break()
    {
        // 11:58 -> 12:50 spans the whole first lunch slot (45m) + 5m into the second = 50m of break.
        Assert.Equal(50, Lunch.OverlapMinutes(T(11, 58), T(12, 50)), 3);
        // A stop wholly outside any break overlaps zero.
        Assert.Equal(0, Lunch.OverlapMinutes(T(9, 0), T(9, 30)), 3);
    }

    [Fact]
    public void Net_removes_break_overlap()
    {
        // 11:58 -> 12:50 is 52m wall, 50m of which is break => 2m net.
        Assert.Equal(2, Lunch.NetMinutes(T(11, 58), T(12, 50)), 3);
    }

    [Fact]
    public void Empty_windows_are_inert()
    {
        var none = new BreakWindows(System.Array.Empty<(TimeOnly, TimeOnly)>());
        Assert.False(none.Any);
        Assert.False(none.IsBreak(T(12, 30)));
        Assert.Equal(30, none.NetMinutes(T(12, 0), T(12, 30)), 3);
    }
}

public class CellRecoveryLogTests
{
    private static DateTime T(int h, int m, int s = 0) => new(2026, 8, 18, h, m, s);

    [Fact]
    public void Recovery_is_partsout_to_the_cell_producing_again()
    {
        var log = new CellRecoveryLog();
        log.OnPartsOut(2, 113, "VW5-6454-104", T(10, 0, 0));
        log.OnCellProduced(2, T(10, 3, 0));
        var r = log.Completed.Single();
        Assert.Equal(2, r.Cell);
        Assert.Equal("VW5-6454-104", r.Part);
        Assert.Equal(TimeSpan.FromMinutes(3), (r.End!.Value - r.Start));
    }

    [Fact]
    public void A_second_partsout_while_down_is_ignored()
    {
        var log = new CellRecoveryLog();
        log.OnPartsOut(2, 113, "A", T(10, 0));
        log.OnPartsOut(2, 113, "A", T(10, 1));   // still down — no second clock
        Assert.Single(log.Open);
        log.OnCellProduced(2, T(10, 4));
        Assert.Single(log.Completed);
    }

    [Fact]
    public void Another_cells_board_does_not_close_this_cells_recovery()
    {
        var log = new CellRecoveryLog();
        log.OnPartsOut(2, 113, "A", T(10, 0));
        log.OnCellProduced(4, T(10, 2));         // a different cell produced
        Assert.Empty(log.Completed);
        Assert.Single(log.Open);
    }

    [Fact]
    public void Per_cell_rollup_counts_exhausts_and_recovery()
    {
        var log = new CellRecoveryLog();
        log.OnPartsOut(2, 113, "A", T(10, 0)); log.OnCellProduced(2, T(10, 2));   // 2 min
        log.OnPartsOut(2, 118, "B", T(11, 0)); log.OnCellProduced(2, T(11, 8));   // 8 min
        log.OnPartsOut(4, 111, "C", T(12, 0)); log.OnCellProduced(4, T(12, 1));   // 1 min
        var byCell = log.ByCell();
        var cell2 = byCell.Single(c => c.Cell == 2);
        Assert.Equal(2, cell2.Count);
        Assert.Equal(TimeSpan.FromMinutes(10), cell2.Total);
        Assert.Equal(TimeSpan.FromMinutes(8), cell2.Max);
        Assert.Equal(1, byCell.Single(c => c.Cell == 4).Count);
    }

    [Fact]
    public void Restore_round_trips()
    {
        var log = new CellRecoveryLog();
        log.OnPartsOut(2, 113, "A", T(10, 0));
        log.OnCellProduced(2, T(10, 2));
        log.OnPartsOut(3, 107, "D", T(10, 5));   // left open
        var restored = new CellRecoveryLog();
        restored.Restore(log.Completed, log.Open);
        restored.OnCellProduced(3, T(10, 9));
        Assert.Equal(2, restored.Completed.Count);
        Assert.Empty(restored.Open);
    }
}
