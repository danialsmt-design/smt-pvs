using Pvs.Core.Inventory;
using Xunit;

namespace Pvs.Core.Tests;

/// <summary>
/// The BOM is the input to every shortage, cost and reorder figure, and ProductBOM.Quantity is known not to
/// be a reliable per-board mount count. These tests pin the checks that stop a bad quantity becoming a
/// purchase request — built from the real L254 data that produced a 16,120-piece ask for a 520-board lot.
/// </summary>
public class BomTrustTests
{
    private static BomRow Row(int id, string part, int qty, string pos = "F1", int? line = 3, int? machine = 1) =>
        new(id, 7, line, "Full", machine, pos, part, qty);

    private static List<BomRow> Ones(int n, string pos = "F1") =>
        Enumerable.Range(1, n).Select(i => Row(i, $"PART-{i:000}", 1, pos)).ToList();

    [Fact]
    public void A_normal_positioned_bom_is_trusted()
    {
        var v = BomTrust.Assess(7, "L307", Ones(20));
        Assert.Equal(BomTrustLevel.Trusted, v.Level);
        Assert.True(v.IsClean);
        Assert.True(v.CanQuoteQuantities);
        Assert.Empty(v.Issues);
    }

    [Fact]
    public void A_clean_bom_leaves_a_figure_untouched()
    {
        var v = BomTrust.Assess(7, "L307", Ones(20));
        Assert.Equal("520 pcs", v.Qualify("520 pcs"));
    }

    // L254: 53 rows, not one with a supply position. Nothing in it was ever checked against a machine.
    [Fact]
    public void A_bom_with_no_supply_positions_at_all_is_unusable()
    {
        var rows = Ones(53, pos: "");
        var v = BomTrust.Assess(0, "L254", rows);

        Assert.Equal(BomTrustLevel.Unusable, v.Level);
        Assert.False(v.CanQuoteQuantities);
        Assert.Contains(v.Issues, i => i.Code == "NO_POSITIONS");
    }

    [Fact]
    public void An_unusable_bom_withholds_the_figure_rather_than_stating_it()
    {
        var v = BomTrust.Assess(0, "L254", Ones(53, pos: ""));
        string s = v.Qualify("16,120 pcs");

        Assert.StartsWith("NOT RELIABLE", s);
        Assert.Contains("supply position", s);
        Assert.Contains("Figure withheld", s);      // the number survives for a human, but never bare
    }

    // The actual defect: WA6-3110-000 stored as 31 among rows that are otherwise 1.
    [Fact]
    public void A_quantity_far_outside_the_models_own_distribution_is_flagged()
    {
        var rows = Ones(52);
        rows.Add(Row(2348, "WA6-3110-000", 31));

        var v = BomTrust.Assess(7, "L254", rows);

        Assert.Equal(BomTrustLevel.Suspect, v.Level);
        Assert.True(v.CanQuoteQuantities);          // suspect is still usable, just never bare
        var issue = Assert.Single(v.Issues, i => i.Code == "QTY_OUTLIER");
        Assert.Contains("WA6-3110-000", issue.Detail);
        Assert.Contains("BOMID 2348", issue.Detail);
    }

    [Fact]
    public void A_suspect_figure_carries_its_reason_inline()
    {
        var rows = Ones(52);
        rows.Add(Row(2348, "WA6-3110-000", 31));

        string s = BomTrust.Assess(7, "L254", rows).Qualify("16,120 pcs");

        Assert.Contains("16,120 pcs", s);
        Assert.Contains("UNVERIFIED", s);
        Assert.Contains("WA6-3110-000", s);
    }

    // A board really can carry 40 of one capacitor. Relative-to-median must not punish that.
    [Fact]
    public void A_genuinely_high_count_on_a_high_count_board_is_not_an_outlier()
    {
        var rows = Enumerable.Range(1, 30).Select(i => Row(i, $"CAP-{i:000}", 12)).ToList();
        rows.Add(Row(99, "CAP-BIG", 40));

        var v = BomTrust.Assess(7, "L311", rows);
        Assert.DoesNotContain(v.Issues, i => i.Code == "QTY_OUTLIER");
    }

    // ...but the floor stops a median of 1 from flagging every ordinary part.
    [Fact]
    public void The_absolute_floor_keeps_small_counts_from_flagging_on_a_median_of_one()
    {
        var rows = Ones(20);
        rows.Add(Row(50, "PART-9", 9));             // 9x the median, but under the floor of 10

        var v = BomTrust.Assess(7, "L309", rows);
        Assert.DoesNotContain(v.Issues, i => i.Code == "QTY_OUTLIER");
    }

    // A part on several feeder positions is normal - four rows summing to 14 is a real 14-placement part.
    [Fact]
    public void The_same_part_on_several_feeder_positions_is_not_an_issue()
    {
        var rows = Ones(20);
        rows.Add(Row(60, "VC8-9370-102", 3, "F10"));
        rows.Add(Row(61, "VC8-9370-102", 4, "F11"));
        rows.Add(Row(62, "VC8-9370-102", 3, "F12"));
        rows.Add(Row(63, "VC8-9370-102", 4, "F13"));

        var v = BomTrust.Assess(7, "L307", rows);
        Assert.Equal(BomTrustLevel.Trusted, v.Level);
    }

