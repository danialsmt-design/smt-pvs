using Pvs.Core.People;
using Pvs.Core.Verification;
using Xunit;

namespace Pvs.Core.Tests;

public class ModelChangeSessionTests
{
    private static readonly Badge Op = new("14000", "Aziz", "Operator (L1)");
    private static readonly Badge Sup = new("14031", "Noraziah", "Supervisor (L2)");

    private static ModelChangeSession TwoFeeders() => new(new[]
    {
        new ModelChangeItem { Machine = 2, Feeder = 115, ExpectedPart = "R0402-10K" },
        new ModelChangeItem { Machine = 2, Feeder = 116, ExpectedPart = "C0603-100N" },
    });

    [Fact]
    public void Full_happy_path_verifies_part_then_confirms_qty_each_feeder()
    {
        var s = TwoFeeders();
        s.ScanBadge(Op);
        Assert.Equal(115, s.Current!.Feeder);

        // feeder 115: part matches -> qty confirm
        Assert.Equal(StepOutcome.Ok, s.ScanReel("R0402-10K", "u1").Outcome);
        Assert.Equal(ModelChangeState.ConfirmingQty, s.State);
        s.SetScannedQty(1200);
        Assert.Equal(1200, s.Current!.CurrentQty);
        Assert.Equal(StepOutcome.Ok, s.ConfirmQty().Outcome);      // accept qty -> advance
        Assert.Equal(116, s.Current!.Feeder);

        // feeder 116
        s.ScanReel("C0603-100N", "u2");
        s.SetScannedQty(800);
        var last = s.ConfirmQty();

        Assert.Equal(StepOutcome.Completed, last.Outcome);
        Assert.Equal(ModelChangeState.Complete, s.State);
        Assert.Equal(2, s.MatchedCount);
        Assert.Equal(2, s.QtyConfirmedCount);
        Assert.Equal(1200, s.Items[0].ConfirmedQty);
    }

    [Fact]
    public void Part_mismatch_interlocks_supervisor_clears_and_operator_rescans()
    {
        var s = TwoFeeders();
        s.ScanBadge(Op);
        var r = s.ScanReel("WRONG", "u1");
        Assert.Equal(StepOutcome.Interlocked, r.Outcome);
        Assert.Equal(ModelChangeState.PartInterlock, s.State);

        Assert.Equal(StepOutcome.Rejected, s.ScanBadge(Op).Outcome);   // operator cannot clear
        Assert.Equal(StepOutcome.Rejected, s.ClearFeeder(2, 115).Outcome); // nor self-clear
        var clr = s.ScanBadge(Sup);                                    // supervisor CLEARS the wrong scan
        Assert.Equal(StepOutcome.Ok, clr.Outcome);
        Assert.Equal(ModelChangeState.Scanning, s.State);
        Assert.Equal(FeederCheckStatus.Pending, s.Items[0].PartStatus); // reset, NOT released
        Assert.Equal(115, s.Current!.Feeder);                          // still on 115 — rescan

        Assert.Equal(StepOutcome.Ok, s.ScanReel("R0402-10K", "u1b").Outcome);  // correct rescan
        Assert.Equal(ModelChangeState.ConfirmingQty, s.State);
        Assert.Equal(FeederCheckStatus.Matched, s.Items[0].PartStatus);
    }

    [Fact]
    public void Unconfirmed_qty_interlocks_and_supervisor_keys_the_correction()
    {
        var s = TwoFeeders();
        s.ScanBadge(Op);
        s.ScanReel("R0402-10K", "u1");
        s.SetScannedQty(1200);

        var rej = s.RejectQty();                                       // operator: qty is wrong
        Assert.Equal(StepOutcome.Interlocked, rej.Outcome);
        Assert.Equal(ModelChangeState.QtyInterlock, s.State);

        Assert.Equal(StepOutcome.Rejected, s.ScanBadge(Op).Outcome);   // operator cannot correct
        Assert.Equal(StepOutcome.Ok, s.ScanBadge(Sup).Outcome);        // supervisor authorises
        Assert.Equal(ModelChangeState.AwaitingCorrectedQty, s.State);

        var done = s.EnterCorrectedQty(950);
        Assert.Equal(StepOutcome.Ok, done.Outcome);
        Assert.Equal(QtyOutcome.Corrected, s.Items[0].QtyOutcome);
        Assert.Equal(950, s.Items[0].ConfirmedQty);
        Assert.Equal("Noraziah", s.Items[0].QtyCorrectedBy!.Name);
        Assert.Equal(116, s.Current!.Feeder);                          // advanced
    }

