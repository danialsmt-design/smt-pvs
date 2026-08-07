using Pvs.Core.People;

namespace Pvs.Core.Verification;

public enum ModelChangeState
{
    AwaitingBadge,
    Scanning,          // awaiting a reel scan for the current feeder
    ConfirmingQty,     // part matched; operator confirms the reel's remaining qty (or flags it wrong)
    PartInterlock,     // scanned part != expected — supervisor release required
    QtyInterlock,      // operator could not confirm qty — supervisor required
    AwaitingCorrectedQty, // supervisor released the qty interlock; awaiting the corrected count
    Complete
}

public enum QtyOutcome { Pending, Confirmed, Corrected }

/// <summary>One feeder row in a model-change check: part verification plus a qty confirmation.</summary>
public sealed class ModelChangeItem
{
    public required int Machine { get; init; }
    public required int Feeder { get; init; }
    public required string ExpectedPart { get; init; }

    // part verification (same as a full scan)
    public FeederCheckStatus PartStatus { get; internal set; } = FeederCheckStatus.Pending;
    public string? ScannedPart { get; internal set; }
    public string? ReelUid { get; internal set; }
    public Badge? ReleasedBy { get; internal set; }

    // qty confirmation
    public int? CurrentQty { get; internal set; }     // reel's remaining qty from inventory (set by the caller)
    public int? ConfirmedQty { get; internal set; }   // the accepted value (== CurrentQty if confirmed, else supervisor's count)
    public QtyOutcome QtyOutcome { get; internal set; } = QtyOutcome.Pending;
    public Badge? QtyCorrectedBy { get; internal set; }
}

/// <summary>
/// The Model-Change check as a state machine. It runs exactly like the shift-change full scan
/// (badge in, then scan each feeder's reel in order; part mismatch interlocks until a supervisor
/// releases it) but adds a per-feeder QUANTITY step: after a feeder's part matches, the reel's
/// current remaining quantity (looked up by UID and supplied by the caller via
/// <see cref="SetScannedQty"/>) is shown. The operator either CONFIRMS it (accept as-is) or flags it
/// wrong; a wrong qty INTERLOCKS and only a supervisor (L2+) may release it and key the corrected
/// count (which the caller writes back to inventory). Complete when every feeder's part is
/// matched/released and its qty confirmed/corrected.
///
/// Pure logic: the caller builds the ordered feeder list, resolves badges, supplies the current qty,
/// and performs the inventory write. This class only sequences the flow.
/// </summary>
public sealed class ModelChangeSession
{
    private readonly List<ModelChangeItem> _items;
    private readonly HashSet<int> _confirmed = new();
    private int _cursor;

    public ModelChangeSession(IEnumerable<ModelChangeItem> feeders)
    {
        _items = feeders?.ToList() ?? throw new ArgumentNullException(nameof(feeders));
        if (_items.Count == 0) throw new ArgumentException("At least one feeder is required.", nameof(feeders));
    }

    public IReadOnlyList<ModelChangeItem> Items => _items;
    public ModelChangeState State { get; private set; } = ModelChangeState.AwaitingBadge;
    public Badge? Operator { get; private set; }
    public ModelChangeItem? Current => _cursor >= 0 && _cursor < _items.Count ? _items[_cursor] : null;

    public int MatchedCount => _items.Count(i => i.PartStatus is FeederCheckStatus.Matched);
    public int ReleasedCount => _items.Count(i => i.PartStatus is FeederCheckStatus.Released);
    public int QtyConfirmedCount => _items.Count(i => i.QtyOutcome is QtyOutcome.Confirmed);
    public int QtyCorrectedCount => _items.Count(i => i.QtyOutcome is QtyOutcome.Corrected);

    /// <summary>Machines the operator has confirmed &amp; saved so far.</summary>
    public IReadOnlyCollection<int> ConfirmedMachines => _confirmed;

    private static bool ItemDone(ModelChangeItem i) =>
        i.PartStatus == FeederCheckStatus.Released ||
        (i.PartStatus == FeederCheckStatus.Matched && i.QtyOutcome is QtyOutcome.Confirmed or QtyOutcome.Corrected);

    /// <summary>True when every feeder on a machine has its part verified and qty confirmed/corrected.</summary>
    public bool MachineComplete(int machine) =>
        _items.Any(i => i.Machine == machine) && _items.Where(i => i.Machine == machine).All(ItemDone);

