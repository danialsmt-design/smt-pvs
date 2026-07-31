using Pvs.Core.People;

namespace Pvs.Core.Verification;

/// <summary>Which full-coverage check this is. Same flow; different trigger/label.</summary>
public enum ScanPurpose { ShiftChange, LotEnd }

public enum FeederCheckStatus
{
    /// <summary>Not scanned yet (blue on the monitor).</summary>
    Pending,
    /// <summary>Scanned and matched (green).</summary>
    Matched,
    /// <summary>Scanned and did NOT match — interlocked (red) until a supervisor releases it.</summary>
    Mismatched,
    /// <summary>A supervisor released a mismatch; recorded as an override.</summary>
    Released
}

/// <summary>One feeder row in a full-scan checklist.</summary>
public sealed class FeederCheck
{
    public required int Machine { get; init; }
    public required int Feeder { get; init; }
    public required string ExpectedPart { get; init; }

    public FeederCheckStatus Status { get; internal set; } = FeederCheckStatus.Pending;
    public string? ScannedPart { get; internal set; }
    public string? ReelUid { get; internal set; }
    public Badge? ReleasedBy { get; internal set; }
}

public enum FullScanState { AwaitingBadge, Scanning, Interlocked, Complete }

/// <summary>
/// The shift-change (Mode A) / lot-end (Mode C) full-scan checklist as a state machine. The operator
/// badges in, then scans each feeder's reel in the given order; each row goes blue -> green (match)
/// or red (mismatch). A mismatch interlocks exactly like Mode B — only a supervisor (L2+) releases
/// it — and the session is Complete only when every feeder is matched or released.
///
/// Pure logic: the caller builds the ordered feeder list (machine-by-machine, first-to-last) from
/// the model's feeder map and resolves scanned badges.
/// </summary>
public sealed class FullScanSession
{
    private readonly List<FeederCheck> _items;
    private readonly HashSet<int> _confirmed = new();
    private int _cursor;

    public FullScanSession(ScanPurpose purpose, IEnumerable<FeederCheck> feeders)
    {
        Purpose = purpose;
        _items = feeders?.ToList() ?? throw new ArgumentNullException(nameof(feeders));
        if (_items.Count == 0) throw new ArgumentException("At least one feeder is required.", nameof(feeders));
    }

    public ScanPurpose Purpose { get; }
    public IReadOnlyList<FeederCheck> Items => _items;
    public FullScanState State { get; private set; } = FullScanState.AwaitingBadge;
    public Badge? Operator { get; private set; }

    /// <summary>The feeder currently to be scanned (or being resolved), or null when complete.</summary>
    public FeederCheck? Current => _cursor >= 0 && _cursor < _items.Count ? _items[_cursor] : null;

    public int MatchedCount => _items.Count(i => i.Status is FeederCheckStatus.Matched);
    public int ReleasedCount => _items.Count(i => i.Status is FeederCheckStatus.Released);
    public int PendingCount => _items.Count(i => i.Status is FeederCheckStatus.Pending);

    /// <summary>Machines the operator has confirmed &amp; saved so far.</summary>
    public IReadOnlyCollection<int> ConfirmedMachines => _confirmed;

    /// <summary>True when every feeder on a machine is matched or released.</summary>
    public bool MachineComplete(int machine) =>
        _items.Any(i => i.Machine == machine) &&
        _items.Where(i => i.Machine == machine).All(i => i.Status is FeederCheckStatus.Matched or FeederCheckStatus.Released);

    /// <summary>Jump the scan to a chosen machine's first un-scanned feeder — lets the operator pick scan order.</summary>
    public ChangeStep GoToMachine(int machine)
    {
        if (State == FullScanState.AwaitingBadge) return Step(StepOutcome.Rejected, "Scan your badge first.");
        if (State != FullScanState.Scanning) return Step(StepOutcome.Rejected, "Finish the current feeder first.");
        int idx = _items.FindIndex(i => i.Machine == machine && i.Status == FeederCheckStatus.Pending);
        if (idx < 0) return Step(StepOutcome.Rejected, $"Machine {machine} has no feeders left to scan.");
        _cursor = idx;
        return Step(StepOutcome.Ok, $"Scan feeder {_items[idx].Feeder} on machine {machine}.");
    }

    /// <summary>Operator confirms a completed machine so its result is saved before moving on.</summary>
    public ChangeStep ConfirmMachine(int machine)
    {
        if (!_items.Any(i => i.Machine == machine)) return Step(StepOutcome.Rejected, $"Machine {machine} is not in this checklist.");
        if (!MachineComplete(machine)) return Step(StepOutcome.Rejected, $"Machine {machine} still has feeders to scan.");
        _confirmed.Add(machine);
        return Step(StepOutcome.Ok, $"Machine {machine} verified and saved.");
    }

