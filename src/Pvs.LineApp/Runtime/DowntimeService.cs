using System.Text.Json;
using Pvs.Core.Runtime;
using Pvs.LineApp.Serial;

namespace Pvs.LineApp.Runtime;

/// <summary>
/// Samples the line's aggregate online state on a timer and feeds a <see cref="DowntimeLog"/>, so the
/// daily report can show line stops. State is persisted to a JSON file (keyed by date) and reloaded on
/// start, so a PVS restart mid-day doesn't lose the day's downtime. At a new-day boundary the log resets.
/// "Down" = no machine online.
/// </summary>
public sealed class DowntimeService : IDisposable
{
    private sealed record Persisted(string Date, DateTime? FirstUp, bool Up, bool Seeded, List<DownSpan> Spans);

    private readonly LineService _line;
    private readonly string _path;
    private readonly object _gate = new();
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
                if (today != _date) { _log.Clear(); _date = today; }
                _log.Sample(anyOnline, now);
                Save();
            }
        }
        catch { /* sampling is best-effort */ }
    }

    /// <summary>Today's production down spans, total downtime, and whether the line is currently up.</summary>
    public (IReadOnlyList<DownSpan> Spans, TimeSpan Total, bool Up) Snapshot()
    {
        lock (_gate)
        {
            var now = DateTime.Now;
            if (now.ToString("yyyy-MM-dd") != _date)
                return (Array.Empty<DownSpan>(), TimeSpan.Zero, _line.Listeners.Any(l => l.Channel.IsOnline));
            return (_log.ProductionSpans, _log.TotalDown(now), _log.IsUp);
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var p = JsonSerializer.Deserialize<Persisted>(File.ReadAllText(_path));
            if (p is null || p.Date != _date) return;   // no file or previous day -> start fresh
            _log.Restore(p.Spans ?? new List<DownSpan>(), p.FirstUp, p.Up, p.Seeded);
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
