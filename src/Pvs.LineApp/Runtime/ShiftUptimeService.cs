using System.Text.Json;
using Pvs.Core.Runtime;
using Pvs.Core.Shifts;
using Pvs.LineApp.Serial;

namespace Pvs.LineApp.Runtime;

/// <summary>
/// Samples the line's aggregate online state on a timer, windowed to the CURRENT shift, so the twice-daily
/// shift report can state how long the line was available, down and producing during the shift that just
/// ended. Unlike <see cref="DowntimeService"/> (calendar-day keyed, feeds the daily report), this resets and
/// ARCHIVES on each shift boundary — so the Night shift's midnight crossing is captured in one window.
/// <para>
/// On every shift change the ended shift's <see cref="ShiftUptimeSummary"/> is written to
/// <c>shift-uptime\&lt;shiftKey&gt;.json</c>; the live shift is persisted to <c>shift-uptime-current.json</c>
/// so a restart mid-shift keeps the running window. "Down" = no machine online.
/// </para>
/// </summary>
public sealed class ShiftUptimeService : IDisposable
{
    private sealed record CurrentState(string ShiftKey, string ShiftName, DateTime WindowStart, DateTime WindowEnd,
        DateTime? FirstUp, bool Up, bool Seeded, List<DownSpan> Spans);

    private readonly LineService _line;
    private readonly ShiftSchedule _shifts;
    private readonly string _dir;
    private readonly object _gate = new();
    private readonly DowntimeLog _log = new();
    private string _shiftKey = "";
    private string _shiftName = "";
    private DateTime _windowStart;
    private DateTime _windowEnd;
    private Timer? _timer;

    public ShiftUptimeService(LineService line, ShiftSchedule shifts, string dir)
    {
        _line = line;
        _shifts = shifts;
        _dir = dir;
        Directory.CreateDirectory(_dir);
        SetWindow(DateTime.Now);
        LoadCurrent();
    }

    public void Start() => _timer = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));

    private void SetWindow(DateTime now)
    {
        var shift = _shifts.ShiftAt(now);
        var start = now.Date + shift.Start.ToTimeSpan();
        if (start > now) start = start.AddDays(-1);       // night shift that began before midnight
        _shiftKey = _shifts.ShiftKey(now);
        _shiftName = shift.Name;
        _windowStart = start;
        _windowEnd = _shifts.EndAfter(start, shift);
    }

    private void Tick()
    {
        try
        {
            var now = DateTime.Now;
            lock (_gate)
            {
                string key = _shifts.ShiftKey(now);
                if (key != _shiftKey)
                {
                    ArchiveCurrent(_windowEnd);   // close the ended shift at its boundary, not "now"
                    _log.Clear();
                    SetWindow(now);
                }
                _log.Sample(_line.Listeners.Any(l => l.Channel.IsOnline), now);
                SaveCurrent();
            }
        }
        catch { /* sampling is best-effort; never disturb the line */ }
    }

    /// <summary>The summary for a given shift key: the live window if it is the current shift, else the
    /// archived file. Returns null if that shift was never recorded.</summary>
    public ShiftUptimeSummary? GetSummary(string shiftKey)
    {
        lock (_gate)
        {
            if (shiftKey == _shiftKey) return BuildCurrent(DateTime.Now);
        }
        return ReadArchived(shiftKey);
    }

    /// <summary>If <paramref name="now"/> has passed the current shift's end, archive it now so the report
    /// never races the 30s sampling tick at the boundary. Safe to call repeatedly.</summary>
    public void EnsureArchivedThrough(DateTime now)
    {
        lock (_gate)
        {
            if (now < _windowEnd) return;
            if (ReadArchived(_shiftKey) is not null) return;   // already archived by a tick
            ArchiveCurrent(_windowEnd);
        }
    }

    public ShiftUptimeSummary Current() { lock (_gate) return BuildCurrent(DateTime.Now); }

    private ShiftUptimeSummary BuildCurrent(DateTime now)
    {
        var (avail, down, prod, stops) = ShiftUptime.Compute(_log.FirstUp, _log.ProductionSpans, _windowStart, _windowEnd, now);
        return new ShiftUptimeSummary(_shiftKey, _shiftName, _windowStart, _windowEnd, _log.FirstUp, avail, down, prod, stops);
    }

    private void ArchiveCurrent(DateTime windowEnd)
    {
        try
        {
            var (avail, down, prod, stops) = ShiftUptime.Compute(_log.FirstUp, _log.ProductionSpans, _windowStart, windowEnd, windowEnd);
            var sum = new ShiftUptimeSummary(_shiftKey, _shiftName, _windowStart, windowEnd, _log.FirstUp, avail, down, prod, stops);
            File.WriteAllText(ArchivePath(_shiftKey), JsonSerializer.Serialize(sum));
        }
        catch { /* best-effort */ }
    }

    private ShiftUptimeSummary? ReadArchived(string shiftKey)
    {
        try
        {
            var p = ArchivePath(shiftKey);
            if (!File.Exists(p)) return null;
            return JsonSerializer.Deserialize<ShiftUptimeSummary>(File.ReadAllText(p));
        }
        catch { return null; }
    }

    // A shift key is "yyyy-MM-dd|Name" — make it a safe file name.
    private string ArchivePath(string shiftKey) =>
        Path.Combine(_dir, "shift-" + shiftKey.Replace('|', '_').Replace(':', '-') + ".json");

    private string CurrentPath => Path.Combine(_dir, "shift-uptime-current.json");

    private void SaveCurrent()
    {
        try
        {
            var st = new CurrentState(_shiftKey, _shiftName, _windowStart, _windowEnd,
                _log.FirstUp, _log.IsUp, _log.Seeded, _log.Spans.ToList());
            File.WriteAllText(CurrentPath, JsonSerializer.Serialize(st));
        }
        catch { /* best-effort */ }
    }

    private void LoadCurrent()
    {
        try
        {
            if (!File.Exists(CurrentPath)) return;
            var st = JsonSerializer.Deserialize<CurrentState>(File.ReadAllText(CurrentPath));
            if (st is null || st.ShiftKey != _shiftKey) return;   // a different shift now -> start this one fresh
            _log.Restore(st.Spans ?? new List<DownSpan>(), st.FirstUp, st.Up, st.Seeded);
        }
        catch { /* corrupt -> start clean */ }
    }

    public void Dispose() => _timer?.Dispose();
}