    /// <summary>Jump the scan to a chosen machine's first un-scanned feeder — lets the operator pick scan order.</summary>
    public ChangeStep GoToMachine(int machine)
    {
        if (State == ModelChangeState.AwaitingBadge) return Step(StepOutcome.Rejected, "Scan your badge first.");
        if (State != ModelChangeState.Scanning) return Step(StepOutcome.Rejected, "Finish the current feeder first.");
        int idx = _items.FindIndex(i => i.Machine == machine && i.PartStatus == FeederCheckStatus.Pending);
        if (idx < 0) return Step(StepOutcome.Rejected, $"Machine {machine} has no feeders left to scan.");
        _cursor = idx;
        return Step(StepOutcome.Ok, $"Scan feeder {_items[idx].Feeder} on machine {machine}.");
    }

    /// <summary>Operator confirms a completed machine so its result is saved before moving on.</summary>
    public ChangeStep ConfirmMachine(int machine)
    {
        if (!_items.Any(i => i.Machine == machine)) return Step(StepOutcome.Rejected, $"Machine {machine} is not in this checklist.");
        if (!MachineComplete(machine)) return Step(StepOutcome.Rejected, $"Machine {machine} still has feeders/qty to complete.");
        _confirmed.Add(machine);
        return Step(StepOutcome.Ok, $"Machine {machine} verified and saved.");
    }

    public ChangeStep ScanBadge(Badge badge)
    {
        switch (State)
        {
            case ModelChangeState.AwaitingBadge:
                if (!badge.CanOperate)
                    return Step(StepOutcome.Rejected, $"{badge.Name}: not a recognised operator.");
                Operator = badge;
                _cursor = NextPending(-1);
                State = ModelChangeState.Scanning;
                return Step(StepOutcome.Ok, $"Operator {badge.Name}. Scan feeder {Current!.Feeder} on machine {Current.Machine}.");

            case ModelChangeState.PartInterlock:
                if (!badge.CanReleaseInterlock)
                    return Step(StepOutcome.Rejected, $"{badge.Name}: a supervisor badge is required to clear the wrong scan.");
                // Supervisor clears the wrong scan and requires a re-scan (does NOT accept the mismatch).
                var pi = _items[_cursor];
                pi.PartStatus = FeederCheckStatus.Pending;
                pi.ScannedPart = null; pi.ReelUid = null; pi.ReleasedBy = badge;
                State = ModelChangeState.Scanning;
                return Step(StepOutcome.Ok, $"Cleared by {badge.Name} — rescan feeder {Current!.Feeder} on machine {Current.Machine}.");

            case ModelChangeState.QtyInterlock:
                if (!badge.CanReleaseInterlock)
                    return Step(StepOutcome.Rejected, $"{badge.Name}: a supervisor badge is required to correct the quantity.");
                _items[_cursor].QtyCorrectedBy = badge;
                State = ModelChangeState.AwaitingCorrectedQty;
                return Step(StepOutcome.Ok, $"Supervisor {badge.Name}. Key the correct quantity for feeder {Current!.Feeder}.");

            default:
                return Step(StepOutcome.Rejected, "No badge is expected at this step.");
        }
    }

    public ChangeStep ScanReel(string partNumber, string uid)
    {
        if (State != ModelChangeState.Scanning)
            return Step(StepOutcome.Rejected, State == ModelChangeState.AwaitingBadge
                ? "Scan your badge first."
                : "No reel scan is expected at this step.");

        var item = _items[_cursor];
        item.ScannedPart = (partNumber ?? string.Empty).Trim();
        item.ReelUid = (uid ?? string.Empty).Trim();

        if (!PartsMatch(item.ScannedPart, item.ExpectedPart))
        {
            item.PartStatus = FeederCheckStatus.Mismatched;
            State = ModelChangeState.PartInterlock;
            return Step(StepOutcome.Interlocked,
                $"Feeder {item.Feeder}: scanned {item.ScannedPart}, expected {item.ExpectedPart}. Supervisor release required.");
        }

        item.PartStatus = FeederCheckStatus.Matched;
        State = ModelChangeState.ConfirmingQty;
        return Step(StepOutcome.Ok, $"Feeder {item.Feeder} part verified. Confirm the remaining quantity.");
    }

    /// <summary>Caller supplies the scanned reel's current remaining qty (from inventory) once it has resolved it.</summary>
    public void SetScannedQty(int? qty)
    {
        if (State == ModelChangeState.ConfirmingQty && Current is { } it) it.CurrentQty = qty;
    }

    /// <summary>Operator accepts the displayed remaining qty as correct.</summary>
    public ChangeStep ConfirmQty()
    {
        if (State != ModelChangeState.ConfirmingQty)
            return Step(StepOutcome.Rejected, "No quantity is awaiting confirmation.");
        var item = _items[_cursor];
        item.ConfirmedQty = item.CurrentQty;
        item.QtyOutcome = QtyOutcome.Confirmed;
        return Advance($"Feeder {item.Feeder} qty confirmed ({item.CurrentQty?.ToString() ?? "n/a"}).");
    }

