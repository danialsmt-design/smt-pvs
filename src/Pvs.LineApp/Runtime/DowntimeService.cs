using System.Text.Json;
using Pvs.Core.Runtime;
using Pvs.LineApp.Serial;

namespace Pvs.LineApp.Runtime;

/// <summary>
/// Samples the line's aggregate online state on a timer and feeds a <see cref="DowntimeLog"/>, so the
/// daily report can show line stops. State is persisted to a JSON file and reloaded on start, so a PVS restart
/// doesn't lose downtime. Spans are kept in a ROLLING window (48 h) and clipped to the caller's window — never
/// wiped at midnight (that dropped a stop spanning 00:00 and, with FirstUp reset, hid a post-midnight outage;
/// audit M3/M7, 2026-09-07). "Down" = no machine online.
/// </summary>
public sealed class DowntimeService : IDisposable
{
    private sealed record Persisted(string Date, DateTime? FirstUp, bool Up, bool Seeded, List<DownSpan> Spans);

    private readonly LineService _line;
    private readonly string _path;
    private readonly object _gate = new();
    private static readonly TimeSpan Retention = TimeSpan.FromHours(48);
    private readonly DowntimeLog _log = new();
    private string _date;
    private Timer? _timer;

    public DowntimeService(LineService line, string path)
    {
        _line = line;
        _path = path;
        _date = DateTime.Now.ToString("yyyy-MM-dd");
        Load();
    }

    public void Start() => _timer = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));

    private void Tick()
    {
        try
        {
            bool anyOnline = _line.Listeners.Any(l => l.Channel.IsOnline);
            var now = DateTime.Now;
            lock (_gate)
            {
                string today = now.ToString("yyyy-MM-dd");
                if (today != _date) { _date = today; _log.Prune(now - Retention); }   // rolling retention, no wipe
                _log.Sample(anyOnline, now);
                Save();
            }
        }
        catch { /* sampling is best-effort */ }
    }

    /// <summary>Today's (calendar day so far) production down spans, total downtime, and whether the line is up.</summary>
    public (IReadOnlyList<DownSpan> Spans, TimeSpan Total, bool Up) Snapshot()
    {
        var now = DateTime.Now;
        return Snapshot(now.Date, now.Date.AddDays(1));
    }

    /// <summary>Production down spans clipped to [from, to), their total, and whether the line is currently up.</summary>
    public (IReadOnlyList<DownSpan> Spans, TimeSpan Total, bool Up) Snapshot(DateTime from, DateTime to)
    {
        lock (_gate)
        {
            var now = DateTime.Now;
            var clipped = new List<DownSpan>();
            foreach (var s in _log.ProductionSpans)
            {
                var start = s.Start < from ? from : s.Start;
                var rawEnd = s.End ?? now;
                var end = rawEnd > to ? to : rawEnd;
                if (end <= start) continue;
                clipped.Add(new DownSpan(start, (s.End is null && to > now && rawEnd == end) ? null : end));
            }
            var total = clipped.Aggregate(TimeSpan.Zero, (a, s) => a + s.Duration(now));
            return (clipped, total, _log.IsUp);
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var p = JsonSerializer.Deserialize<Persisted>(File.ReadAllText(_path));
            if (p is null) return;
            _log.Restore(p.Spans ?? new List<DownSpan>(), p.FirstUp, p.Up, p.Seeded);
            _log.Prune(DateTime.Now - Retention);
        }
        catch { /* corrupt/empty -> start clean */ }
    }

    private void Save()
    {
        try
        {
            var p = new Persisted(_date, _log.FirstUp, _log.IsUp, _log.Seeded, _log.Spans.ToList());
            File.WriteAllText(_path, JsonSerializer.Serialize(p));
        }
        catch { /* best-effort persistence */ }
    }

    public void Dispose() => _timer?.Dispose();
}
