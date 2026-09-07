namespace Pvs.Core.Runtime;

/// <summary>
/// One line downtime span. It is AUTO-opened from the machine's R1 stream with a machine-side reason; the operator
/// may later add a comment (their classification + a free note) which annotates the span without changing its timing.
/// <see cref="End"/> is null while still down.
/// </summary>
public sealed record ReasonStop(
    DateTime Start,
    DateTime? End,
    string MachineReason,                 // auto, from R1: estop / error / starved / stopped / offline
    string? OperatorReason = null,        // operator comment classification (see ReasonStopLog.OperatorReasons)
    string? Note = null,                  // free-text operator note
    IReadOnlyList<int>? Machines = null)  // which machine(s) were in the stop state
{
    public TimeSpan Duration(DateTime now) => (End ?? now) - Start;
    /// <summary>The reason to report: operator classification if given, else the machine reason. Never null — an
    /// old-format span restored from a pre-rework stops.json can have a null MachineReason, so fall back to "stopped".</summary>
    public string Reason
    {
        get
        {
            var r = string.IsNullOrWhiteSpace(OperatorReason) ? MachineReason : OperatorReason;
            return string.IsNullOrWhiteSpace(r) ? "stopped" : r!;
        }
    }
}

/// <summary>
/// Records line downtime. Fed by the machine's R1 operating-state stream (SI-F manual §8.2): a stop is AUTO-opened
/// when the line was producing and then stops, tagged with the machine's own reason; it closes when the line runs
/// again. The operator does not start the clock — they add a COMMENT (their reason + optional note) that annotates
/// the auto-captured span (Danial 2026-08-22, superseding the earlier operator-tap-only rule).
///
/// Pure and in-memory; the host owns the clock, the condition feed, and persistence.
/// </summary>
public sealed class ReasonStopLog
{
    /// <summary>Machine-side reasons derived from the R1 stream (auto).</summary>
    public static readonly IReadOnlyList<string> MachineReasons =
        new[] { "estop", "error", "starved", "stopped", "offline" };

    /// <summary>Operator comment classifications (UI supplies the bilingual labels; the log stores the key).</summary>
    public static readonly IReadOnlyList<string> OperatorReasons =
        new[] { "machine", "waiting_part", "no_air", "rest", "scheduled" };

    public static bool IsMachineReason(string? r) => r is not null && MachineReasons.Contains(r, StringComparer.OrdinalIgnoreCase);
    public static bool IsOperatorReason(string? r) => r is not null && OperatorReasons.Contains(r, StringComparer.OrdinalIgnoreCase);

    private readonly List<ReasonStop> _stops = new();
    private ReasonStop? _open;

    public IReadOnlyList<ReasonStop> Stops => _stops;
    public ReasonStop? Open => _open;

    /// <summary>Auto-open a downtime span from the machine signal (or re-tag the machine reason / machines of the
    /// one already open). No-op inputs are ignored.</summary>
    public void AutoStart(string machineReason, IReadOnlyList<int>? machines, DateTime at)
    {
        if (!IsMachineReason(machineReason)) machineReason = "stopped";
        if (_open is null)
            _open = new ReasonStop(at, null, machineReason, Machines: machines);
        else
            // keep the operator's comment + original start; refresh the machine reason/machines as the wire evolves
            _open = _open with { MachineReason = machineReason, Machines = machines ?? _open.Machines };
    }

    /// <summary>Operator adds a comment to the current (or most-recent) span: their classification and/or a note.
    /// Returns false if there is no span to annotate.</summary>
    public bool AddComment(string? operatorReason, string? note, DateTime at)
    {
        var target = _open;
        if (target is null)
        {
            if (_stops.Count == 0) return false;
            // annotate the most recent closed span
            var idx = _stops.Count - 1;
            _stops[idx] = ApplyComment(_stops[idx], operatorReason, note);
            return true;
        }
        _open = ApplyComment(target, operatorReason, note);
        return true;
    }

    private static ReasonStop ApplyComment(ReasonStop s, string? operatorReason, string? note)
    {
        string? or = IsOperatorReason(operatorReason) ? operatorReason!.ToLowerInvariant() : s.OperatorReason;
        string? nt = string.IsNullOrWhiteSpace(note) ? s.Note : note!.Trim();
        return s with { OperatorReason = or, Note = nt };
    }

    /// <summary>The line produced a board / a machine started mounting — close any open span.</summary>
    public void OnProduced(DateTime at) { if (_open is not null) Close(at); }

    /// <summary>Force-close any open span at a shift boundary (the line never ran again this shift).</summary>
    public void CloseAtShiftEnd(DateTime at) { if (_open is not null) Close(at); }

    private void Close(DateTime at)
    {
        var end = at < _open!.Start ? _open.Start : at;   // never a negative span
        _stops.Add(_open with { End = end });
        _open = null;
    }

    /// <summary>Reset to clean state.</summary>
    public void Clear() { _stops.Clear(); _open = null; }

    /// <summary>Drop CLOSED stops that ended before <paramref name="before"/> (rolling retention — the log is no
    /// longer wiped at midnight, which lost a night shift's stops mid-shift). The open span is never pruned.</summary>
    public int Prune(DateTime before) => _stops.RemoveAll(s => s.End is DateTime e && e < before);

    /// <summary>Restore persisted state (host reload).</summary>
    public void Restore(IEnumerable<ReasonStop> stops, ReasonStop? open)
    {
        _stops.Clear();
        _stops.AddRange(stops);
        _open = open;
    }

    /// <summary>Total time and stop-count per EFFECTIVE reason as of <paramref name="now"/> (an open span counts to now).</summary>
    public IReadOnlyList<(string Reason, TimeSpan Total, int Count)> ByReason(DateTime now)
    {
        var all = _open is null ? _stops : _stops.Append(_open);
        return all.GroupBy(s => s.Reason)
            .Select(g => (g.Key, g.Aggregate(TimeSpan.Zero, (a, s) => a + s.Duration(now)), g.Count()))
            .OrderByDescending(x => x.Item2)
            .ToList();
    }
}