    public ChangeStep ScanBadge(Badge badge)
    {
        switch (State)
        {
            case FullScanState.AwaitingBadge:
                if (!badge.CanOperate)
                    return Step(StepOutcome.Rejected, $"{badge.Name}: not a recognised operator.");
                Operator = badge;
                _cursor = NextPending(-1);
                State = FullScanState.Scanning;
                return Step(StepOutcome.Ok, $"Operator {badge.Name}. Scan feeder {Current!.Feeder} on machine {Current.Machine}.");

            case FullScanState.Interlocked:
                if (!badge.CanReleaseInterlock)
                    return Step(StepOutcome.Rejected, $"{badge.Name}: a supervisor badge is required to clear the wrong scan.");
                // Supervisor clears the wrong scan and requires a re-scan (does NOT accept the mismatch).
                var item = _items[_cursor];
                item.Status = FeederCheckStatus.Pending;
                item.ScannedPart = null; item.ReelUid = null; item.ReleasedBy = badge;
                State = FullScanState.Scanning;
                return Step(StepOutcome.Ok, $"Cleared by {badge.Name} — rescan feeder {item.Feeder} on machine {item.Machine}.");

            default:
                return Step(StepOutcome.Rejected, "No badge is expected at this step.");
        }
    }

    public ChangeStep ScanReel(string partNumber, string uid)
    {
        if (State != FullScanState.Scanning)
            return Step(StepOutcome.Rejected, State == FullScanState.AwaitingBadge
                ? "Scan your badge first."
                : "No reel scan is expected at this step.");

        var item = _items[_cursor];
        item.ScannedPart = (partNumber ?? string.Empty).Trim();
        item.ReelUid = (uid ?? string.Empty).Trim();

        if (!PartsMatch(item.ScannedPart, item.ExpectedPart))
        {
            item.Status = FeederCheckStatus.Mismatched;
            State = FullScanState.Interlocked;
            return Step(StepOutcome.Interlocked,
                $"Feeder {item.Feeder}: scanned {item.ScannedPart}, expected {item.ExpectedPart}. Supervisor release required.");
        }

        item.Status = FeederCheckStatus.Matched;
        return Advance($"Feeder {item.Feeder} verified.");
    }

    /// <summary>
    /// Reset a single feeder back to Pending and make it the current one to rescan — lets the operator
    /// fix a wrong scan (including a mismatch) without a supervisor. Only the currently-interlocked
    /// feeder may be cleared while interlocked; otherwise the interlock must be resolved first.
    /// </summary>
    public ChangeStep ClearFeeder(int machine, int feeder)
    {
        if (State == FullScanState.AwaitingBadge) return Step(StepOutcome.Rejected, "Scan your badge first.");
        int idx = _items.FindIndex(i => i.Machine == machine && i.Feeder == feeder);
        if (idx < 0) return Step(StepOutcome.Rejected, $"Feeder {feeder} is not in this checklist.");
        // A wrong scan (interlock) can only be cleared by a supervisor badge — not self-cleared by the operator.
        if (State == FullScanState.Interlocked)
            return Step(StepOutcome.Rejected, "Wrong scan — a SUPERVISOR badge is required to clear it.");
        if (idx != _cursor && _items[idx].Status == FeederCheckStatus.Pending)
            return Step(StepOutcome.Rejected, "Scan feeders in sequence — you can't skip ahead to an un-scanned feeder.");

        var it = _items[idx];
        it.Status = FeederCheckStatus.Pending;
        it.ScannedPart = null; it.ReelUid = null; it.ReleasedBy = null;
        _cursor = idx;
        State = FullScanState.Scanning;
        return Step(StepOutcome.Ok, $"Feeder {feeder} on machine {machine} cleared — scan it again.");
    }

    private ChangeStep Advance(string prefix)
    {
        _cursor = NextPending(_cursor);
        if (_cursor < 0)
        {
            State = FullScanState.Complete;
            return Step(StepOutcome.Completed, $"{prefix} All {_items.Count} feeders checked.");
        }
        State = FullScanState.Scanning;
        return Step(StepOutcome.Ok, $"{prefix} Scan feeder {Current!.Feeder} on machine {Current.Machine}.");
    }

