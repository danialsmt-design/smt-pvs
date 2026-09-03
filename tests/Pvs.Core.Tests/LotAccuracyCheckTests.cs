using Pvs.Core.Data;
using Pvs.Core.Runtime;
using Xunit;

namespace Pvs.Core.Tests;

/// <summary>
/// The four-way board-count check. Scenarios are the real ones: the operator resets every machine's counter
/// at end of lot (written SOP), PVS goes blind on restarts and serial drops, the operator's own application
/// keeps its own count, and the same lot number legitimately runs on two lines and both sides at once.
/// </summary>
public class LotAccuracyCheckTests
{
    // L307-style 4-up panel on Line 1, B side — one machine cycle = 4 child boards.
    private const int PerPanel = 4;
    private static readonly LotScope L1B = LotScope.For("HC20789020000", "B", 1);
    private static LotSizeRow Order(int qty) => new("HC20789020000", "Planned", qty, null, null);

    private static LotAccuracyInput Input(
        int pvsPanels, int? machinePanels = null, int? previousMachinePanels = null,
        int? operatorBoards = null, LotSizeRow? lotSize = null, LotScope? scope = null, string? error = null) =>
        new(scope ?? L1B, pvsPanels, PerPanel, machinePanels, previousMachinePanels, operatorBoards,
            lotSize ?? Order(3000), Machine: 4, MachineError: error);

    [Fact]
    public void All_four_sources_agreeing_is_the_healthy_case()
    {
        var r = LotAccuracyCheck.Compare(Input(pvsPanels: 300, machinePanels: 300, previousMachinePanels: 280,
            operatorBoards: 1200, lotSize: Order(3000)));

        Assert.Equal(AccuracyStatus.Agree, r.Status);
        Assert.Empty(r.Findings);
        Assert.False(r.NeedsSupervisor);
        Assert.False(r.ShouldAdoptMachineCount);
        Assert.False(r.ShouldReAnchor);
        // Everything is reported in BOARDS, so a supervisor never has to convert panels.
        Assert.Equal(1200, r.PvsBoards);
        Assert.Equal(1200, r.MachineBoards);
        Assert.Equal(1200, r.OperatorBoards);
        Assert.Equal(3000, r.LotSizeBoards);
    }

    [Fact]
    public void Machine_ahead_of_PVS_means_PVS_was_blind_and_the_machine_count_is_adopted()
    {
        // PVS was down for a restart; the machine kept counting.
        var r = LotAccuracyCheck.Compare(Input(pvsPanels: 250, machinePanels: 300, previousMachinePanels: 280,
            lotSize: Order(3000)));

        Assert.Equal(AccuracyStatus.PvsBlind, r.Status);
        Assert.True(r.ShouldAdoptMachineCount);
        Assert.Null(r.AdoptBlockedBecause);
        Assert.Equal(1200, r.MachineBoards);
    }

    [Fact]
    public void Machine_ahead_but_past_the_lot_size_is_never_adopted()
    {
        // The L307 failure: a machine counter that was never reset reads far above the lot's real output.
        var r = LotAccuracyCheck.Compare(Input(pvsPanels: 250, machinePanels: 900, previousMachinePanels: 880,
            lotSize: Order(3000)));

        Assert.Equal(AccuracyStatus.OverLotSize, r.Status);     // the over-size alarm outranks the blind-PVS one
        Assert.False(r.ShouldAdoptMachineCount);
        Assert.Contains("over the lot cap", r.AdoptBlockedBecause!);
        // The blind-PVS observation is still in the findings — the headline hides nothing.
        Assert.Contains(r.Findings, f => f.Kind == AccuracyStatus.PvsBlind);
        Assert.True(r.NeedsSupervisor);
    }

    [Fact]
    public void A_machine_counter_reset_mid_lot_is_re_anchored_never_counted_as_production()
    {
        // Operator reset the machine at end of the previous lot (SOP) while this run was still counting.
        var r = LotAccuracyCheck.Compare(Input(pvsPanels: 300, machinePanels: 5, previousMachinePanels: 300,
            operatorBoards: 1200, lotSize: Order(3000)));

        Assert.Equal(AccuracyStatus.MachineReset, r.Status);
        Assert.True(r.ShouldReAnchor);
        Assert.False(r.ShouldAdoptMachineCount);            // adopting a reset would wipe the lot's count
        Assert.Equal(1200, r.PvsBoards);                    // PVS's own count is untouched by the reset
        Assert.Contains(r.Findings, f => f.Kind == AccuracyStatus.MachineReset && f.Action.Contains("NOT production"));
    }

