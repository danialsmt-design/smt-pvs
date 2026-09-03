using Pvs.Core.Data;

namespace Pvs.Core.Runtime;

/// <summary>
/// The identity of ONE production run: a lot number, the side being built, and the line building it.
/// <para>
/// The lot number ALONE is not a run. The same lot legitimately runs on both sides (one lot number covers
/// the A-side and B-side passes of the same boards) AND on two lines at once — observed live: lot
/// HC20789020000 running on Line 1 B-side and Line 5 A-side simultaneously. Comparing counts keyed on the
/// lot alone therefore compares Line 1's boards against Line 5's target and raises a false alarm on every
/// pass, so every figure in this file is keyed on the whole triple.
/// </para>
/// </summary>
public readonly record struct LotScope(string Lot, string Side, int Line)
{
    /// <summary>Builds a scope with the normalisation that makes equality reliable: lot trimmed and upper-cased
    /// (the DB stores it inconsistently cased), side reduced to its first letter ("B Side" -> "B"), line as-is.</summary>
    public static LotScope For(string? lot, string? side, int line)
    {
        string l = (lot ?? "").Trim().ToUpperInvariant();
        string s = (side ?? "").Trim();
        return new LotScope(l, s.Length > 0 ? s[..1].ToUpperInvariant() : "", line);
    }

    /// <summary>False when there is no lot to check (e.g. right after a model change).</summary>
    public bool HasLot => Lot.Length > 0;

    /// <summary>Stable one-token key for logs, JSONL and dictionaries — e.g. <c>HC20789020000|B|L1</c>.</summary>
    public string Key => $"{Lot}|{Side}|L{Line}";

    public override string ToString() => Key;
}

/// <summary>What a four-way comparison of one production run came out as.</summary>
public enum AccuracyStatus
{
    /// <summary>Every source that reported agrees within tolerance — the healthy case.</summary>
    Agree,
    /// <summary>No lot is being tracked, so there is nothing to compare.</summary>
    NoLot,
    /// <summary>No usable lot size, so nothing can be checked against the target and no adoption cap exists.</summary>
    LotSizeUnknown,
    /// <summary>The machine didn't give up its counter this pass (refused, or no reply).</summary>
    NoMachineRead,
    /// <summary>The machine counted MORE than PVS: PVS was blind and missed boards. Adopt the machine's figure.</summary>
    PvsBlind,
    /// <summary>The machine's counter stepped BACKWARDS: the operator reset it. Re-anchor; never production.</summary>
    MachineReset,
    /// <summary>PVS counted more than the machine with no reset observed — an unseen reset or a double-count.</summary>
    PvsAhead,
    /// <summary>The operator's own recorded count disagrees with PVS. A human decides; never auto-corrected.</summary>
    OperatorDisagrees,
    /// <summary>A count has passed the lot size: genuine over-production, or a double-count / un-reset counter.</summary>
    OverLotSize,
}

/// <summary>One thing found wrong (or worth saying) about a run, and what should happen about it.</summary>
public sealed record AccuracyFinding(AccuracyStatus Kind, string Message, string Action);

/// <summary>Everything known about one run at one instant, as the raw figures each source reports.</summary>
/// <param name="Scope">Lot + side + line. Counts from different scopes are never compared.</param>
/// <param name="PvsPanels">PVS's own R0 board-complete count off the LAST machine, in panels.</param>
/// <param name="PerPanel">Child boards per panel for the model (LineConfig.PanelBoardsFor). Minimum 1.</param>
/// <param name="MachinePanels">The last machine's own C1M counter, in panels (PWBs), or null if it didn't answer.</param>
/// <param name="PreviousMachinePanels">That counter at the PREVIOUS read — the only hard evidence of a reset.</param>
/// <param name="OperatorBoards">Boards the OPERATOR's application recorded in DailyProductionCount for this
/// scope, in boards. Null when the operator has written nothing yet (which is not a discrepancy — mid-lot
/// their rows simply haven't been keyed in).</param>
/// <param name="LotSize">The raw DeliveryDocuments row for the lot, or null if it wasn't found.</param>
/// <param name="Machine">The machine number the counter came from (the last machine on the line).</param>
/// <param name="MachineError">The machine's reject code (e.g. A4E00) when there was no read.</param>
public sealed record LotAccuracyInput(
    LotScope Scope,
    int PvsPanels,
    int PerPanel,
    int? MachinePanels,
    int? PreviousMachinePanels,
    int? OperatorBoards,
    LotSizeRow? LotSize,
    int Machine = 4,
    string? MachineError = null);

