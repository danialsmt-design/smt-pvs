namespace Pvs.Core.Runtime;

/// <summary>What a comparison of PVS's board count against the machine's own counter means.</summary>
public enum ReconcileStatus
{
    /// <summary>The machine didn't give us a count (rejected the C1M, or the report never arrived).</summary>
    NoRead,
    /// <summary>Counts match within tolerance — the healthy case.</summary>
    Agree,
    /// <summary>The machine's counter went BACKWARDS since our last read: the operator reset it
    /// (SOP at end of lot). The drop is NOT production and must never be counted as such.</summary>
    ResetDetected,
    /// <summary>The machine counted MORE than PVS: PVS was blind (app stopped, PC rebooted, serial
    /// dropped) and missed boards. The machine's figure is the truthful one.</summary>
    PvsBehind,
    /// <summary>PVS counted more than the machine, with no reset observed. Either a reset happened
    /// while we weren't looking, or PVS double-counted. Needs a human look — never auto-corrected.</summary>
    PvsAhead,
}

/// <summary>
/// One comparison of PVS's own count against a machine's internal counter, for one machine.
/// <see cref="Delta"/> is machine minus PVS (positive = the machine saw more).
/// </summary>
public sealed record ReconcileResult(
    int Machine,
    ReconcileStatus Status,
    int? MachineCount,
    int PvsCount,
    int? PreviousMachineCount,
    int Delta,
    string Message,
    string? Error = null)
{
    /// <summary>True when PVS's baseline should be re-anchored to the machine's figure. Only for
    /// <see cref="ReconcileStatus.PvsBehind"/> — the one case where the machine is definitively right.
    /// A reset re-anchors too, but to the machine's NEW (post-reset) value, not as missed production.</summary>
    public bool ShouldAdoptMachineCount => Status == ReconcileStatus.PvsBehind;

    /// <summary>True when this result needs a human to look at it.</summary>
    public bool NeedsAttention => Status is ReconcileStatus.PvsAhead or ReconcileStatus.NoRead;
}

/// <summary>
/// Compares PVS's live board count against a Sony machine's own production counter (read via C1M) and
/// says what the difference means.
/// <para>
/// The two sources fail in OPPOSITE ways, which is the whole point of comparing them:
/// PVS counts R0 events in real time and never resets, but misses boards whenever it is off, the PC
/// reboots, or a serial link drops. The machine's counter survives all of that, but the operator
/// RESETS it at the end of every lot, on all four machines, per written SOP — so it can go backwards
/// at any moment, and a naive delta across a reset reads as negative (or, worse, as a small positive
/// that silently hides a lot's worth of production).
/// </para>
/// <para>
/// Nothing here mutates anything. It classifies and explains; the caller decides what to do and is
/// expected to LOG every result — the discrepancies are themselves the useful data.
/// </para>
/// </summary>
public static class CounterReconciler
{
    /// <summary>Default slack, in panels, before a difference is treated as real. A read takes ~30 s
    /// over serial, so a producing line legitimately moves on a little between the two observations.</summary>
    public const int DefaultTolerance = 3;

    /// <param name="machine">Machine number, for the result.</param>
    /// <param name="machineCount">The machine's own counter (C1M), or null if it didn't answer.</param>
    /// <param name="pvsCount">PVS's own count for the same scope (panels/cycles).</param>
    /// <param name="previousMachineCount">The machine's counter at our PREVIOUS read, if we have one.
    /// This is the only hard evidence of an operator reset, so pass it whenever it's known.</param>
    /// <param name="tolerance">Slack in panels; negative values are treated as 0.</param>
    /// <param name="error">The machine's reject code (e.g. A4E00), when there was no read.</param>
    public static ReconcileResult Reconcile(
        int machine,
        int? machineCount,
        int pvsCount,
        int? previousMachineCount = null,
        int tolerance = DefaultTolerance,
        string? error = null)
    {
        if (tolerance < 0) tolerance = 0;

        if (machineCount is not int mc)
            return new ReconcileResult(machine, ReconcileStatus.NoRead, null, pvsCount, previousMachineCount, 0,
                error is null
                    ? "no count from the machine (no reply within the read window)"
                    : $"no count from the machine — it refused with {error}",
                error);

        // A counter that went backwards is the ONE unambiguous signal of an operator reset. Check it
        // before any difference test, because after a reset the machine-vs-PVS gap is huge and would
        // otherwise be misread as PVS double-counting.
        if (previousMachineCount is int prev && mc < prev)
            return new ReconcileResult(machine, ReconcileStatus.ResetDetected, mc, pvsCount, prev, mc - pvsCount,
                $"machine counter dropped {prev} -> {mc}: operator reset (SOP at end of lot). " +
                "Re-anchor to the new value; the drop is not production.");

        int delta = mc - pvsCount;

        if (Math.Abs(delta) <= tolerance)
            return new ReconcileResult(machine, ReconcileStatus.Agree, mc, pvsCount, previousMachineCount, delta,
                $"agree: machine {mc}, PVS {pvsCount} (within {tolerance})");

        if (delta > 0)
            return new ReconcileResult(machine, ReconcileStatus.PvsBehind, mc, pvsCount, previousMachineCount, delta,
                $"machine {mc} vs PVS {pvsCount}: PVS missed {delta} panel(s) while blind — adopt the machine's count");

        // PVS ahead. Without a previous machine reading we cannot distinguish "reset we didn't see"
        // from "PVS double-counted", so say so plainly rather than guessing.
        string why = previousMachineCount is null
            ? "no previous machine reading, so an unobserved reset cannot be ruled out"
            : "no reset observed since the last read";
        return new ReconcileResult(machine, ReconcileStatus.PvsAhead, mc, pvsCount, previousMachineCount, delta,
            $"PVS {pvsCount} vs machine {mc}: PVS is {-delta} panel(s) ahead — {why}. Needs checking.");
    }

    /// <summary>
    /// The spread across machines on the SAME lot. Because every machine's counter is reset at lot start
    /// and boards run M1-&gt;M4 in series, all four should track each other; a machine far behind was
    /// stopped, bypassed, or never had its counter reset.
    /// </summary>
    /// <returns>Null when fewer than two machines reported a count.</returns>
    public static (int Min, int Max, int Spread)? CrossMachineSpread(IEnumerable<ReconcileResult> results)
    {
        var counts = results.Where(r => r.MachineCount is not null).Select(r => r.MachineCount!.Value).ToList();
        if (counts.Count < 2) return null;
        int min = counts.Min(), max = counts.Max();
        return (min, max, max - min);
    }
}