    [Fact]
    public void A_reset_that_lands_close_to_the_PVS_count_is_still_caught()
    {
        // The nasty one: after the reset the numbers happen to look plausible. Only the backwards step tells.
        var r = LotAccuracyCheck.Compare(Input(pvsPanels: 300, machinePanels: 299, previousMachinePanels: 900));
        Assert.Equal(AccuracyStatus.MachineReset, r.Status);
    }

    [Fact]
    public void Operator_ahead_of_PVS_needs_a_human_and_is_never_auto_corrected()
    {
        // The operators are attentive to this count; PVS is not assumed right.
        var r = LotAccuracyCheck.Compare(Input(pvsPanels: 300, machinePanels: 300, previousMachinePanels: 280,
            operatorBoards: 1320, lotSize: Order(3000)));

        Assert.Equal(AccuracyStatus.OperatorDisagrees, r.Status);
        Assert.True(r.NeedsSupervisor);
        Assert.False(r.ShouldAdoptMachineCount);
        Assert.Equal(1320, r.OperatorBoards);               // reported exactly as the operator wrote it
        Assert.Contains(r.Findings, f => f.Kind == AccuracyStatus.OperatorDisagrees && f.Action.Contains("stands as written"));
    }

    [Fact]
    public void The_operators_normal_lag_behind_PVS_is_not_an_alarm()
    {
        // Their app writes a row periodically, not per board, so mid-lot it always trails a little.
        var r = LotAccuracyCheck.Compare(Input(pvsPanels: 300, machinePanels: 300, previousMachinePanels: 280,
            operatorBoards: 1150, lotSize: Order(3000)));

        Assert.Equal(AccuracyStatus.Agree, r.Status);
        Assert.DoesNotContain(r.Findings, f => f.Kind == AccuracyStatus.OperatorDisagrees);
    }

    [Fact]
    public void PVS_far_ahead_of_the_operator_is_reported_too()
    {
        var r = LotAccuracyCheck.Compare(Input(pvsPanels: 300, machinePanels: 300, previousMachinePanels: 280,
            operatorBoards: 800, lotSize: Order(3000)));

        Assert.Equal(AccuracyStatus.OperatorDisagrees, r.Status);
        Assert.Contains("400", r.Findings.First(f => f.Kind == AccuracyStatus.OperatorDisagrees).Message);
        Assert.True(r.NeedsSupervisor);
    }

    [Fact]
    public void A_count_past_the_lot_size_is_flagged_as_over_production_or_a_double_count()
    {
        // 3400 boards against a 3000 lot, past the +10% slack.
        var r = LotAccuracyCheck.Compare(Input(pvsPanels: 850, machinePanels: 850, previousMachinePanels: 800,
            operatorBoards: 3400, lotSize: Order(3000)));

        Assert.Equal(AccuracyStatus.OverLotSize, r.Status);
        Assert.True(r.NeedsSupervisor);
        var f = r.Findings.First(x => x.Kind == AccuracyStatus.OverLotSize);
        Assert.Contains("PVS 3400", f.Message);
        Assert.Contains("operator 3400", f.Message);
    }

    [Fact]
    public void Overproduction_inside_the_slack_is_not_an_alarm()
    {
        // A little over the order is normal. Slack is 2%, so a 3000-board lot tolerates up to 3060.
        var r = LotAccuracyCheck.Compare(Input(pvsPanels: 760, machinePanels: 760, previousMachinePanels: 700,
            lotSize: Order(3000)));   // 3040 boards vs a 3060 cap
        Assert.Equal(AccuracyStatus.Agree, r.Status);
    }

