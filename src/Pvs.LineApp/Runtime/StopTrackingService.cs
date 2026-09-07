using System.Text.Json;
using Pvs.Core.Events;
using Pvs.Core.Runtime;
using Pvs.Core.Shifts;

namespace Pvs.LineApp.Runtime;

/// <summary>
/// Downtime capture, fed by the machines' own Sony R1 operating-state stream (SI-F manual §8.2). Two halves:
///   1. AUTOMATIC line downtime — the line's stop is AUTO-recorded the moment it was producing and then stops,
///      tagged with the machine's own reason (estop / error / starved / stopped / offline). It closes when a
///      machine starts mounting again. The operator does not start the clock; they add a COMMENT (their reason
///      classification + optional note) that annotates the auto-captured span (Danial 2026-08-22).
///   2. AUTOMATIC per-cell parts-exhaust recovery — every parts-out is timed to that cell producing again.
/// Persists to a JSON file (survives a restart) and keeps a ROLLING window of stops (48 h) — it is never wiped at
/// midnight (that lost a night shift's stops mid-shift; audit M3, 2026-09-07). "The line has run" is tracked per
/// SHIFT: an open span is closed at the shift boundary and the activity flags reset there. Reports/spans are
/// clipped to a caller-supplied window (a Daiya shift, a report day). READ-ONLY toward the machines.
/// </summary>
public sealed class StopTrackingService : IDisposable
{
    private sealed record Persisted(
        string Date,
        List<ReasonStop> Stops, ReasonStop? OpenStop,
        List<CellRecovery> Recoveries, List<CellRecovery> OpenRecoveries,
        DateTime? LastBoardAt, bool SawBoard, bool SawActive, string? ShiftKey);

    private readonly List<Pvs.Core.Runtime.MachineChannel> _channels;
    private readonly Func<int, int, string?> _partResolver;
    private readonly ShiftSchedule _shifts;
    private readonly BreakWindows _breaks;
    private readonly int _stopSeconds;
    private readonly string _path;

    /// <summary>How long closed stops/recoveries are kept (covers any shift's Daiya sheet + the daily report).</summary>
    private static readonly TimeSpan Retention = TimeSpan.FromHours(48);

    private readonly object _gate = new();
    private readonly ReasonStopLog _reasons = new();
    private readonly CellRecoveryLog _cells = new();
    private string _date;
    private string? _shiftKey;
    private DateTime? _lastBoardAt;
    private bool _sawBoard;
    private bool _sawActive;   // the line has been mounting (or produced a board) at some point today
    private Timer? _timer;

    public StopTrackingService(IEnumerable<Pvs.Core.Runtime.MachineChannel> channels,
        Func<int, int, string?> partResolver, ShiftSchedule shifts, BreakWindows breaks, int stopSeconds, string path)
    {
        _channels = channels.ToList();
        _partResolver = partResolver;
        _shifts = shifts;
        _breaks = breaks;
        _stopSeconds = stopSeconds > 0 ? stopSeconds : 90;
        _path = path;
        _date = DateTime.Now.ToString("yyyy-MM-dd");
        _shiftKey = SafeShiftKey(DateTime.Now);
        Load();
    }

