using Pvs.Core.People;
using Pvs.Core.Verification;
using Xunit;

namespace Pvs.Core.Tests;

public class FullScanSessionTests
{
    private static readonly Badge Op = new("14000", "Aziz", "Operator (L1)");
    private static readonly Badge Sup = new("14031", "Noraziah", "Supervisor (L2)");

    private static FullScanSession ThreeFeeders() => new(ScanPurpose.ShiftChange, new[]
    {
        new FeederCheck { Machine = 2, Feeder = 115, ExpectedPart = "R0402-10K" },
        new FeederCheck { Machine = 2, Feeder = 116, ExpectedPart = "C0603-100N" },
        new FeederCheck { Machine = 2, Feeder = 117, ExpectedPart = "R0402-4K7" },
    });

    [Fact]
    public void Walks_feeders_in_order_and_completes_when_all_match()
    {
        var s = ThreeFeeders();
        s.ScanBadge(Op);
        Assert.Equal(115, s.Current!.Feeder);

        Assert.Equal(StepOutcome.Ok, s.ScanReel("R0402-10K", "u1").Outcome);
        Assert.Equal(116, s.Current!.Feeder);                     // advanced
        Assert.Equal(StepOutcome.Ok, s.ScanReel("C0603-100N", "u2").Outcome);
        var last = s.ScanReel("R0402-4K7", "u3");

        Assert.Equal(StepOutcome.Completed, last.Outcome);
        Assert.Equal(FullScanState.Complete, s.State);
        Assert.Equal(3, s.MatchedCount);
    }

    [Fact]
    public void Mismatch_interlocks_that_feeder()
    {
        var s = ThreeFeeders();
        s.ScanBadge(Op);
        var r = s.ScanReel("WRONG", "u1");
        Assert.Equal(StepOutcome.Interlocked, r.Outcome);
        Assert.Equal(FullScanState.Interlocked, s.State);
        Assert.Equal(FeederCheckStatus.Mismatched, s.Items[0].Status);
    }

    [Fact]
    public void Cannot_scan_past_an_interlock_without_a_supervisor()
    {
        var s = ThreeFeeders();
        s.ScanBadge(Op);
        s.ScanReel("WRONG", "u1");                    // interlock on feeder 115
        var r = s.ScanReel("C0603-100N", "u2");       // try to scan the next feeder
        Assert.Equal(StepOutcome.Rejected, r.Outcome);
        Assert.Equal(115, s.Current!.Feeder);          // still stuck on 115
    }

    [Fact]
    public void Supervisor_clears_wrong_scan_and_requires_rescan()
    {
        var s = ThreeFeeders();
        s.ScanBadge(Op);
        s.ScanReel("WRONG", "u1");                    // interlock
        Assert.Equal(StepOutcome.Rejected, s.ScanBadge(Op).Outcome);   // operator can't clear

        var clr = s.ScanBadge(Sup);                   // supervisor CLEARS the wrong scan
        Assert.Equal(StepOutcome.Ok, clr.Outcome);
        Assert.Equal(FullScanState.Scanning, s.State);
        Assert.Equal(FeederCheckStatus.Pending, s.Items[0].Status);    // reset, NOT released/accepted
        Assert.Null(s.Items[0].ScannedPart);
        Assert.Equal(115, s.Current!.Feeder);          // still on 115 — must rescan
        Assert.Equal("Noraziah", s.Items[0].ReleasedBy!.Name);         // who cleared it

        Assert.Equal(StepOutcome.Ok, s.ScanReel("R0402-10K", "u1b").Outcome);  // correct rescan
        Assert.Equal(FeederCheckStatus.Matched, s.Items[0].Status);
        Assert.Equal(116, s.Current!.Feeder);          // now advances
    }

    [Fact]
    public void Complete_after_supervisor_clears_and_operator_rescans_correctly()
    {
        var s = new FullScanSession(ScanPurpose.LotEnd, new[]
        {
            new FeederCheck { Machine = 3, Feeder = 111, ExpectedPart = "WA7-8586-000" }
        });
        s.ScanBadge(Op);
        s.ScanReel("WRONG", "u1");        // interlock on the only feeder
        s.ScanBadge(Sup);                 // supervisor clears -> back to scanning
        Assert.Equal(FullScanState.Scanning, s.State);
        var ok = s.ScanReel("WA7-8586-000", "u1b");   // correct rescan -> complete
        Assert.Equal(StepOutcome.Completed, ok.Outcome);
        Assert.Equal(FullScanState.Complete, s.State);
        Assert.Equal(1, s.MatchedCount);
    }

    [Fact]
    public void Nothing_scans_before_badging_in()
    {
        var s = ThreeFeeders();
        Assert.Equal(StepOutcome.Rejected, s.ScanReel("R0402-10K", "u1").Outcome);
    }

    [Fact]
    public void ClearFeeder_resets_a_matched_feeder_and_lets_it_be_rescanned()
    {
        var s = ThreeFeeders();
        s.ScanBadge(Op);
        s.ScanReel("R0402-10K", "u1");                 // 115 matched -> now on 116
        var c = s.ClearFeeder(2, 115);
        Assert.Equal(StepOutcome.Ok, c.Outcome);
        Assert.Equal(115, s.Current!.Feeder);          // jumped back to 115
        Assert.Equal(FeederCheckStatus.Pending, s.Items[0].Status);
        Assert.Equal(StepOutcome.Ok, s.ScanReel("R0402-10K", "u1b").Outcome);
        Assert.Equal(FeederCheckStatus.Matched, s.Items[0].Status);
    }