/// <summary>Slack allowed before a difference counts as real. All figures are BOARDS except where noted.</summary>
/// <param name="MachineTolerancePanels">Machine-vs-PVS slack in PANELS — a C1M read takes ~30 s over serial,
/// so a producing line moves on between the two observations. Same default as the two-way reconciler.</param>
/// <param name="OperatorAheadToleranceBoards">How far the operator's total may exceed PVS's before it is
/// reported. Kept tight: the operator being AHEAD means PVS missed real boards, which is never normal.
/// The small default absorbs the rounding between a board count and a panel count.</param>
/// <param name="OperatorBehindToleranceBoards">How far the operator's total may TRAIL PVS before it is
/// reported. Kept loose: the operator app writes a row periodically, not per board, so mid-lot it always
/// trails. Alarming on that lag would bury the real disagreements in noise.</param>
/// <param name="OverProductionSlack">Fraction over the lot size that still counts as normal overproduction
/// before anyone is told.
/// <para>
/// Deliberately TIGHTER than the +10% ceiling on the adopt guard, because the two answer different
/// questions: "should a human look at this?" and "may I overwrite the count?". Reporting should be eager;
/// overwriting should be reluctant.
/// </para>
/// <para>
/// 10% was too loose to be useful. Live case (Line 2, lot HC20789022000, 2026-08-07): PVS counted 980
/// boards against a 900 target because the lot anchor was set ~20 panels early and the tail of the previous
/// run was attributed to this lot. 980/900 = 1.089 — it slipped under a 1.10 cap and nobody was told until
/// the lot closed. On a 900-board lot, 10% is 90 boards of silent drift.
/// </para></param>
public sealed record LotAccuracyOptions(
    int MachineTolerancePanels = CounterReconciler.DefaultTolerance,
    int OperatorAheadToleranceBoards = 4,
    int OperatorBehindToleranceBoards = 200,
    double OverProductionSlack = 0.02)
{
    public static readonly LotAccuracyOptions Default = new();
}

/// <summary>
/// The outcome of one four-way comparison. Every figure is carried through in BOARDS (the unit the lot size,
/// the operator and the customer all use) so a supervisor reading the log never has to convert anything.
/// </summary>
public sealed record LotAccuracyResult(
    LotScope Scope,
    AccuracyStatus Status,
    int PvsBoards,
    int? MachineBoards,
    int? OperatorBoards,
    int? LotSizeBoards,
    LotSizeSource LotSizeSource,
    ReconcileResult Machine,
    IReadOnlyList<AccuracyFinding> Findings,
    bool ShouldAdoptMachineCount,
    string? AdoptBlockedBecause,
    string Message)
{
    /// <summary>True when PVS's baseline should be re-anchored to the machine's NEW (post-reset) value. The
    /// drop itself is not production and must never be added to anything.</summary>
    public bool ShouldReAnchor => Machine.Status == ReconcileStatus.ResetDetected;

    /// <summary>True when this run needs a person to look at it. Nothing here is ever fixed automatically.</summary>
    public bool NeedsSupervisor => Findings.Any(f =>
        f.Kind is AccuracyStatus.OperatorDisagrees or AccuracyStatus.OverLotSize
               or AccuracyStatus.PvsAhead or AccuracyStatus.NoMachineRead);

    /// <summary>The findings as one log line, so nothing is lost to the single headline <see cref="Status"/>.</summary>
    public string Detail => string.Join(" | ", Findings.Select(f => $"{f.Kind}: {f.Message} -> {f.Action}"));
}