    private int NextPending(int after)
    {
        for (int i = after + 1; i < _items.Count; i++)
            if (_items[i].Status == FeederCheckStatus.Pending) return i;
        return -1;
    }

    private static bool PartsMatch(string a, string b) =>
        string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    private ChangeStep Step(StepOutcome outcome, string message) => new(outcome, message);

    /// <summary>
    /// Re-pull the feeder list into an IN-PROGRESS scan (e.g. after a mid-check ProductBOM amendment). Unchanged
    /// feeders keep their scanned status; a feeder whose expected part CHANGED (or is newly added) is reset to
    /// Pending; removed feeders drop out. The scan resumes AT the amended feeder. Operator/badge is preserved.
    /// </summary>
    public ChangeStep SyncFeeders(IEnumerable<(int Machine, int Feeder, string ExpectedPart)> feeders)
    {
        var incoming = feeders?.ToList() ?? new List<(int, int, string)>();
        if (incoming.Count == 0) return Step(StepOutcome.Rejected, "No feeders to sync.");
        var keys = new HashSet<(int, int)>(incoming.Select(f => (f.Machine, f.Feeder)));
        _items.RemoveAll(i => !keys.Contains((i.Machine, i.Feeder)));
        var amended = new List<(int, int)>();
        foreach (var f in incoming)
        {
            int idx = _items.FindIndex(i => i.Machine == f.Machine && i.Feeder == f.Feeder);
            if (idx < 0)
            {
                _items.Add(new FeederCheck { Machine = f.Machine, Feeder = f.Feeder, ExpectedPart = f.ExpectedPart });
                amended.Add((f.Machine, f.Feeder));
            }
            else if (!PartsMatch(_items[idx].ExpectedPart, f.ExpectedPart))
            {
                _items[idx] = new FeederCheck { Machine = f.Machine, Feeder = f.Feeder, ExpectedPart = f.ExpectedPart };
                amended.Add((f.Machine, f.Feeder));
            }
        }
        _items.Sort((a, b) => a.Machine != b.Machine ? a.Machine.CompareTo(b.Machine) : a.Feeder.CompareTo(b.Feeder));
        _confirmed.RemoveWhere(m => !_items.Any(i => i.Machine == m));
        if (State != FullScanState.AwaitingBadge)
        {
            int cur = -1;
            if (amended.Count > 0)
            {
                var first = amended.OrderBy(k => k.Item1).ThenBy(k => k.Item2).First();
                cur = _items.FindIndex(i => i.Machine == first.Item1 && i.Feeder == first.Item2);
            }
            if (cur < 0) cur = NextPending(-1);
            _cursor = cur;
            State = _cursor < 0 ? FullScanState.Complete : FullScanState.Scanning;
        }
        return amended.Count == 0
            ? Step(StepOutcome.Ok, $"Checklist refreshed — no feeder changed ({_items.Count} feeders).")
            : Step(StepOutcome.Ok, $"Checklist refreshed — {amended.Count} feeder(s) amended; rescan feeder {Current?.Feeder} on machine {Current?.Machine}.");
    }

    // ---- persistence: capture / rebuild the whole check so it survives a restart or accidental cancel ----

    /// <summary>Capture the full check state (items, statuses, cursor, confirmed machines, operator).</summary>
    public FullScanSnapshot Export() => new(
        Purpose, State, BadgeSnapshot.From(Operator), _cursor, _confirmed.ToList(),
        _items.Select(i => new FeederCheckSnapshot(
            i.Machine, i.Feeder, i.ExpectedPart,
            i.Status, i.ScannedPart, i.ReelUid, BadgeSnapshot.From(i.ReleasedBy))).ToList());

    /// <summary>Rebuild a session from a snapshot so the operator continues from exactly where they stopped.</summary>
    public static FullScanSession Restore(FullScanSnapshot snap)
    {
        var items = snap.Items
            .Select(s => new FeederCheck { Machine = s.Machine, Feeder = s.Feeder, ExpectedPart = s.ExpectedPart })
            .ToList();
        var session = new FullScanSession(snap.Purpose, items);
        for (int k = 0; k < items.Count; k++)
        {
            var s = snap.Items[k]; var it = items[k];
            it.Status = s.Status; it.ScannedPart = s.ScannedPart; it.ReelUid = s.ReelUid;
            it.ReleasedBy = s.ReleasedBy?.ToBadge();
        }
        session.State = snap.State;
        session.Operator = snap.Operator?.ToBadge();
        session._cursor = snap.Cursor;
        foreach (var m in snap.Confirmed) session._confirmed.Add(m);
        return session;
    }
}
