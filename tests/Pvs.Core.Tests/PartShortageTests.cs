using Pvs.Core.Inventory;

namespace Pvs.Core.Tests;

/// <summary>
/// The forward-looking shortage forecast: per-board usage out of the ProductBOM copies, then the running
/// demand lot by lot in delivery-date order, and the first lot that cannot be covered.
/// </summary>
public class PartShortageTests
{
    private static readonly DateTime Now = new(2026, 8, 7, 8, 0, 0);

    private static UpcomingLot Lot(string lot, string model, int daysAhead, int size, int built = 0) =>
        new(lot, model, Now.Date.AddDays(daysAhead), size, built);

    private static PartStock Have(string part, long store, long line = 0, decimal? price = null) =>
        new(part, store, line, line, price);

    private static ShortageForecast Run(
        IEnumerable<UpcomingLot> lots, IEnumerable<ModelPartUsage> usage, IEnumerable<PartStock> stock,
        int leadDays = 2) =>
        ShortageForecaster.Analyse(Now, lots, usage, stock, new ShortageOptions(leadDays));

    // ---- per-board usage out of the BOM ----

    [Fact]
    public void A_part_on_several_feeders_of_one_side_is_summed()
    {
        // L254 really does mount VE3-6800-105 from machine 2 (9/board) and machine 1 (1/board).
        var usage = BomUsageResolver.Resolve(new[]
        {
            new BomUsageRow("L254", "Full", 3, "VE3-6800-105", 9),
            new BomUsageRow("L254", "Full", 3, "VE3-6800-105", 1),
        });
        Assert.Equal(10, Assert.Single(usage).QtyPerBoard);
    }

    [Fact]
    public void A_part_used_on_both_sides_of_one_model_sums_across_sides()
    {
        var usage = BomUsageResolver.Resolve(new[]
        {
            new BomUsageRow("L264", "A Side", 3, "VE3-1480-104", 15),
            new BomUsageRow("L264", "B Side", 3, "VE3-1480-104", 10),
        });
        Assert.Equal(25, Assert.Single(usage).QtyPerBoard);
    }

    [Fact]
    public void Disagreeing_per_line_copies_of_one_side_are_not_added_together()
    {
        // L261's 'A Side' is stored twice and the copies disagree (55 under Line 1, 40 under Line 3).
        // Counting both would double the demand; the larger copy is taken so demand is never understated.
        var usage = BomUsageResolver.Resolve(new[]
        {
            new BomUsageRow("L261", "A Side", 1, "VW5-6454-104", 12),
            new BomUsageRow("L261", "A Side", 3, "VW5-6454-104", 9),
        });
        Assert.Equal(12, Assert.Single(usage).QtyPerBoard);
    }

    [Fact]
    public void Sides_held_under_different_line_numbers_are_both_counted()
    {
        // L307 keeps 'B Side' under Line 1 and 'A Side' under Line 5. Filtering to one line loses half the BOM.
        var usage = BomUsageResolver.Resolve(new[]
        {
            new BomUsageRow("L307", "B Side", 1, "VR8-1300-101", 4),
            new BomUsageRow("L307", "A Side", 5, "VR8-1300-101", 3),
        });
        Assert.Equal(7, Assert.Single(usage).QtyPerBoard);
    }

    [Fact]
    public void Rows_with_no_part_or_no_quantity_are_ignored()
    {
        var usage = BomUsageResolver.Resolve(new[]
        {
            new BomUsageRow("L254", "Full", 3, "  ", 5),
            new BomUsageRow("L254", "Full", 3, "WA6-3110-000", 0),
            new BomUsageRow("", "Full", 3, "WA6-3110-000", 5),
            new BomUsageRow("L254", "Full", 3, " WA6-3110-000 ", 31),
        });
        var u = Assert.Single(usage);
        Assert.Equal("WA6-3110-000", u.PartNumber);
        Assert.Equal(31, u.QtyPerBoard);
    }

    // ---- the running demand ----

    [Fact]
    public void No_shortage_at_all_reports_nothing()
    {
        var f = Run(
            new[] { Lot("HC1", "L254", 3, 300) },
            new[] { new ModelPartUsage("L254", "WA6-3110-000", 31) },
            new[] { Have("WA6-3110-000", 20_000) });

        Assert.Empty(f.Findings);
        Assert.False(f.Any);
        Assert.Equal(1, f.LotsConsidered);
    }