    // Regression: Line 2, lot HC20789022000, 2026-08-07. PVS counted 980 boards against a 900 target
    // because the lot anchor was set ~20 panels early and the previous run's tail was attributed to this
    // lot. Only 900 were actually made. At the old 10% slack this was 1.089 and slipped through silently
    // until the lot closed; at 2% the cap is 918 and it is reported.
    [Fact]
    public void The_line2_anchor_overcount_is_reported_rather_than_slipping_through()
    {
        var r = LotAccuracyCheck.Compare(Input(pvsPanels: 245, machinePanels: 245, previousMachinePanels: 200,
            lotSize: Order(900)));    // 980 boards vs a 918 cap
        Assert.Equal(AccuracyStatus.OverLotSize, r.Status);
        Assert.Contains(r.Findings, f => f.Kind == AccuracyStatus.OverLotSize);
        Assert.Contains(r.Findings, f => f.Action.Contains("anchor", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void An_over_count_still_blocks_adoption()
    {
        var r = LotAccuracyCheck.Compare(Input(pvsPanels: 245, machinePanels: 260, previousMachinePanels: 200,
            lotSize: Order(900)));
        Assert.False(r.ShouldAdoptMachineCount);
        Assert.NotNull(r.AdoptBlockedBecause);
    }

    [Fact]
    public void A_delivered_lot_is_measured_against_DeliveredQty_not_a_zeroed_RevisedQty()
    {
        // Status='Delivered' zeroes RevisedQty (and Quantity here); the real figure is DeliveredQty.
        // Read the live way this lot's size would be 0 and its 2400 boards would read as over-production.
        var delivered = new LotSizeRow("HC20789020000", "Delivered", Quantity: 0, RevisedQty: 0, DeliveredQty: 2480);
        var r = LotAccuracyCheck.Compare(Input(pvsPanels: 600, machinePanels: 600, previousMachinePanels: 580,
            operatorBoards: 2400, lotSize: delivered));

        Assert.Equal(AccuracyStatus.Agree, r.Status);
        Assert.Equal(2480, r.LotSizeBoards);
        Assert.Equal(LotSizeSource.DeliveredQty, r.LotSizeSource);
        Assert.DoesNotContain(r.Findings, f => f.Kind == AccuracyStatus.OverLotSize);
    }

    [Fact]
    public void An_unknown_lot_size_is_reported_and_blocks_adoption()
    {
        // The DB-was-down-at-lot-start window. Without a target there is no cap, so an un-reset machine
        // counter cannot be ruled out and adoption stays refused — the safe direction.
        var r = LotAccuracyCheck.Compare(Input(pvsPanels: 250, machinePanels: 300, previousMachinePanels: 280)
            with { LotSize = null });   // the lot isn't readable from DeliveryDocuments at all

        Assert.Contains(r.Findings, f => f.Kind == AccuracyStatus.LotSizeUnknown);
        Assert.False(r.ShouldAdoptMachineCount);
        Assert.Contains("lot size unknown", r.AdoptBlockedBecause!);
        Assert.Null(r.LotSizeBoards);
    }

    [Fact]
    public void An_unknown_lot_size_is_the_headline_when_nothing_else_is_wrong()
    {
        var r = LotAccuracyCheck.Compare(Input(pvsPanels: 300, machinePanels: 300, previousMachinePanels: 280,
            operatorBoards: 1200) with { LotSize = null });
        Assert.Equal(AccuracyStatus.LotSizeUnknown, r.Status);
    }

    [Fact]
    public void A_refused_machine_read_leaves_that_leg_unchecked_and_says_why()
    {
        var r = LotAccuracyCheck.Compare(Input(pvsPanels: 300, machinePanels: null, operatorBoards: 1200,
            lotSize: Order(3000), error: "A4E00"));

        Assert.Equal(AccuracyStatus.NoMachineRead, r.Status);
        Assert.Null(r.MachineBoards);
        Assert.True(r.NeedsSupervisor);
        Assert.Contains("A4E00", r.Findings.First(f => f.Kind == AccuracyStatus.NoMachineRead).Message);
    }

    [Fact]
    public void With_no_lot_selected_there_is_nothing_to_check()
    {
        var r = LotAccuracyCheck.Compare(Input(pvsPanels: 12, machinePanels: 12, scope: LotScope.For("", "B", 1)));
        Assert.Equal(AccuracyStatus.NoLot, r.Status);
        Assert.Single(r.Findings);
    }

    [Fact]
    public void The_same_lot_on_two_lines_is_two_separate_runs()
    {
        // Observed live: HC20789020000 running on Line 1 B-side and Line 5 A-side at the same time. Keyed on
        // the lot alone, Line 5's operator disagreement would be raised against Line 1's counts as well.
        var line1 = Input(pvsPanels: 300, machinePanels: 300, previousMachinePanels: 280, operatorBoards: 1200,
            scope: LotScope.For("HC20789020000", "B", 1));
        var line5 = Input(pvsPanels: 200, machinePanels: 200, previousMachinePanels: 180, operatorBoards: 1500,
            scope: LotScope.For("HC20789020000", "A", 5));

        var results = LotAccuracyCheck.CompareAll(new[] { line1, line5 });

        Assert.Equal(2, results.Count);
        Assert.NotEqual(results[0].Scope, results[1].Scope);
        Assert.Equal("HC20789020000|B|L1", results[0].Scope.Key);
        Assert.Equal("HC20789020000|A|L5", results[1].Scope.Key);
        Assert.Equal(AccuracyStatus.Agree, results[0].Status);              // Line 1 is fine...
        Assert.Equal(AccuracyStatus.OperatorDisagrees, results[1].Status);  // ...and only Line 5 is not
    }

    [Fact]
    public void The_same_lot_on_both_sides_of_one_line_is_two_separate_runs()
    {
        // One lot number covers the A-side and B-side passes; the B pass starts from zero again.
        var aSide = Input(pvsPanels: 750, machinePanels: 750, previousMachinePanels: 700, operatorBoards: 3000,
            scope: LotScope.For("HC20789020000", "A", 1));
        var bSide = Input(pvsPanels: 100, machinePanels: 100, previousMachinePanels: 80, operatorBoards: 400,
            scope: LotScope.For("HC20789020000", "B", 1));

        var results = LotAccuracyCheck.CompareAll(new[] { aSide, bSide });

        Assert.NotEqual(results[0].Scope, results[1].Scope);
        Assert.All(results, r => Assert.Equal(AccuracyStatus.Agree, r.Status));
        // Summed as one lot they would be 3400 boards against a 3000 order — a false over-production alarm.
        Assert.Equal(3000, results[0].PvsBoards);
        Assert.Equal(400, results[1].PvsBoards);
    }

    [Fact]
    public void Scope_keys_normalise_case_and_the_side_wording()
    {
        Assert.Equal(LotScope.For("HC20789020000", "B", 1), LotScope.For(" hc20789020000 ", "B Side", 1));
        Assert.NotEqual(LotScope.For("HC20789020000", "B", 1), LotScope.For("HC20789020000", "B", 5));
        Assert.NotEqual(LotScope.For("HC20789020000", "B", 1), LotScope.For("HC20789020000", "A", 1));
        Assert.False(LotScope.For("  ", "B", 1).HasLot);
    }

    [Fact]
    public void Every_discrepancy_is_kept_for_the_supervisor_not_just_the_headline()
    {
        // Machine reset AND the operator disagreeing AND no lot size, all in one pass.
        var r = LotAccuracyCheck.Compare(Input(pvsPanels: 300, machinePanels: 4, previousMachinePanels: 300,
            operatorBoards: 1400) with { LotSize = null });

        Assert.Equal(AccuracyStatus.MachineReset, r.Status);
        Assert.Contains(r.Findings, f => f.Kind == AccuracyStatus.OperatorDisagrees);
        Assert.Contains(r.Findings, f => f.Kind == AccuracyStatus.LotSizeUnknown);
        Assert.Contains("OperatorDisagrees", r.Detail);
        Assert.Contains("LotSizeUnknown", r.Detail);
        // The headline carries all four numbers so the log line stands on its own.
        Assert.Contains("PVS 1200", r.Message);
        Assert.Contains("operator 1400", r.Message);
    }

    [Fact]
    public void The_machine_leg_is_the_existing_two_way_reconciler_not_a_second_implementation()
    {
        var r = LotAccuracyCheck.Compare(Input(pvsPanels: 250, machinePanels: 300, previousMachinePanels: 280));
        Assert.Equal(ReconcileStatus.PvsBehind, r.Machine.Status);
        Assert.Equal(50, r.Machine.Delta);       // still panels, as the reconciler reports them
        Assert.Equal(4, r.Machine.Machine);
    }
}