/// <summary>
/// Continuous accuracy check for board counting: compares the FOUR independent numbers that describe how
/// much a production run has made, and says which one to believe.
/// <list type="number">
///   <item>LOT SIZE — the order's target, from DeliveryDocuments (see <see cref="LotSizeResolver"/>).</item>
///   <item>PVS — R0 board-completes counted live off the LAST machine. Misses everything produced while
///         PVS is off, the PC reboots, or a serial link drops.</item>
///   <item>MACHINE — the machine's own C1M counter. Survives PVS being off, but the operator RESETS it at
///         end of lot per SOP, so it steps backwards without warning.</item>
///   <item>OPERATOR — what the operator's own application wrote to DailyProductionCount. The operators are
///         attentive to this figure and it is treated as CREDIBLE: when it disagrees with PVS, PVS is not
///         assumed right, and the operator's number is NEVER rewritten.</item>
/// </list>
/// <para>
/// The machine-vs-PVS leg is not re-implemented here — it delegates to <see cref="CounterReconciler"/>, so
/// reset detection and its statuses stay in one place. This adds the two legs it never had: the order's
/// target, and the human count.
/// </para>
/// <para>
/// Nothing in this file mutates anything, talks to a machine, or writes to a database. It classifies and
/// explains; the caller acts and is expected to LOG every result — a discrepancy nobody recorded is the
/// failure mode this whole check exists to remove.
/// </para>
/// </summary>
public static class LotAccuracyCheck
{
    // Headline order: the most consequential finding wins the single Status field. A reset first, because it
    // invalidates every raw comparison after it; then the two that corrupt figures downstream (an over-size
    // count reaches material planning and the customer; a disagreeing operator means one of the two counts
    // in the DB is wrong); then the merely suspicious; then the merely incomplete. Findings keeps the rest.
    private static readonly AccuracyStatus[] Precedence =
    {
        AccuracyStatus.NoLot,
        AccuracyStatus.MachineReset,
        AccuracyStatus.OverLotSize,
        AccuracyStatus.OperatorDisagrees,
        AccuracyStatus.PvsAhead,
        AccuracyStatus.PvsBlind,
        AccuracyStatus.NoMachineRead,
        AccuracyStatus.LotSizeUnknown,
    };

    /// <summary>Compares one run's four numbers. Pure — safe to call as often as you like.</summary>
    public static LotAccuracyResult Compare(LotAccuracyInput input, LotAccuracyOptions? options = null)
    {
        var o = options ?? LotAccuracyOptions.Default;
        int perPanel = input.PerPanel > 0 ? input.PerPanel : 1;
        var findings = new List<AccuracyFinding>();

        // The machine leg, delegated verbatim to the existing two-way reconciler (panels in, panels out).
        var machine = CounterReconciler.Reconcile(input.Machine, input.MachinePanels, Math.Max(0, input.PvsPanels),
            input.PreviousMachinePanels, o.MachineTolerancePanels, input.MachineError);

        int pvsBoards = Math.Max(0, input.PvsPanels) * perPanel;
        int? machineBoards = machine.MachineCount is int mc ? mc * perPanel : null;
        int? opBoards = input.OperatorBoards;
        var size = LotSizeResolver.Resolve(input.LotSize);

        if (!input.Scope.HasLot)
        {
            findings.Add(new AccuracyFinding(AccuracyStatus.NoLot,
                "no lot is being tracked on this line", "select the running lot; counts are not attributable until then"));
            return Build(input.Scope, pvsBoards, machineBoards, opBoards, size, machine, findings, false,
                "no lot is being tracked");
        }

        // ---- leg 1: machine vs PVS ----
        switch (machine.Status)
        {
            case ReconcileStatus.ResetDetected:
                findings.Add(new AccuracyFinding(AccuracyStatus.MachineReset, machine.Message,
                    "re-anchor to the machine's new value; the drop is NOT production and must never be added to any count"));
                break;
            case ReconcileStatus.PvsBehind:
                findings.Add(new AccuracyFinding(AccuracyStatus.PvsBlind, machine.Message,
                    "adopt the machine's count for the lot (subject to the lot-size cap below)"));
                break;
            case ReconcileStatus.PvsAhead:
                findings.Add(new AccuracyFinding(AccuracyStatus.PvsAhead, machine.Message,
                    "check by hand: either a reset happened unseen or PVS double-counted — never auto-corrected"));
                break;
            case ReconcileStatus.NoRead:
                findings.Add(new AccuracyFinding(AccuracyStatus.NoMachineRead, machine.Message,
                    "check the machine is in AUTO and the serial link is up; the machine leg is unchecked this pass"));
                break;
        }

        // ---- leg 2: operator vs PVS ----
        // The operator's figure is CREDIBLE and is never rewritten by anything here. Both directions are
        // reported, but with very different thresholds — see LotAccuracyOptions.
        if (opBoards is int op)
        {
            int delta = op - pvsBoards;
            if (delta > o.OperatorAheadToleranceBoards)
                findings.Add(new AccuracyFinding(AccuracyStatus.OperatorDisagrees,
                    $"operator recorded {op} boards, PVS counted {pvsBoards} — the operator is {delta} AHEAD",
                    "supervisor to reconcile: PVS most likely missed boards while blind. The operator's figure stands as written."));
            else if (-delta > o.OperatorBehindToleranceBoards)
                findings.Add(new AccuracyFinding(AccuracyStatus.OperatorDisagrees,
                    $"PVS counted {pvsBoards} boards, operator recorded {op} — PVS is {-delta} AHEAD of the operator",
                    "supervisor to reconcile: either rows are still to be keyed in, or PVS is over-counting. Never corrected automatically."));
        }

        // ---- leg 3: everything against the lot size ----
        long cap = 0;
        if (size.IsKnown)
        {
            int target = size.Boards!.Value;
            cap = (long)Math.Ceiling(target * (1 + o.OverProductionSlack));
            var over = new List<string>();
            if (pvsBoards > cap) over.Add($"PVS {pvsBoards}");
            if (machineBoards is int mb && mb > cap) over.Add($"machine {mb}");
            if (opBoards is int ob && ob > cap) over.Add($"operator {ob}");
            if (over.Count > 0)
                findings.Add(new AccuracyFinding(AccuracyStatus.OverLotSize,
                    $"{string.Join(", ", over)} against a lot size of {target} ({size.Source}, cap {cap})",
                    "supervisor to confirm genuine over-production; otherwise a double-count, a lot anchor set " +
                    "before the lot actually started, or a machine counter that was never reset"));
        }
        else
        {
            findings.Add(new AccuracyFinding(AccuracyStatus.LotSizeUnknown, size.Note,
                "no target to check against and no adoption cap — machine-count adoption stays refused until the order row is readable"));
        }

        // ---- what may be done automatically ----
        // Only ONE correction is ever automatic: adopting the last machine's count when it read ahead of PVS.
        // It is refused without a lot size (an un-reset cumulative counter cannot be ruled out) and refused
        // when the machine's own figure is already over the cap — the L307 failure, where 825 panels were
        // adopted against a 300-panel target and produced a phantom 3300-board row.
        bool adopt = machine.ShouldAdoptMachineCount;
        string? blocked = null;
        if (adopt && !size.IsKnown)
        {
            adopt = false;
            blocked = "lot size unknown — an un-reset machine counter cannot be ruled out";
        }
        else if (adopt && machineBoards is int mb2 && mb2 > cap)
        {
            adopt = false;
            blocked = $"machine count {mb2} boards is over the lot cap {cap}";
        }

        return Build(input.Scope, pvsBoards, machineBoards, opBoards, size, machine, findings, adopt, blocked);
    }