    [Fact]
    public void A_part_short_across_several_lots_is_reported_at_the_first_lot_that_runs_out()
    {
        // 5,000 on hand. 100/lot at 20 a board: lot 1 needs 2,000 (fine), lot 2 takes it to 4,000 (fine),
        // lot 3 to 6,000 — the third lot is the one that cannot be built, and it is the one named.
        var f = Run(
            new[] { Lot("HC1", "L254", 1, 100), Lot("HC2", "L254", 4, 100), Lot("HC3", "L254", 9, 100) },
            new[] { new ModelPartUsage("L254", "WA6-3110-000", 20) },
            new[] { Have("WA6-3110-000", 5_000) });

        var one = Assert.Single(f.Findings);
        Assert.Equal("HC3", one.LotNo);
        Assert.Equal(6_000, one.DemandThroughLot);
        Assert.Equal(1_000, one.ShortBy);
        Assert.Equal(5_000, one.Available);
        Assert.False(one.Urgent);                       // nine days out — information, not an emergency
    }

    [Fact]
    public void Only_the_first_failing_lot_is_reported_per_part()
    {
        var f = Run(
            new[] { Lot("HC1", "L254", 1, 500), Lot("HC2", "L254", 4, 500) },
            new[] { new ModelPartUsage("L254", "WA6-3110-000", 10) },
            new[] { Have("WA6-3110-000", 100) });

        Assert.Equal("HC1", Assert.Single(f.Findings).LotNo);
    }

    [Fact]
    public void A_lot_already_fully_built_needs_no_material()
    {
        // 300-board lot with 300 built. Nothing on hand at all, and still nothing to report.
        var f = Run(
            new[] { Lot("HC20787321000", "L254", 3, 300, built: 300) },
            new[] { new ModelPartUsage("L254", "WA6-3110-000", 31) },
            new[] { Have("WA6-3110-000", 0) });

        Assert.Empty(f.Findings);
        Assert.Equal(1, f.LotsAlreadyBuilt);
        Assert.Empty(f.ActiveLots);
    }

    [Fact]
    public void A_partly_built_lot_only_needs_material_for_what_is_left()
    {
        var f = Run(
            new[] { Lot("HC1", "L254", 3, 300, built: 250) },
            new[] { new ModelPartUsage("L254", "WA6-3110-000", 10) },
            new[] { Have("WA6-3110-000", 500) });

        Assert.Empty(f.Findings);                       // 50 boards left x 10 = 500, exactly covered
    }

    [Fact]
    public void A_part_used_on_both_sides_drives_demand_at_the_combined_rate()
    {
        // 25/board (15 on the A side, 10 on the B side) x 600 boards = 15,000 against 10,000 on hand.
        var usage = BomUsageResolver.Resolve(new[]
        {
            new BomUsageRow("L264", "A Side", 3, "VE3-1480-104", 15),
            new BomUsageRow("L264", "B Side", 3, "VE3-1480-104", 10),
        });
        var f = Run(new[] { Lot("HC20788571000", "L264", 5, 600) }, usage, new[] { Have("VE3-1480-104", 10_000) });

        var one = Assert.Single(f.Findings);
        Assert.Equal(25, one.QtyPerBoard);
        Assert.Equal(15_000, one.DemandThroughLot);
        Assert.Equal(5_000, one.ShortBy);
    }

    [Fact]
    public void An_unknown_lot_size_creates_no_demand_and_is_declared()
    {
        var f = Run(
            new[]
            {
                new UpcomingLot("HC-NOSIZE", "L254", Now.Date.AddDays(1), null, 0),
                new UpcomingLot("HC-ZERO", "L254", Now.Date.AddDays(1), 0, 0),
            },
            new[] { new ModelPartUsage("L254", "WA6-3110-000", 31) },
            new[] { Have("WA6-3110-000", 0) });

        Assert.Empty(f.Findings);
        Assert.Equal(2, f.LotsUnknownSize);
    }

    [Fact]
    public void Demand_exactly_equal_to_stock_is_not_a_shortage_but_one_more_piece_is()
    {
        var lots = new[] { Lot("HC1", "L254", 1, 100), Lot("HC2", "L254", 4, 100) };
        var usage = new[] { new ModelPartUsage("L254", "WA6-3110-000", 10) };

        // Exactly at the boundary: 2,000 demanded through lot 2, 2,000 on hand.
        Assert.Empty(Run(lots, usage, new[] { Have("WA6-3110-000", 2_000) }).Findings);

        // One piece less and lot 2 is the first that cannot be built — lot 1 still can.
        var one = Assert.Single(Run(lots, usage, new[] { Have("WA6-3110-000", 1_999) }).Findings);
        Assert.Equal("HC2", one.LotNo);
        Assert.Equal(1, one.ShortBy);
    }

    [Fact]
    public void Store_stock_alone_would_call_a_loaded_part_empty()
    {
        // The bug this monitor exists to avoid: StockIns.RemainingQty is zeroed at issue, so a part with
        // thousands of pieces on the machines reads as ZERO from the store. Both halves must be counted.
        var loaded = new PartStock("VV5-3355-103", StoreQty: 0, LineQty: 7_192, LineQtyTracked: 7_192, UnitPrice: null);
        Assert.Equal(7_192, loaded.Available);

        var f = Run(
            new[] { Lot("HC1", "L307", 1, 700) },
            new[] { new ModelPartUsage("L307", "VV5-3355-103", 10) },
            new[] { loaded });

        Assert.Empty(f.Findings);
    }

