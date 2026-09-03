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
/// Persists to a per-day JSON file (survives a restart, resets at the day boundary) and closes an open span at
/// the shift boundary. READ-ONLY toward the machines — it never sends a control command.
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
        lock (_gate) { RollDayIfNeeded(at); _cells.OnPartsOut(machine, feeder, part, at); Save(); }
    }

    private void OnBoard(int machine, DateTime at)
    {
        lock (_gate)
        {
            RollDayIfNeeded(at);
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
            RollDayIfNeeded(now);
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
                RollDayIfNeeded(now);
                EvaluateConditions(now);
                // Shift rollover: an open span the line never recovered from ends at the boundary.
                var key = SafeShiftKey(now);
                if (key != _shiftKey) { _reasons.CloseAtShiftEnd(now); _shiftKey = key; }
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

    private void RollDayIfNeeded(DateTime now)
    {
        string today = now.ToString("yyyy-MM-dd");
        if (today == _date) return;
        _reasons.Clear();
        _cells.Clear();
        _lastBoardAt = null;
        _sawBoard = false;
        _sawActive = false;
        _date = today;
        _shiftKey = SafeShiftKey(now);
    }

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
    public object Report()
    {
        var now = DateTime.Now;
        lock (_gate)
        {
            return new
            {
                byReason = _reasons.ByReason(now)
                    .Select(r => new { reason = r.Reason, minutes = Math.Round(r.Total.TotalMinutes, 1), count = r.Count })
                    .ToList(),
                stops = _reasons.Stops.Concat(_reasons.Open is { } o ? new[] { o } : Array.Empty<ReasonStop>())
                    .OrderBy(s => s.Start)
                    .Select(s => new { reason = s.Reason, machineReason = s.MachineReason, operatorReason = s.OperatorReason,
                                       note = s.Note, machines = s.Machines, start = s.Start, end = s.End,
                                       minutes = Math.Round(s.Duration(now).TotalMinutes, 1) })
                    .ToList(),
                byCell = _cells.ByCell()
                    .Select(c => new
                    {
                        cell = c.Cell,
                        exhausts = c.Count,
                        totalMinutes = Math.Round(c.Total.TotalMinutes, 1),
                        avgMinutes = c.Count > 0 ? Math.Round(c.Total.TotalMinutes / c.Count, 1) : 0,
                        worstMinutes = Math.Round(c.Max.TotalMinutes, 1)
                    }).ToList(),
                recoveries = _cells.Completed
                    .OrderBy(r => r.Start)
                    .Select(r => new { cell = r.Cell, feeder = r.Feeder, part = r.Part, start = r.Start, end = r.End,
                                       minutes = Math.Round(((r.End ?? r.Start) - r.Start).TotalMinutes, 1) })
                    .ToList()
            };
        }
    }

    /// <summary>Downtime spans for the Daiya Graph timeline: (start, end, effective reason). Open span end = null.</summary>
    public IReadOnlyList<(DateTime Start, DateTime? End, string Reason)> Spans()
    {
        lock (_gate)
            return _reasons.Stops.Concat(_reasons.Open is { } o ? new[] { o } : Array.Empty<ReasonStop>())
                .OrderBy(s => s.Start)
                .Select(s => (s.Start, s.End, s.Reason))
                .ToList();
    }

    /// <summary>Downtime spans WITH machine attribution: effective reason, the raw machine-side reason, and which
    /// machine(s) were in the stop state. Used by the Daiya report to list machine stop reasons and total the
    /// lost time per machine.</summary>
    public IReadOnlyList<(DateTime Start, DateTime? End, string Reason, string MachineReason, IReadOnlyList<int> Machines)> SpansDetailed()
    {
        lock (_gate)
            return _reasons.Stops.Concat(_reasons.Open is { } o ? new[] { o } : Array.Empty<ReasonStop>())
                .OrderBy(s => s.Start)
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
            if (p is null || p.Date != _date) return;   // no file or a previous day -> start fresh
            _reasons.Restore(p.Stops ?? new(), p.OpenStop);
            _cells.Restore(p.Recoveries ?? new(), p.OpenRecoveries ?? new());
            _lastBoardAt = p.LastBoardAt;
            _sawBoard = p.SawBoard;
            _sawActive = p.SawActive;
            _shiftKey = p.ShiftKey ?? _shiftKey;
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
