using Pvs.Core.Runtime;
using Xunit;

namespace Pvs.Core.Tests;

/// <summary>
/// Scenarios are the real ones from Line 1: the operator resets every machine's counter at end of lot
/// (written SOP), and PVS goes blind on restarts / serial drops. See CounterReconciler's remarks.
/// </summary>
public class CounterReconcilerTests
{
    [Fact]
    public void Matching_counts_agree()
    {
        // The live 2026-07-31 reading: machine said 155, PVS's lot counter said 155.
        var r = CounterReconciler.Reconcile(4, machineCount: 155, pvsCount: 155);
        Assert.Equal(ReconcileStatus.Agree, r.Status);
        Assert.Equal(0, r.Delta);
        Assert.False(r.NeedsAttention);
        Assert.False(r.ShouldAdoptMachineCount);
    }

    [Fact]
    public void Small_difference_inside_tolerance_still_agrees()
    {
        // A read takes ~30s over serial, so a running line moves on a little between observations.
        var r = CounterReconciler.Reconcile(4, machineCount: 157, pvsCount: 155);
        Assert.Equal(ReconcileStatus.Agree, r.Status);
    }

    [Fact]
    public void Machine_ahead_means_PVS_missed_boards_while_blind()
    {
        var r = CounterReconciler.Reconcile(4, machineCount: 200, pvsCount: 155, previousMachineCount: 180);
        Assert.Equal(ReconcileStatus.PvsBehind, r.Status);
        Assert.Equal(45, r.Delta);
        Assert.True(r.ShouldAdoptMachineCount);   // the machine is definitively right here
    }

    [Fact]
    public void Counter_going_backwards_is_an_operator_reset_not_production()
    {
        // End of lot: operator resets all four machines per SOP.
        var r = CounterReconciler.Reconcile(4, machineCount: 12, pvsCount: 300, previousMachineCount: 300);
        Assert.Equal(ReconcileStatus.ResetDetected, r.Status);
        Assert.Contains("reset", r.Message, StringComparison.OrdinalIgnoreCase);
        // A reset must never be adopted as "PVS missed boards" — that would invent production.
        Assert.False(r.ShouldAdoptMachineCount);
    }

    [Fact]
    public void A_reset_is_detected_even_when_the_new_count_is_close_to_PVS()
    {
        // The nasty case: a reset lands where the numbers happen to look plausible. Only the
        // backwards step reveals it, which is why the previous reading must be passed in.
        var r = CounterReconciler.Reconcile(4, machineCount: 154, pvsCount: 155, previousMachineCount: 300);
        Assert.Equal(ReconcileStatus.ResetDetected, r.Status);
    }

    [Fact]
    public void PVS_ahead_with_no_reset_evidence_is_flagged_not_guessed()
    {
        var r = CounterReconciler.Reconcile(4, machineCount: 100, pvsCount: 155, previousMachineCount: 90);
        Assert.Equal(ReconcileStatus.PvsAhead, r.Status);
        Assert.True(r.NeedsAttention);
        Assert.False(r.ShouldAdoptMachineCount);   // never auto-correct this one
    }

    [Fact]
    public void PVS_ahead_without_a_previous_reading_says_a_reset_cannot_be_ruled_out()
    {
        var r = CounterReconciler.Reconcile(4, machineCount: 5, pvsCount: 155);
        Assert.Equal(ReconcileStatus.PvsAhead, r.Status);
        Assert.Contains("unobserved reset", r.Message);
    }

    [Fact]
    public void A_refused_read_reports_the_reject_code()
    {
        // M1-M3 were doing exactly this on 2026-07-31.
        var r = CounterReconciler.Reconcile(1, machineCount: null, pvsCount: 155, error: "A4E00");
        Assert.Equal(ReconcileStatus.NoRead, r.Status);
        Assert.Equal("A4E00", r.Error);
        Assert.Contains("A4E00", r.Message);
        Assert.True(r.NeedsAttention);
    }

    [Fact]
    public void A_silent_machine_reports_no_read_without_an_error_code()
    {
        var r = CounterReconciler.Reconcile(1, machineCount: null, pvsCount: 155);
        Assert.Equal(ReconcileStatus.NoRead, r.Status);
        Assert.Null(r.Error);
    }

    [Fact]
    public void Cross_machine_spread_shows_a_machine_left_behind()
    {
        var results = new[]
        {
            CounterReconciler.Reconcile(1, 158, 155),
            CounterReconciler.Reconcile(2, 157, 155),
            CounterReconciler.Reconcile(3, 156, 155),
            CounterReconciler.Reconcile(4,  90, 155),   // stopped, bypassed, or never reset
        };
        var spread = CounterReconciler.CrossMachineSpread(results);
        Assert.NotNull(spread);
        Assert.Equal(90, spread!.Value.Min);
        Assert.Equal(158, spread.Value.Max);
        Assert.Equal(68, spread.Value.Spread);
    }

    [Fact]
    public void Cross_machine_spread_needs_at_least_two_readings()
    {
        var results = new[]
        {
            CounterReconciler.Reconcile(1, null, 155, error: "A4E00"),
            CounterReconciler.Reconcile(2, 157, 155),
        };
        Assert.Null(CounterReconciler.CrossMachineSpread(results));
    }
}