    [Fact]
    public void GoToMachine_lets_operator_choose_which_machine_to_scan_first()
    {
        var s = new FullScanSession(ScanPurpose.ShiftChange, new[]
        {
            new FeederCheck { Machine = 1, Feeder = 10, ExpectedPart = "A" },
            new FeederCheck { Machine = 3, Feeder = 20, ExpectedPart = "B" },
            new FeederCheck { Machine = 3, Feeder = 21, ExpectedPart = "C" },
        });
        s.ScanBadge(Op);
        Assert.Equal(1, s.Current!.Machine);                 // defaults to first machine
        Assert.Equal(StepOutcome.Ok, s.GoToMachine(3).Outcome);
        Assert.Equal(3, s.Current!.Machine);                 // jumped to machine 3
        Assert.Equal(20, s.Current!.Feeder);                 // its first pending feeder
    }

    [Fact]
    public void ConfirmMachine_only_saves_once_all_its_feeders_pass()
    {
        var s = ThreeFeeders();                 // all on machine 2
        s.ScanBadge(Op);
        Assert.Equal(StepOutcome.Rejected, s.ConfirmMachine(2).Outcome);   // not done yet
        s.ScanReel("R0402-10K", "u1"); s.ScanReel("C0603-100N", "u2"); s.ScanReel("R0402-4K7", "u3");
        Assert.Equal(StepOutcome.Ok, s.ConfirmMachine(2).Outcome);
        Assert.Contains(2, s.ConfirmedMachines);
    }

    [Fact]
    public void ClearFeeder_cannot_skip_ahead_to_an_unscanned_feeder()
    {
        var s = ThreeFeeders();                 // feeders 115, 116, 117 (all on M2)
        s.ScanBadge(Op);                        // cursor at 115
        Assert.Equal(StepOutcome.Rejected, s.ClearFeeder(2, 117).Outcome);  // can't jump ahead
        Assert.Equal(115, s.Current!.Feeder);   // still in sequence
    }

    [Fact]
    public void ClearFeeder_cannot_self_clear_a_wrong_scan_supervisor_required()
    {
        var s = ThreeFeeders();
        s.ScanBadge(Op);
        s.ScanReel("WRONG", "u1");                     // interlock on 115
        var c = s.ClearFeeder(2, 115);                 // operator cannot clear a wrong scan
        Assert.Equal(StepOutcome.Rejected, c.Outcome);
        Assert.Equal(FullScanState.Interlocked, s.State);
        // only a supervisor badge clears it, then the operator rescans
        Assert.Equal(StepOutcome.Ok, s.ScanBadge(Sup).Outcome);
        Assert.Equal(StepOutcome.Ok, s.ScanReel("R0402-10K", "u1b").Outcome);
    }

    [Fact]
    public void Export_restore_midway_resumes_and_completes()
    {
        var s = ThreeFeeders();
        s.ScanBadge(Op);
        s.ScanReel("R0402-10K", "u1");                 // 115 matched -> now on 116

        // Simulate a restart: JSON round-trip, then rebuild.
        var snap = System.Text.Json.JsonSerializer.Deserialize<FullScanSnapshot>(
            System.Text.Json.JsonSerializer.Serialize(s.Export()))!;
        var r = FullScanSession.Restore(snap);

        Assert.Equal(ScanPurpose.ShiftChange, r.Purpose);
        Assert.Equal(FullScanState.Scanning, r.State);
        Assert.Equal("Aziz", r.Operator!.Name);
        Assert.Equal(116, r.Current!.Feeder);          // resumes exactly where it stopped
        Assert.Equal(FeederCheckStatus.Matched, r.Items[0].Status);
        Assert.Equal("u1", r.Items[0].ReelUid);

        r.ScanReel("C0603-100N", "u2");
        Assert.Equal(StepOutcome.Completed, r.ScanReel("R0402-4K7", "u3").Outcome);
        Assert.Equal(FullScanState.Complete, r.State);
        Assert.Equal(3, r.MatchedCount);
    }

    [Fact]
    public void SyncFeeders_refreshes_an_in_progress_scan_and_resumes_at_the_amended_feeder()
    {
        var s = ThreeFeeders();   // 115,116,117
        s.ScanBadge(Op);
        s.ScanReel("R0402-10K", "u1");   // 115 matched -> on 116

        s.SyncFeeders(new[] { (2, 115, "R0402-10K"), (2, 116, "C0603-XXX"), (2, 117, "R0402-4K7") });

        Assert.Equal(FeederCheckStatus.Matched, s.Items[0].Status);   // 115 kept
        Assert.Equal(FeederCheckStatus.Pending, s.Items[1].Status);   // 116 reset
        Assert.Equal("C0603-XXX", s.Items[1].ExpectedPart);
        Assert.Equal(116, s.Current!.Feeder);                        // resume at the amended feeder
        Assert.Equal(FullScanState.Scanning, s.State);
    }
}