    [Fact]
    public void Mostly_unpositioned_rows_are_suspect_even_when_a_few_are_positioned()
    {
        var rows = Ones(18, pos: "");
        rows.Add(Row(90, "PART-A", 1, "F1"));
        rows.Add(Row(91, "PART-B", 1, "F2"));

        var v = BomTrust.Assess(7, "L309", rows);
        Assert.Equal(BomTrustLevel.Suspect, v.Level);
        Assert.Contains(v.Issues, i => i.Code == "FEW_POSITIONS");
    }

    [Fact]
    public void Rows_with_no_line_or_machine_are_flagged()
    {
        var rows = Ones(20);
        rows.Add(Row(70, "YA2-0965-006", 1, "F1", line: null, machine: null));

        var v = BomTrust.Assess(7, "L254", rows);
        Assert.Contains(v.Issues, i => i.Code == "NO_LINE");
        Assert.Contains(v.Issues, i => i.Code == "NO_MACHINE");
    }

    [Fact]
    public void ProductId_zero_is_flagged_as_not_a_valid_key()
    {
        var v = BomTrust.Assess(0, "L254", Ones(20));
        Assert.Contains(v.Issues, i => i.Code == "PRODUCT_ID");
    }

    [Fact]
    public void An_empty_bom_is_unusable_rather_than_silently_zero()
    {
        var v = BomTrust.Assess(7, "L999", new List<BomRow>());
        Assert.Equal(BomTrustLevel.Unusable, v.Level);
        Assert.False(v.CanQuoteQuantities);
        Assert.Contains(v.Issues, i => i.Code == "EMPTY");
    }

    [Fact]
    public void A_zero_quantity_row_is_flagged()
    {
        var rows = Ones(20);
        rows.Add(Row(80, "PART-Z", 0));

        var v = BomTrust.Assess(7, "L307", rows);
        Assert.Contains(v.Issues, i => i.Code == "QTY_NOT_POSITIVE");
    }

    // L307/L313: Line 1 holds B Side, Line 5 holds A Side. Two lines, but complementary halves of ONE board -
    // every two-sided part reconciles with Canon only when the lines are summed. This must NOT be flagged.
    [Fact]
    public void Complementary_sides_split_across_two_lines_are_not_duplicates()
    {
        var rows = new List<BomRow>
        {
            new(2515, 3, 1, "B Side", 1, "F101", "VW5-6454-104", 19),
            new(2533, 3, 5, "A Side", 1, "F201", "VW5-6454-104", 6),
            new(2540, 3, 1, "B Side", 2, "F102", "VR8-1300-101", 4),
            new(2541, 3, 5, "A Side", 2, "F202", "VR8-1300-101", 4),
        };

        var v = BomTrust.Assess(3, "L307", rows);
        Assert.DoesNotContain(v.Issues, i => i.Code == "DUPLICATE_LINE_LAYOUT");
    }

    // L261: Lines 1 and 3 each hold a COMPLETE copy of all 34 parts, both sides on each line.
    // Summing across lines doubles every quantity.
    [Fact]
    public void The_same_part_and_side_on_two_lines_is_a_duplicated_layout()
    {
        var rows = new List<BomRow>
        {
            new(2400, 1, 1, "A Side", 1, "F101", "VS1-8550-005", 4),
            new(2421, 1, 3, "A Side", 1, "F101", "VS1-8550-005", 1),
            new(2403, 1, 1, "B Side", 2, "F102", "VS1-8887-012", 4),
            new(2419, 1, 3, "B Side", 2, "F102", "VS1-8887-012", 1),
        };

        var v = BomTrust.Assess(1, "L261", rows);
        Assert.Equal(BomTrustLevel.Suspect, v.Level);
        var issue = Assert.Single(v.Issues, i => i.Code == "DUPLICATE_LINE_LAYOUT");
        Assert.Contains("double-counts", issue.Detail);
        Assert.Contains("1, 3", issue.Detail);
    }

    // L254 sits entirely on one line, so the question does not arise.
    [Fact]
    public void A_single_line_model_is_never_a_duplicated_layout()
    {
        var v = BomTrust.Assess(7, "L254", Ones(20));
        Assert.DoesNotContain(v.Issues, i => i.Code == "DUPLICATE_LINE_LAYOUT");
    }

    // Board-level rows (Line NULL, Side "Full") are sub-assemblies, not feeder placements - they must not
    // be mistaken for a second line's copy.
    [Fact]
    public void Board_level_rows_with_no_line_do_not_count_as_a_duplicate_layout()
    {
        var rows = new List<BomRow>
        {
            new(2549, 3, 1, "B Side", 3, "F538", "YH4-3212-008", 1),
            new(2823, 3, null, "Full", null, "", "YH4-3212-008", 1),
        };

        var v = BomTrust.Assess(3, "L307", rows);
        Assert.DoesNotContain(v.Issues, i => i.Code == "DUPLICATE_LINE_LAYOUT");
    }

    // The reason quoted by Qualify() must be the worst one, not whichever was appended first.
    [Fact]
    public void The_most_serious_issue_is_reported_first()
    {
        var rows = Ones(52, pos: "");
        rows.Add(Row(2348, "WA6-3110-000", 31, pos: ""));

        var v = BomTrust.Assess(0, "L254", rows);
        Assert.Equal(BomTrustLevel.Unusable, v.Issues[0].Level);
        Assert.Equal("NO_POSITIONS", v.Issues[0].Code);
    }
}