    /// <summary>Operator flags the displayed qty as wrong — interlocks for a supervisor to correct.</summary>
    public ChangeStep RejectQty()
    {
        if (State != ModelChangeState.ConfirmingQty)
            return Step(StepOutcome.Rejected, "No quantity is awaiting confirmation.");
        State = ModelChangeState.QtyInterlock;
        return Step(StepOutcome.Interlocked,
            $"Feeder {_items[_cursor].Feeder} quantity not confirmed. Supervisor must key the correct quantity.");
    }

    /// <summary>Supervisor keys the corrected count (after releasing the qty interlock). Caller writes it back to inventory.</summary>
    public ChangeStep EnterCorrectedQty(int qty)
    {
        if (State != ModelChangeState.AwaitingCorrectedQty)
            return Step(StepOutcome.Rejected, "Not awaiting a corrected quantity.");
        if (qty < 0)
            return Step(StepOutcome.Rejected, "Quantity cannot be negative.");
        var item = _items[_cursor];
        item.ConfirmedQty = qty;
        item.QtyOutcome = QtyOutcome.Corrected;
        return Advance($"Feeder {item.Feeder} qty corrected to {qty} by {item.QtyCorrectedBy?.Name}.");
    }

    /// <summary>
    /// Reset a single feeder (part + qty) back to Pending and make it the current one to rescan — lets
    /// the operator fix a wrong scan without a supervisor. While a feeder is mid-step (interlock / qty),
    /// only that same feeder may be cleared; otherwise finish/resolve it first.
    /// </summary>
    public ChangeStep ClearFeeder(int machine, int feeder)
    {
        if (State == ModelChangeState.AwaitingBadge) return Step(StepOutcome.Rejected, "Scan your badge first.");
        int idx = _items.FindIndex(i => i.Machine == machine && i.Feeder == feeder);
        if (idx < 0) return Step(StepOutcome.Rejected, $"Feeder {feeder} is not in this checklist.");
        // A wrong PART scan (interlock) can only be cleared by a supervisor badge — not self-cleared.
        if (State == ModelChangeState.PartInterlock)
            return Step(StepOutcome.Rejected, "Wrong scan — a SUPERVISOR badge is required to clear it.");
        bool midStep = State is ModelChangeState.ConfirmingQty or ModelChangeState.PartInterlock
                              or ModelChangeState.QtyInterlock or ModelChangeState.AwaitingCorrectedQty;
        if (midStep && idx != _cursor)
            return Step(StepOutcome.Rejected, "Finish or resolve the current feeder first.");
        if (idx != _cursor && _items[idx].PartStatus == FeederCheckStatus.Pending)
            return Step(StepOutcome.Rejected, "Scan feeders in sequence — you can't skip ahead to an un-scanned feeder.");

        var it = _items[idx];
        it.PartStatus = FeederCheckStatus.Pending;
        it.ScannedPart = null; it.ReelUid = null; it.ReleasedBy = null;
        it.CurrentQty = null; it.ConfirmedQty = null; it.QtyOutcome = QtyOutcome.Pending; it.QtyCorrectedBy = null;
        _cursor = idx;
        State = ModelChangeState.Scanning;
        return Step(StepOutcome.Ok, $"Feeder {feeder} on machine {machine} cleared — scan it again.");
    }

    private ChangeStep Advance(string prefix)
    {
        _cursor = NextPending(_cursor);
        if (_cursor < 0)
        {
            State = ModelChangeState.Complete;
            return Step(StepOutcome.Completed, $"{prefix} Model change complete — all {_items.Count} feeders checked.");
        }
        State = ModelChangeState.Scanning;
        return Step(StepOutcome.Ok, $"{prefix} Scan feeder {Current!.Feeder} on machine {Current.Machine}.");
    }

    private int NextPending(int after)
    {
        for (int i = after + 1; i < _items.Count; i++)
            if (_items[i].PartStatus == FeederCheckStatus.Pending) return i;
        return -1;
    }

    // Substitute-aware match: a slash in a BOM/feeder part number lists approved substitutes (see PartNumber).
    private static bool PartsMatch(string a, string b) => PartNumber.Matches(a, b);

    private static ChangeStep Step(StepOutcome outcome, string message) => new(outcome, message);