    /// <summary>
    /// Compares several runs in one go — the same lot on two lines, or both sides of one lot, come back as
    /// SEPARATE results because their scopes differ. Input order is preserved.
    /// </summary>
    public static IReadOnlyList<LotAccuracyResult> CompareAll(
        IEnumerable<LotAccuracyInput> inputs, LotAccuracyOptions? options = null) =>
        (inputs ?? Enumerable.Empty<LotAccuracyInput>()).Select(i => Compare(i, options)).ToList();

    /// <summary>Picks the headline status off the findings and assembles the result.</summary>
    private static LotAccuracyResult Build(
        LotScope scope, int pvsBoards, int? machineBoards, int? opBoards, LotSize size,
        ReconcileResult machine, List<AccuracyFinding> findings, bool adopt, string? blocked)
    {
        var status = Precedence.FirstOrDefault(p => findings.Any(f => f.Kind == p), AccuracyStatus.Agree);
        return new LotAccuracyResult(scope, status, pvsBoards, machineBoards, opBoards,
            size.IsKnown ? size.Boards : null, size.Source, machine, findings, adopt, blocked,
            Summary(scope, pvsBoards, machineBoards, opBoards, size, status, findings));
    }

    /// <summary>The one-line headline a supervisor reads first: all four numbers, then what it means.</summary>
    private static string Summary(
        LotScope scope, int pvsBoards, int? machineBoards, int? opBoards, LotSize size,
        AccuracyStatus status, List<AccuracyFinding> findings)
    {
        string lot = size.IsKnown ? $"{size.Boards} ({size.Source})" : "unknown";
        string mach = machineBoards is int m ? m.ToString() : "no read";
        string op = opBoards is int o ? o.ToString() : "none recorded";
        string head = $"{scope.Key}: lot size {lot}, PVS {pvsBoards}, machine {mach}, operator {op}";
        var first = findings.FirstOrDefault(f => f.Kind == status);
        return first is null ? head + " — all sources agree" : $"{head} — {first.Message}";
    }
}