    public void Start()
    {
        foreach (var ch in _channels)
        {
            int machine = ch.Machine;
            ch.PartsOutDetected += e => OnPartsOut(e.Machine, e.Feeder, e.At);
            ch.BoardCompleted += at => OnBoard(machine, at);
        }
        // Evaluate the R1 condition stream every 4s: auto-open/close the line downtime span. (Faster than the old
        // 10s so a stop is caught promptly; still cheap — it only reads latched in-memory state.)
        _timer = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(4));
    }

    private void OnPartsOut(int machine, int feeder, DateTime at)
    {
        string? part = null;
        try { part = _partResolver(machine, feeder); } catch { /* best-effort tag */ }
        lock (_gate) { PruneIfNeeded(at); _cells.OnPartsOut(machine, feeder, part, at); Save(); }
    }

    private void OnBoard(int machine, DateTime at)
    {
        lock (_gate)
        {
            PruneIfNeeded(at);
            _cells.OnCellProduced(machine, at);   // per-cell recovery closes for THIS cell
            _reasons.OnProduced(at);              // a board = line producing => close the auto stop
            _lastBoardAt = at;
            _sawBoard = true;
            _sawActive = true;
            Save();
        }
    }

    /// <summary>Operator adds a COMMENT to the current (or most-recent) auto-captured stop: their reason
    /// classification and/or a free note. Returns false if the reason is unknown or there is no span to annotate.</summary>
    public bool AddComment(string? reason, string? note)
    {
        if (reason is not null && !ReasonStopLog.IsOperatorReason(reason) && string.IsNullOrWhiteSpace(note)) return false;
        var now = DateTime.Now;
        lock (_gate)
        {
            PruneIfNeeded(now);
            bool ok = _reasons.AddComment(reason, note, now);
            if (ok) Save();
            return ok;
        }
    }

    private void Tick()
    {
        try
        {
            var now = DateTime.Now;
            lock (_gate)
            {
                PruneIfNeeded(now);
                EvaluateConditions(now);
                // Shift rollover: an open span the line never recovered from ends at the boundary, and "has the line
                // run" starts over for the new shift (it used to reset at calendar midnight — mid night-shift).
                var key = SafeShiftKey(now);
                if (key != _shiftKey) { _reasons.CloseAtShiftEnd(now); _shiftKey = key; _sawActive = false; _sawBoard = false; }
                Save();
            }
        }
        catch { /* sampling is best-effort */ }
    }

    /// <summary>The heart of the R1 feed: read each machine's latched condition and auto-open/close the line's
    /// downtime span. Downtime is only recorded once the line has actually run today — a line that never started
    /// (all Unknown/idle) is NOT downtime.</summary>
    private void EvaluateConditions(DateTime now)
    {
        if (_channels.Count == 0) return;
        var conds = _channels.Select(c => (c.Machine, Cond: c.Condition.Condition)).ToList();

        if (conds.Any(x => x.Cond == MachineCondition.Mounting))
        {
            _sawActive = true;
            _reasons.OnProduced(now);   // a machine is mounting => line up => close any open span (recovery)
            return;
        }
        if (!_sawActive) return;        // never ran today => not downtime, stay quiet

        // Line is not mounting and it ran earlier today => it's down. Pick the machine-side reason (priority order).
        string? reason = null;
        Func<MachineCondition, List<int>> pick = c => conds.Where(x => x.Cond == c).Select(x => x.Machine).ToList();
        List<int> machines;
        if (conds.Any(x => x.Cond == MachineCondition.EmergencyStop)) { reason = "estop"; machines = pick(MachineCondition.EmergencyStop); }
        else if (conds.Any(x => x.Cond == MachineCondition.Recovering)) { reason = "error"; machines = pick(MachineCondition.Recovering); }
        else if (conds.Any(x => x.Cond == MachineCondition.Starved)) { reason = "starved"; machines = pick(MachineCondition.Starved); }
        else if (conds.Any(x => x.Cond == MachineCondition.Stopped)) { reason = "stopped"; machines = pick(MachineCondition.Stopped); }
        else if (conds.All(x => x.Cond == MachineCondition.Offline)) { reason = "offline"; machines = conds.Select(x => x.Machine).ToList(); }
        else return;   // all Unknown / mixed-unknown after a restart => can't tell => don't invent downtime

        _reasons.AutoStart(reason, machines, now);
    }

    /// <summary>Rolling retention instead of a midnight wipe: closed stops/recoveries older than the retention
    /// window are dropped; nothing else changes at a day boundary.</summary>
    private void PruneIfNeeded(DateTime now)
    {
        string today = now.ToString("yyyy-MM-dd");
        if (today == _date) return;
        _date = today;
        _reasons.Prune(now - Retention);
        _cells.Prune(now - Retention);
    }

    /// <summary>The wall-clock window of the CURRENT shift instance (used when a caller asks without a window).</summary>
    private (DateTime From, DateTime To) CurrentShiftWindow(DateTime now)
    {
        try
        {
            var shift = _shifts.ShiftAt(now);
            var from = _shifts.ShiftStart(now);
            return (from, _shifts.EndAfter(from, shift));
        }
        catch { return (now.Date, now.Date.AddDays(1)); }
    }

    /// <summary>A stop clipped to [from, to): null when it doesn't overlap. An open span keeps End = null only while
    /// the window is still running (to &gt; now); otherwise it is clipped to the window end.</summary>
    private static ReasonStop? Clip(ReasonStop s, DateTime from, DateTime to, DateTime now)
    {
        var start = s.Start < from ? from : s.Start;
        var rawEnd = s.End ?? now;
        var end = rawEnd > to ? to : rawEnd;
        if (end <= start) return null;
        DateTime? endOut = (s.End is null && to > now && rawEnd == end) ? null : end;
        return s with { Start = start, End = endOut };
    }

    private List<ReasonStop> ClippedStops(DateTime from, DateTime to, DateTime now) =>
        _reasons.Stops.Concat(_reasons.Open is { } o ? new[] { o } : Array.Empty<ReasonStop>())
            .Select(s => Clip(s, from, to, now))
            .Where(s => s is not null).Select(s => s!)
            .OrderBy(s => s.Start)
            .ToList();

    private string? SafeShiftKey(DateTime now)
    {
        try { return _shifts.ShiftKey(now); } catch { return null; }
    }

    /// <summary>Live state for the operator screen: the open (auto) stop with its machine reason, any operator
    /// comment, elapsed seconds, and whether the line is currently down.</summary>
    public object State()
    {
        var now = DateTime.Now;
        lock (_gate)
        {
            var open = _reasons.Open;
            bool inBreak = _breaks.IsBreak(now);   // HINT only — a covered line still runs through a break
            return new
            {
                down = open is not null,
                machineReason = open?.MachineReason,
                openReason = open?.Reason,               // effective (operator comment ?? machine reason)
                operatorReason = open?.OperatorReason,
                note = open?.Note,
                machines = open?.Machines,
                openSince = open?.Start,
                openSeconds = open is null ? 0 : (int)(now - open.Start).TotalSeconds,
                inBreak,
                suggestedReason = inBreak ? "rest" : null,
                lastBoardAt = _lastBoardAt,
                reasons = ReasonStopLog.OperatorReasons,
                openCells = _cells.Open
                    .OrderBy(r => r.Cell)
                    .Select(r => new { cell = r.Cell, feeder = r.Feeder, part = r.Part, seconds = (int)(now - r.Start).TotalSeconds })
                    .ToList()
            };
        }
    }

    /// <summary>Typed inputs for the line-health rollup: is the line producing, is it down, was it active today,
    /// and any open reason (effective).</summary>
    public (bool Producing, bool Stopped, bool SawBoard, string? OpenReason) HealthInputs()
    {
        var now = DateTime.Now;
        lock (_gate)
        {
            bool mounting = _channels.Any(c => c.Condition.Condition == MachineCondition.Mounting);
            bool recentBoard = _lastBoardAt is DateTime lb && (now - lb).TotalSeconds <= _stopSeconds;
            bool producing = mounting || recentBoard;
            return (producing, _sawActive && !producing, _sawBoard, _reasons.Open?.Reason);
        }
    }

    /// <summary>Report rollup for the daily report: reason tallies (effective), each span with machine + operator
    /// reason + note, and per-cell exhaust recovery.</summary>
    public object Report() { var now = DateTime.Now; var (f, t) = CurrentShiftWindow(now); return Report(f, t); }

    /// <summary>Report rollup for ONE window [from, to): stops clipped to it, reason tallies from the clipped spans,
    /// and the per-cell exhaust recoveries that started inside it.</summary>
    public object Report(DateTime from, DateTime to)
    {
        var now = DateTime.Now;
        lock (_gate)
        {
            var stops = ClippedStops(from, to, now);
            var recs = _cells.Completed.Concat(_cells.Open).Where(r => r.Start >= from && r.Start < to).OrderBy(r => r.Start).ToList();
            return new
            {
                from, to,
                byReason = stops.GroupBy(s => s.Reason)
                    .Select(g => new { reason = g.Key, minutes = Math.Round(g.Sum(s => s.Duration(now).TotalMinutes), 1), count = g.Count() })
                    .OrderByDescending(x => x.minutes)
                    .ToList(),
                stops = stops
                    .Select(s => new { reason = s.Reason, machineReason = s.MachineReason, operatorReason = s.OperatorReason,
                                       note = s.Note, machines = s.Machines, start = s.Start, end = s.End,
                                       minutes = Math.Round(s.Duration(now).TotalMinutes, 1) })
                    .ToList(),
                byCell = recs.Where(r => r.End is not null).GroupBy(r => r.Cell).OrderBy(g => g.Key)
                    .Select(g =>
                    {
                        var total = g.Aggregate(TimeSpan.Zero, (a, r) => a + r.Duration(now));
                        var max = g.Max(r => r.Duration(now));
                        return new
                        {
                            cell = g.Key,
                            exhausts = g.Count(),
                            totalMinutes = Math.Round(total.TotalMinutes, 1),
                            avgMinutes = Math.Round(total.TotalMinutes / g.Count(), 1),
                            worstMinutes = Math.Round(max.TotalMinutes, 1)
                        };
                    }).ToList(),
                recoveries = recs.Where(r => r.End is not null)
                    .Select(r => new { cell = r.Cell, feeder = r.Feeder, part = r.Part, start = r.Start, end = r.End,
                                       minutes = Math.Round(((r.End ?? r.Start) - r.Start).TotalMinutes, 1) })
                    .ToList()
            };
        }
    }

    /// <summary>Downtime spans for the Daiya Graph timeline: (start, end, effective reason). Open span end = null.</summary>
    public IReadOnlyList<(DateTime Start, DateTime? End, string Reason)> Spans()
    {
        var now = DateTime.Now; var (f, t) = CurrentShiftWindow(now);
        lock (_gate) return ClippedStops(f, t, now).Select(s => (s.Start, s.End, s.Reason)).ToList();
    }

    /// <summary>Downtime spans WITH machine attribution: effective reason, the raw machine-side reason, and which
    /// machine(s) were in the stop state. Used by the Daiya report to list machine stop reasons and total the
    /// lost time per machine.</summary>
    public IReadOnlyList<(DateTime Start, DateTime? End, string Reason, string MachineReason, IReadOnlyList<int> Machines)> SpansDetailed()
    { var now = DateTime.Now; var (f, t) = CurrentShiftWindow(now); return SpansDetailed(f, t); }

    /// <summary>Downtime spans WITH machine attribution, clipped to [from, to) — the window of ONE Daiya sheet
    /// (a shift instance), so a Night sheet never carries the Morning's stops and a past shift within retention
    /// still has its downtime.</summary>
    public IReadOnlyList<(DateTime Start, DateTime? End, string Reason, string MachineReason, IReadOnlyList<int> Machines)> SpansDetailed(DateTime from, DateTime to)
    {
        var now = DateTime.Now;
        lock (_gate)
            return ClippedStops(from, to, now)
                .Select(s => (s.Start, s.End, s.Reason, s.MachineReason ?? "",
                              (IReadOnlyList<int>)(s.Machines ?? (IReadOnlyList<int>)Array.Empty<int>())))
                .ToList();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var p = JsonSerializer.Deserialize<Persisted>(File.ReadAllText(_path));
            if (p is null) return;
            // Any saved state comes back (rolling retention prunes what is too old); the activity flags only when the
            // file was written in the CURRENT shift instance — a restart in a new shift starts that shift's flags fresh.
            var now = DateTime.Now;
            _reasons.Restore(p.Stops ?? new(), p.OpenStop);
            _cells.Restore(p.Recoveries ?? new(), p.OpenRecoveries ?? new());
            _reasons.Prune(now - Retention);
            _cells.Prune(now - Retention);
            bool sameShift = p.ShiftKey is not null && p.ShiftKey == SafeShiftKey(now);
            _lastBoardAt = p.LastBoardAt;
            _sawBoard = sameShift && p.SawBoard;
            _sawActive = sameShift && p.SawActive;
            if (!sameShift) _reasons.CloseAtShiftEnd(now);
        }
        catch { /* corrupt/empty or old-format -> start clean */ }
    }

    private void Save()
    {
        try
        {
            var p = new Persisted(_date, _reasons.Stops.ToList(), _reasons.Open,
                _cells.Completed.ToList(), _cells.Open.ToList(), _lastBoardAt, _sawBoard, _sawActive, _shiftKey);
            File.WriteAllText(_path, JsonSerializer.Serialize(p));
        }
        catch { /* best-effort persistence */ }
    }

    public void Dispose() => _timer?.Dispose();
}