    /// <summary>
    /// Re-pull the feeder list into an IN-PROGRESS check (e.g. after a mid-check ProductBOM amendment). Feeders
    /// whose expected part is UNCHANGED keep their scanned status; a feeder whose part CHANGED (or is newly added)
    /// is reset to Pending — its old scan verified the old part. Removed feeders drop out. The scan then resumes
    /// AT the amended feeder so the operator just re-scans that one and carries on. Operator/badge is preserved.
    /// </summary>
    public ChangeStep SyncFeeders(IEnumerable<(int Machine, int Feeder, string ExpectedPart)> feeders)
    {
        var incoming = feeders?.ToList() ?? new List<(int, int, string)>();
        if (incoming.Count == 0) return Step(StepOutcome.Rejected, "No feeders to sync.");
        var keys = new HashSet<(int, int)>(incoming.Select(f => (f.Machine, f.Feeder)));
        _items.RemoveAll(i => !keys.Contains((i.Machine, i.Feeder)));
        var amended = new List<(int, int)>();   // feeders whose part changed or that were newly added
        foreach (var f in incoming)
        {
            int idx = _items.FindIndex(i => i.Machine == f.Machine && i.Feeder == f.Feeder);
            if (idx < 0)
            {
                _items.Add(new ModelChangeItem { Machine = f.Machine, Feeder = f.Feeder, ExpectedPart = f.ExpectedPart });
                amended.Add((f.Machine, f.Feeder));
            }
            else if (!PartsMatch(_items[idx].ExpectedPart, f.ExpectedPart))
            {
                // part changed -> a fresh Pending item (the old scan verified the OLD part, so it must be redone)
                _items[idx] = new ModelChangeItem { Machine = f.Machine, Feeder = f.Feeder, ExpectedPart = f.ExpectedPart };
                amended.Add((f.Machine, f.Feeder));
            }
            // unchanged -> keep the existing item (preserve scanned status)
        }
        _items.Sort((a, b) => a.Machine != b.Machine ? a.Machine.CompareTo(b.Machine) : a.Feeder.CompareTo(b.Feeder));
        _confirmed.RemoveWhere(m => !_items.Any(i => i.Machine == m));
        if (State != ModelChangeState.AwaitingBadge)
        {
            // resume AT the first amended feeder; else the first still-pending; else the check is complete
            int cur = -1;
            if (amended.Count > 0)
            {
                var first = amended.OrderBy(k => k.Item1).ThenBy(k => k.Item2).First();
                cur = _items.FindIndex(i => i.Machine == first.Item1 && i.Feeder == first.Item2);
            }
            if (cur < 0) cur = NextPending(-1);
            _cursor = cur;
            State = _cursor < 0 ? ModelChangeState.Complete : ModelChangeState.Scanning;
        }
        return amended.Count == 0
            ? Step(StepOutcome.Ok, $"Checklist refreshed — no feeder changed ({_items.Count} feeders).")
            : Step(StepOutcome.Ok, $"Checklist refreshed — {amended.Count} feeder(s) amended; rescan feeder {Current?.Feeder} on machine {Current?.Machine}.");
    }

    // ---- persistence: capture / rebuild the whole check so it survives a restart or accidental cancel ----

    /// <summary>Capture the full check state (items, statuses, qty outcomes, cursor, confirmed machines, operator).</summary>
    public ModelChangeSnapshot Export() => new(
        State, BadgeSnapshot.From(Operator), _cursor, _confirmed.ToList(),
        _items.Select(i => new ModelChangeItemSnapshot(
            i.Machine, i.Feeder, i.ExpectedPart,
            i.PartStatus, i.ScannedPart, i.ReelUid, BadgeSnapshot.From(i.ReleasedBy),
            i.CurrentQty, i.ConfirmedQty, i.QtyOutcome, BadgeSnapshot.From(i.QtyCorrectedBy))).ToList());

    /// <summary>Rebuild a session from a snapshot so the operator continues from exactly where they stopped.</summary>
    public static ModelChangeSession Restore(ModelChangeSnapshot snap)
    {
        var items = snap.Items
            .Select(s => new ModelChangeItem { Machine = s.Machine, Feeder = s.Feeder, ExpectedPart = s.ExpectedPart })
            .ToList();
        var session = new ModelChangeSession(items);
        for (int k = 0; k < items.Count; k++)
        {
            var s = snap.Items[k]; var it = items[k];
            it.PartStatus = s.PartStatus; it.ScannedPart = s.ScannedPart; it.ReelUid = s.ReelUid;
            it.ReleasedBy = s.ReleasedBy?.ToBadge();
            it.CurrentQty = s.CurrentQty; it.ConfirmedQty = s.ConfirmedQty;
            it.QtyOutcome = s.QtyOutcome; it.QtyCorrectedBy = s.QtyCorrectedBy?.ToBadge();
        }
        session.State = snap.State;
        session.Operator = snap.Operator?.ToBadge();
        session._cursor = snap.Cursor;
        foreach (var m in snap.Confirmed) session._confirmed.Add(m);
        return session;
    }
}
