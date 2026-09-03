using Pvs.Core.Data;

namespace Pvs.LineApp.Runtime;

/// <summary>
/// A light heartbeat to the central DB (SELECT 1) every ~45s, so the line-health rollup has a real connectivity
/// signal instead of the silent try/catch degradation the rest of the app uses. Isolated: it never blocks a
/// request and only reads its own last-outcome. Disabled when no SQL login is configured (the Null repo).
/// </summary>
public sealed class DbHealthService : IDisposable
{
    private readonly IReelPartRepository _repo;
    private readonly bool _enabled;
    private readonly WhatsAppSender? _alert;
    private readonly string _lineName;
    private readonly bool _alertOnDown;
    private readonly object _gate = new();
    private Timer? _timer;
    private bool _everRan;
    private bool _ok;
    private DateTime? _lastOkAt;
    private string? _lastError;
    private DateTime? _lastErrorAt;
    private int _consecutiveFails;
    private bool _alertedDown;
    private DateTime _lastBadgePreload;   // throttle for the offline badge-cache refresh

    public DbHealthService(IReelPartRepository repo, bool enabled,
        WhatsAppSender? alert = null, string lineName = "", bool alertOnDown = false)
    {
        _repo = repo;
        _enabled = enabled;
        _alert = alert;
        _lineName = string.IsNullOrWhiteSpace(lineName) ? "A line" : lineName;
        _alertOnDown = alertOnDown;
    }

    public bool Enabled => _enabled;

    /// <summary>true = last probe OK, false = last probe failed, null = configured but no probe yet (or disabled).</summary>
    public bool? Ok
    {
        get { lock (_gate) return !_enabled || !_everRan ? (bool?)null : _ok; }
    }

    public void Start()
    {
        if (_enabled) _timer = new Timer(_ => _ = Probe(), null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(45));
    }

    private async Task Probe()
    {
        bool ok;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            ok = await _repo.PingAsync(cts.Token);   // PingAsync tries BOTH the primary and the fallback route
            lock (_gate)
            {
                _everRan = true; _ok = ok;
                if (ok) _lastOkAt = DateTime.Now;
                else { _lastError = "no result"; _lastErrorAt = DateTime.Now; }
            }
            // Keep the OFFLINE badge cache complete while the DB is up, so operator/supervisor auth survives an
            // outage. Throttled (~15 min) and fire-and-forget so it never stalls the heartbeat.
            if (ok && DateTime.Now - _lastBadgePreload > TimeSpan.FromMinutes(15))
            {
                _lastBadgePreload = DateTime.Now;
                _ = _repo.PreloadBadgesAsync();
            }
        }
        catch (Exception ex)
        {
            ok = false;
            lock (_gate) { _everRan = true; _ok = false; _lastError = ex.Message; _lastErrorAt = DateTime.Now; }
        }

        // Alert on WhatsApp when NEITHER route is reachable — one message per outage, one on recovery. A false
        // ping means both the primary AND fallback failed, i.e. nothing on this line can reach the DB. Never done
        // in the lock, and fire-and-forget so it can't stall the timer.
        if (!_alertOnDown || _alert is null || !_alert.Configured) return;
        string? toSend = null;
        lock (_gate)
        {
            if (ok)
            {
                _consecutiveFails = 0;
                if (_alertedDown) { _alertedDown = false; toSend = $"✅ {_lineName}: production DB reachable again ({DateTime.Now:HH:mm}). Held counts are writing back. — PVS"; }
            }
            else
            {
                _consecutiveFails++;
                if (_consecutiveFails >= 3 && !_alertedDown)   // ~1.5 min of total unreachability before crying wolf
                {
                    _alertedDown = true;
                    toSend = $"⚠️ {_lineName}: CANNOT reach the production DB on EITHER route (LAN + Tailscale) as of {DateTime.Now:HH:mm}. Production is being HELD on the line and will write back automatically when the DB returns. — PVS";
                }
            }
        }
        if (toSend is not null) _ = _alert.SendAsync(toSend);
    }

    public object Snapshot()
    {
        lock (_gate)
        {
            if (!_enabled) return new { enabled = false, state = "na" };
            string state = !_everRan ? "warn" : _ok ? "ok" : "down";
            return new { enabled = true, state, ok = _ok, lastOkAt = _lastOkAt, lastError = _lastError, lastErrorAt = _lastErrorAt };
        }
    }

    public void Dispose() => _timer?.Dispose();
}
