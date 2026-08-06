using Pvs.Core.Data;
using Pvs.LineApp.Serial;

namespace Pvs.LineApp.Runtime;

/// <summary>
/// Fires the per-line shift report email at each shift boundary (Day ends 19:30, Night ends 07:30). Builds the
/// shift that JUST ended — models/lots completed plus available / actual-production / down time — and emails the
/// line's PICs. Runs inside the line app on a 60s timer, so it needs no external scheduler and no operator PC:
/// Line 1 (the gateway) sends its own report and relays the others'. Fires once per boundary, guarded by a marker
/// file so a restart within the window doesn't double-send. Best-effort throughout — never disturbs the line.
/// </summary>
public sealed class ShiftReportScheduler : IHostedService, IDisposable
{
    // How long after the boundary the report may still fire (covers a late shift-change scan / brief outage).
    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(45);

    private readonly LineService _line;
    private readonly IReelPartRepository _repo;
    private readonly EmailSender _email;
    private readonly ILogger<ShiftReportScheduler> _log;
    private readonly string _markerPath;
    private readonly object _gate = new();
    private string _lastSent = "";
    private Timer? _timer;

    public ShiftReportScheduler(LineService line, IReelPartRepository repo, EmailSender email, ILogger<ShiftReportScheduler> log)
    {
        _line = line; _repo = repo; _email = email; _log = log;
        _markerPath = Path.Combine(AppContext.BaseDirectory, "shift-report-sent.txt");
        try { if (File.Exists(_markerPath)) _lastSent = File.ReadAllText(_markerPath).Trim(); } catch { /* first run */ }
    }

    public Task StartAsync(CancellationToken ct)
    {
        _timer = new Timer(_ => { _ = TickAsync(); }, null, TimeSpan.FromSeconds(25), TimeSpan.FromSeconds(60));
        return Task.CompletedTask;
    }

    private async Task TickAsync()
    {
        try
        {
            if (!_email.Enabled) return;
            var now = DateTime.Now;
            var shifts = _line.Config.ToShiftSchedule();

            // The boundary we're just past = the start of the CURRENT shift; the shift that ended is the one before it.
            var cur = shifts.ShiftAt(now);
            var curStart = now.Date + cur.Start.ToTimeSpan();
            if (curStart > now) curStart = curStart.AddDays(-1);          // night shift began before midnight
            if (now - curStart > Grace) return;                           // outside the send window
            var endedKey = shifts.ShiftKey(curStart.AddSeconds(-1));       // the shift just before this boundary
            lock (_gate) { if (endedKey == _lastSent) return; }            // already sent for this boundary

            var uptimeSvc = _line.ShiftUptime;
            if (uptimeSvc is null) return;
            uptimeSvc.EnsureArchivedThrough(now);
            var summary = uptimeSvc.GetSummary(endedKey);
            if (summary is null) { _log.LogDebug("Shift report {Key}: no uptime summary yet — retry.", endedKey); return; }

            var report = await ShiftReport.BuildAsync(_line.Config, _repo, summary);
            var ok = await _email.SendToPicsAsync(ShiftReport.Subject(report), ShiftReport.Body(report));
            if (ok)
            {
                lock (_gate) { _lastSent = endedKey; }
                try { File.WriteAllText(_markerPath, endedKey); } catch { /* best-effort */ }
                _log.LogInformation("Shift report SENT for {Key}: {Boards} boards, {Lots} lot(s).", endedKey, report.TotalBoards, report.Lots.Count);
            }
            else _log.LogWarning("Shift report {Key} send failed — retry next tick.", endedKey);
        }
        catch (Exception ex) { _log.LogDebug(ex, "Shift report tick failed."); }
    }

    public Task StopAsync(CancellationToken ct) { _timer?.Change(Timeout.Infinite, Timeout.Infinite); return Task.CompletedTask; }
    public void Dispose() => _timer?.Dispose();
}
