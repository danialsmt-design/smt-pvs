using Pvs.Core.Feeders;
using Xunit;

namespace Pvs.Core.Tests;

public class FeederMasterTests
{
    private static MasterBlock L307B() => new("L307", "B", 4,
        new Dictionary<int, List<MasterFeeder>>
        {
            [1] = new() { new(112, "WD8-5697-000", 4), new(114, "WA2-2419-000", 4), new(111, "VW5-6654-102", 5) },
            [2] = new() { new(113, "VW5-6454-104", 19) },
            [3] = new() { new(127, "VL8-1180-103", 1) },
            [4] = new() { new(111, "WA6-4722-000", 1) },
        }, "edited", true, new DateTime(2026, 9, 25), "YATI", 1);

    [Fact]
    public void Totals_per_machine_and_line_per_board_and_per_panel()
    {
        var b = L307B();
        Assert.Equal(13, b.MachineShotsPerBoard(1));
        Assert.Equal(52, b.MachineShotsPerPanel(1));
        Assert.Equal(19, b.MachineShotsPerBoard(2));
        Assert.Equal(76, b.MachineShotsPerPanel(2));
        Assert.Equal(34, b.TotalShotsPerBoard);
        Assert.Equal(136, b.TotalShotsPerPanel);
        Assert.Equal(6, b.FeederCount);
        Assert.Equal("L307|B", b.Key);
        Assert.Equal("L307|B", MasterBlock.KeyOf(" l307 ", "b"));
    }

    [Fact]
    public void Validation_refuses_fractions_zero_shots_duplicates_and_empty_parts()
    {
        Assert.Empty(FeederMasterRules.Validate(L307B()));
        var bad = L307B() with { Machines = new Dictionary<int, List<MasterFeeder>> { [1] = new() { new(112, "WD8", 0), new(112, "", 2) } } };
        var errs = FeederMasterRules.Validate(bad);
        Assert.Contains(errs, e => e.Contains("shots per board"));
        Assert.Contains(errs, e => e.Contains("listed twice"));
        Assert.Contains(errs, e => e.Contains("part is empty"));
        Assert.Contains(FeederMasterRules.Validate(L307B() with { BoardsPerPanel = 0 }), e => e.Contains("boards per panel"));
        Assert.Contains(FeederMasterRules.Validate(L307B() with { Side = "C" }), e => e.Contains("side"));
    }

    [Fact]
    public void A_pen_drive_per_panel_count_must_divide_by_the_panel_factor()
    {
        Assert.Equal(4, FeederMasterRules.ShotsPerBoardFromPanelCount(16, 4));     // L1 WD8-5697-000: 16/panel = 4/board
        Assert.Equal(19, FeederMasterRules.ShotsPerBoardFromPanelCount(76, 4));    // VW5-6454-104
        Assert.Null(FeederMasterRules.ShotsPerBoardFromPanelCount(106, 4));        // L347 M1 at 4-up: 26.5 — impossible
        Assert.Equal(53, FeederMasterRules.ShotsPerBoardFromPanelCount(106, 2));   // at 2-up it is whole
        Assert.Null(FeederMasterRules.ShotsPerBoardFromPanelCount(0, 4));
    }

    [Fact]
    public void Diff_lists_added_removed_and_changed_rows()
    {
        var cur = L307B();
        var inc = cur with
        {
            BoardsPerPanel = 4,
            Machines = new Dictionary<int, List<MasterFeeder>>
            {
                [1] = new() { new(112, "WD8-5697-000", 4), new(114, "WA2-2419-000", 5), new(115, "VR8-1300-123", 4) },   // 114 changed, 111 removed, 115 added
                [2] = cur.Machines[2], [3] = cur.Machines[3], [4] = cur.Machines[4],
            }
        };
        var d = FeederMasterRules.Diff(cur, inc);
        Assert.Contains(d, x => x.Contains("F114") && x.Contains("4 -> 5"));
        Assert.Contains(d, x => x.Contains("F115") && x.Contains("added"));
        Assert.Contains(d, x => x.Contains("F111") && x.Contains("removed"));
        Assert.Equal(3, d.Count);
        Assert.Single(FeederMasterRules.Diff(null, inc));
    }
}