    [Fact]
    public void Cannot_confirm_qty_before_part_is_scanned()
    {
        var s = TwoFeeders();
        s.ScanBadge(Op);
        Assert.Equal(StepOutcome.Rejected, s.ConfirmQty().Outcome);
    }

    [Fact]
    public void Nothing_scans_before_badging_in()
    {
        var s = TwoFeeders();
        Assert.Equal(StepOutcome.Rejected, s.ScanReel("R0402-10K", "u1").Outcome);
    }

    [Fact]
    public void Negative_corrected_qty_is_rejected()
    {
        var s = TwoFeeders();
        s.ScanBadge(Op);
        s.ScanReel("R0402-10K", "u1");
        s.SetScannedQty(100);
        s.RejectQty();
        s.ScanBadge(Sup);
        Assert.Equal(StepOutcome.Rejected, s.EnterCorrectedQty(-5).Outcome);
    }

    [Fact]
    public void ClearFeeder_resets_part_and_qty_for_rescan()
    {
        var s = TwoFeeders();
        s.ScanBadge(Op);
        s.ScanReel("R0402-10K", "u1"); s.SetScannedQty(100); s.ConfirmQty();   // 115 fully done -> on 116
        var c = s.ClearFeeder(2, 115);
        Assert.Equal(StepOutcome.Ok, c.Outcome);
        Assert.Equal(115, s.Current!.Feeder);
        Assert.Equal(FeederCheckStatus.Pending, s.Items[0].PartStatus);
        Assert.Equal(QtyOutcome.Pending, s.Items[0].QtyOutcome);
        Assert.Null(s.Items[0].ConfirmedQty);
        Assert.Equal(ModelChangeState.Scanning, s.State);
    }

    // ---- persistence (Export/Restore): a check survives a restart / cancel and resumes from where it stopped ----

    [Fact]
    public void Export_restore_midway_resumes_at_the_exact_feeder_and_continues()
    {
        var s = TwoFeeders();
        s.ScanBadge(Op);
        s.ScanReel("R0402-10K", "u1"); s.SetScannedQty(1200); s.ConfirmQty();  // 115 done -> now on 116

        // Simulate a restart: serialize to JSON, then rebuild.
        var json = System.Text.Json.JsonSerializer.Serialize(s.Export());
        var snap = System.Text.Json.JsonSerializer.Deserialize<ModelChangeSnapshot>(json)!;
        var r = ModelChangeSession.Restore(snap);

        // State carried over exactly.
        Assert.Equal(ModelChangeState.Scanning, r.State);
        Assert.Equal("Aziz", r.Operator!.Name);
        Assert.Equal(116, r.Current!.Feeder);                 // resumes on the next unscanned feeder
        Assert.Equal(FeederCheckStatus.Matched, r.Items[0].PartStatus);
        Assert.Equal(QtyOutcome.Confirmed, r.Items[0].QtyOutcome);
        Assert.Equal(1200, r.Items[0].ConfirmedQty);
        Assert.Equal(FeederCheckStatus.Pending, r.Items[1].PartStatus);

        // And the restored session drives to completion normally.
        r.ScanReel("C0603-100N", "u2"); r.SetScannedQty(800);
        Assert.Equal(StepOutcome.Completed, r.ConfirmQty().Outcome);
        Assert.Equal(ModelChangeState.Complete, r.State);
        Assert.Equal(2, r.MatchedCount);
    }

    [Fact]
    public void Export_restore_preserves_confirmed_machines_and_interlock_state()
    {
        var s = TwoFeeders();
        s.ScanBadge(Op);
        s.ScanReel("WRONG", "u1");                            // interlocked awaiting a supervisor

        var snap = System.Text.Json.JsonSerializer.Deserialize<ModelChangeSnapshot>(
            System.Text.Json.JsonSerializer.Serialize(s.Export()))!;
        var r = ModelChangeSession.Restore(snap);

        Assert.Equal(ModelChangeState.PartInterlock, r.State);
        Assert.Equal(FeederCheckStatus.Mismatched, r.Items[0].PartStatus);
        Assert.Equal(StepOutcome.Rejected, r.ScanBadge(Op).Outcome);   // still needs a supervisor
        Assert.Equal(StepOutcome.Ok, r.ScanBadge(Sup).Outcome);        // supervisor clears after restore
        Assert.Equal(ModelChangeState.Scanning, r.State);
    }
}