    [Fact]
    public void A_lot_inside_the_lead_time_window_is_urgent_and_one_beyond_it_is_not()
    {
        var f = Run(
            new[] { Lot("HC-SOON", "L254", 2, 100), Lot("HC-LATER", "L264", 6, 100) },
            new[] { new ModelPartUsage("L254", "PART-A", 10), new ModelPartUsage("L264", "PART-B", 10) },
            new[] { Have("PART-A", 0), Have("PART-B", 0) },
            leadDays: 2);

        Assert.True(f.Findings.Single(x => x.PartNumber == "PART-A").Urgent);
        Assert.False(f.Findings.Single(x => x.PartNumber == "PART-B").Urgent);
        Assert.Single(f.Urgent);
        Assert.Single(f.Later);
    }

    [Fact]
    public void An_overdue_lot_is_urgent()
    {
        var f = Run(
            new[] { Lot("HC-LATE", "L254", -1, 100) },
            new[] { new ModelPartUsage("L254", "PART-A", 10) },
            new[] { Have("PART-A", 0) });

        Assert.True(Assert.Single(f.Findings).Urgent);
        Assert.Equal(-1, f.Findings[0].DaysToDelivery);
    }

    [Fact]
    public void Lots_are_consumed_in_delivery_date_order_not_input_order()
    {
        // Fed later-first. The earlier lot must take the stock, so the LATER one is the one that runs out.
        var f = Run(
            new[] { Lot("HC-LATER", "L254", 9, 100), Lot("HC-EARLIER", "L254", 1, 100) },
            new[] { new ModelPartUsage("L254", "PART-A", 10) },
            new[] { Have("PART-A", 1_500) });

        Assert.Equal("HC-LATER", Assert.Single(f.Findings).LotNo);
    }

    [Fact]
    public void An_undated_lot_is_worked_last_and_never_jumps_the_queue()
    {
        var f = Run(
            new[] { new UpcomingLot("HC-NODATE", "L254", null, 100, 0), Lot("HC-DATED", "L254", 9, 100) },
            new[] { new ModelPartUsage("L254", "PART-A", 10) },
            new[] { Have("PART-A", 1_500) });

        Assert.Equal("HC-NODATE", Assert.Single(f.Findings).LotNo);
        Assert.False(f.Findings[0].Urgent);              // no date => never urgent
    }

    [Fact]
    public void A_model_with_no_parts_list_is_skipped_and_named()
    {
        var f = Run(
            new[] { Lot("HC1", "L999", 1, 300) },
            new[] { new ModelPartUsage("L254", "PART-A", 10) },
            new[] { Have("PART-A", 0) });

        Assert.Empty(f.Findings);
        Assert.Equal("L999", Assert.Single(f.ModelsWithoutBom));
    }

    [Fact]
    public void A_part_with_no_stock_row_at_all_counts_as_zero_on_hand()
    {
        var f = Run(
            new[] { Lot("HC1", "L254", 1, 10) },
            new[] { new ModelPartUsage("L254", "NEVER-STOCKED", 3) },
            Array.Empty<PartStock>());

        var one = Assert.Single(f.Findings);
        Assert.Equal(0, one.Available);
        Assert.Equal(30, one.ShortBy);
    }

    [Fact]
    public void The_shortfall_is_valued_when_a_unit_price_is_known()
    {
        var f = Run(
            new[] { Lot("HC1", "L254", 1, 100) },
            new[] { new ModelPartUsage("L254", "WA6-3110-000", 31) },
            new[] { Have("WA6-3110-000", 100, price: 0.1118m) });

        var one = Assert.Single(f.Findings);
        Assert.Equal(3_000, one.ShortBy);
        Assert.Equal(3_000 * 0.1118m, one.ShortValue);
    }

    [Fact]
    public void Coverage_names_which_lines_are_reporting_and_which_are_not()
    {
        var c = new ShortageCoverage(1_000, 250, new[] { "1", "2", "5" }, new[] { "3", "4" });
        Assert.Equal(25.0, c.TrackedPercent);
        Assert.False(c.FullyTracked);
        Assert.False(c.NothingKnown);

        var all = new ShortageCoverage(1_000, 900, new[] { "1", "2" }, Array.Empty<string>());
        Assert.True(all.FullyTracked);
        Assert.True(ShortageCoverage.Unknown.NothingKnown);
    }

    [Fact]
    public void Null_and_empty_inputs_produce_an_empty_forecast_rather_than_throwing()
    {
        var f = ShortageForecaster.Analyse(Now, null, null, null);
        Assert.Empty(f.Findings);
        Assert.Equal(0, f.LotsConsidered);
        Assert.Empty(BomUsageResolver.Resolve(null));
    }
}
