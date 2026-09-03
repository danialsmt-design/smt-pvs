using Pvs.Core.Config;
using Pvs.Core.Data;
using Pvs.Core.Inventory;

namespace Pvs.LineApp.Runtime;

/// <summary>The outcome of one shortage check — what was found, what was sent, and why.</summary>
public sealed record ShortageRunResult(
    ShortageForecast Forecast, ShortageAlertPlan Plan, bool Sent, string Subject, string Body, string Message)
{
    public static ShortageRunResult NotRun(DateTime at, string why) => new(
        ShortageForecast.Empty(at),
        new ShortageAlertPlan(Array.Empty<ShortageAlertItem>(), Array.Empty<ShortageAlertEntry>(), 0),
        false, "", "", why);
}

/// <summary>
/// The forward-looking parts-shortage monitor: once a day (and on demand), works out which upcoming lot will
/// run a part out, and emails the production manager while there is still time to ask the customer to ship.
/// Follows <see cref="ShiftReportScheduler"/>'s shape — a 60-second timer inside the line app, so it needs no
/// external scheduler and no operator PC.
/// <para>
/// <b>This runs for weeks inside the app that drives a live production line's parts interlock.</b> Everything
/// here is therefore best-effort in the same sense as <see cref="EmailSender"/>: every failure — database
/// unreachable, gateway line down, SMTP refused, malformed config, corrupt state, a query that returns
/// nothing — is caught, logged and shrugged off. No exception may escape the timer callback. A missed report
/// is acceptable; stopping the line is not.
/// </para>
/// <para>
/// <b>One line only sends.</b> Every line PC runs this same build against the same shared database, so the
/// check is gated on a per-line config flag that is OFF by default. When it is off the timer is never started
/// at all.
/// </para>
/// </summary>
public sealed class ShortageMonitorService : IHostedService, IDisposable
{
    /// <summary>How long after the configured time the daily run may still fire (covers a restart or an outage).</summary>
    private static readonly TimeSpan Grace = TimeSpan.FromHours(3);

    private readonly LineConfig _config;
    private readonly IReelPartRepository _repo;
    private readonly EmailSender _email;
    private readonly ILogger<ShortageMonitorService> _log;
    private readonly ShortageStateStore _store;
    private int _running;                       // 0/1 overlap guard — a slow query must not let two passes run
    private Timer? _timer;

    public ShortageMonitorService(LineConfig config, IReelPartRepository repo, EmailSender email, ILogger<ShortageMonitorService> log)
    {
        _config = config; _repo = repo; _email = email; _log = log;
        _store = new ShortageStateStore(Path.Combine(AppContext.BaseDirectory, "shortage-monitor.json"), log);
    }

    private ShortageMonitorConfig Cfg => _config.ShortageMonitor ?? new ShortageMonitorConfig();

    public Task StartAsync(CancellationToken ct)
    {
        if (!Cfg.Enabled)
        {
            _log.LogInformation("Parts-shortage monitor is off on this line (shortageMonitor.enabled = false).");
            return Task.CompletedTask;
        }
        _log.LogInformation("Parts-shortage monitor on: daily at {At}, {Lead}-day lead time, {Horizon}-day horizon.",
            Cfg.RunAt(), Cfg.LeadTimeDays, Cfg.HorizonDays);
        _timer = new Timer(_ => { _ = TickAsync(); }, null, TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(60));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        return Task.CompletedTask;
    }

    public void Dispose() => _timer?.Dispose();

    private async Task TickAsync()
    {
        // Nothing below may throw into the host. This catch is the last line of that promise.
        try
        {
            if (!Cfg.Enabled) return;
            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;   // a previous pass is still going
            try
            {
                var now = DateTime.Now;
                var due = now.Date + Cfg.RunAt().ToTimeSpan();
                if (now < due || now - due > Grace) return;                     // outside today's window

                string today = now.ToString("yyyy-MM-dd");
                var state = _store.Load();
                if (string.Equals(state.LastRunDate, today, StringComparison.Ordinal)) return;   // already ran today

                // RunCoreAsync, not RunAsync: this tick already holds the overlap guard.
                var result = await RunCoreAsync(send: true, now, CancellationToken.None);

                // Mark the day done whether or not anything was sent: the check ran, and retrying it every
                // minute for three hours would hammer a database that has nothing new to say.
                state = _store.Load();
                state.LastRunDate = today;
                _store.Save(state);

                _log.LogInformation("Parts-shortage check: {Found} shortage(s), sent={Sent}. {Msg}",
                    result.Forecast.Findings.Count, result.Sent, result.Message);
            }
            finally { Interlocked.Exchange(ref _running, 0); }
        }
        catch (Exception ex) { _log.LogWarning(ex, "Parts-shortage monitor tick failed — ignored."); }
    }

    /// <summary>
    /// Runs the whole check once: gather, forecast, decide what is worth saying, optionally send, persist.
    /// Safe to call at any time (the on-demand endpoint uses it). Never throws.
    /// </summary>
    public async Task<ShortageRunResult> RunAsync(bool send, CancellationToken ct = default)
    {
        var now = DateTime.Now;
        // Same overlap guard as the timer, so a refreshed browser tab cannot stack passes on a 4 GB DB box.
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            return ShortageRunResult.NotRun(now, "a parts check is already running — try again in a moment.");
        try { return await RunCoreAsync(send, now, ct); }
        finally { Interlocked.Exchange(ref _running, 0); }
    }

    private async Task<ShortageRunResult> RunCoreAsync(bool send, DateTime now, CancellationToken ct)
    {
        try
        {
            var cfg = Cfg;
            var forecast = await BuildForecastAsync(now, cfg, ct);
            var state = _store.Load();
            var plan = ShortageAlertLedger.Decide(
                state.Alerts, forecast, now,
                new ShortageAlertOptions(cfg.ReminderInterval, cfg.MaterialChangePercent));

            bool allClear = !plan.ShouldSend && !forecast.Any && DueForAllClear(state, cfg, now);
            bool worthSending = plan.ShouldSend || allClear;

            string subject = ShortageReport.Subject(plan, forecast);
            string body = ShortageReport.Body(plan, forecast, cfg.LeadTimeDays, now);

            bool sent = false;
            string message;
            if (!worthSending)
            {
                message = forecast.Any
                    ? $"{forecast.Findings.Count} known shortage(s), nothing new to say ({plan.SuppressedQuiet} unchanged)."
                    : "nothing short.";
            }
            else if (!send)
            {
                message = "preview only — not sent.";
            }
            else
            {
                sent = await SendAsync(subject, body, cfg, ct);
                message = sent ? "sent." : "send failed or email disabled — state not advanced.";
            }

            // Only remember having said something once it actually went out. If the gateway was down, the
            // next run must say it again rather than assume he was told.
            if (sent || !worthSending)
            {
                state.Alerts = KeepSaying(plan, sent);
                if (sent && allClear) state.LastAllClear = now;
                _store.Save(state);
            }

            return new ShortageRunResult(forecast, plan, sent, subject, body, message);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Parts-shortage check failed — ignored.");
            return ShortageRunResult.NotRun(now, "check failed: " + ex.Message);
        }
    }

    /// <summary>
    /// When an email could not be delivered, roll the "last sent" clock back for everything it would have
    /// carried, so the next run re-offers it instead of falling silent about a live shortage.
    /// </summary>
    private static List<ShortageAlertEntry> KeepSaying(ShortageAlertPlan plan, bool sent)
    {
        if (sent) return plan.NextState.ToList();
        var unsent = new HashSet<string>(plan.Outstanding.Select(i => i.PartNumber + "|" + i.LotNo), StringComparer.OrdinalIgnoreCase);
        return plan.NextState.Where(e => !unsent.Contains(e.Key)).ToList();
    }

    private static bool DueForAllClear(ShortageMonitorState state, ShortageMonitorConfig cfg, DateTime now) =>
        cfg.AllClearDays > 0 &&
        (state.LastAllClear is not DateTime last || (now - last).TotalDays >= cfg.AllClearDays);

    /// <summary>Routes by recipient ROLE, falling back to everyone rather than letting an alert go nowhere.</summary>
    private async Task<bool> SendAsync(string subject, string body, ShortageMonitorConfig cfg, CancellationToken ct)
    {
        if (!_email.Enabled) { _log.LogInformation("Parts-shortage alert not sent: email is disabled in config."); return false; }
        var all = _config.Email?.Recipients ?? new List<EmailRecipient>();
        var matched = all.Where(r => cfg.RoleMatches(r?.Role)).ToList();
        if (matched.Count == 0 && all.Count > 0)
        {
            _log.LogWarning("No recipient role matched {Roles} — sending the parts-shortage alert to all {N} recipients.",
                string.Join("/", cfg.RecipientRoles ?? new List<string>()), all.Count);
            matched = all;
        }
        if (matched.Count == 0) { _log.LogWarning("Parts-shortage alert not sent: no recipients configured."); return false; }
        return await _email.SendAsync(matched.Select(r => r.Email), subject, body, ct);
    }

    /// <summary>
    /// Pulls the three inputs the forecast needs and hands them to the pure logic. Each query is guarded on its
    /// own: losing the price list or the coverage figures degrades the report, it does not cancel it.
    /// </summary>
    private async Task<ShortageForecast> BuildForecastAsync(DateTime now, ShortageMonitorConfig cfg, CancellationToken ct)
    {
        var lots = await _repo.GetUpcomingLotsAsync(cfg.HorizonDays, ct);
        if (lots.Count == 0) return ShortageForecast.Empty(now);

        var models = lots.Select(l => l.Model).Where(m => !string.IsNullOrWhiteSpace(m))
                         .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var bom = await _repo.GetBomUsageForModelsAsync(models, ct);
        var usage = BomUsageResolver.Resolve(bom);
        if (usage.Count == 0) return ShortageForecast.Empty(now);

        var parts = usage.Select(u => u.PartNumber).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var stock = await _repo.GetPartStockAsync(parts, cfg.ReelDaysBack, cfg.ReelFreshHours, ct);

        ShortageCoverage coverage = ShortageCoverage.Unknown;
        try { coverage = Coverage(await _repo.GetReelTrackingByLineAsync(cfg.ReelDaysBack, cfg.ReelFreshHours, ct)); }
        catch (Exception ex) { _log.LogDebug(ex, "Reel-tracking coverage unavailable — report will not claim any."); }

        return ShortageForecaster.Analyse(
            now, lots, usage, stock, new ShortageOptions(cfg.LeadTimeDays, cfg.HorizonDays), coverage);
    }

    /// <summary>
    /// A line counts as TRACKED when PVS has refreshed some of its reel balances recently. The test is
    /// deliberately a low bar rather than a percentage: most of the piece count on ANY line is stale rows for
    /// reels long consumed, so even a line syncing every few minutes shows only single-digit percent "fresh".
    /// What distinguishes a synced line from an unsynced one is that it has fresh reels at all — measured live,
    /// Lines 1/2/5 refresh 24-92 reels apiece while Lines 3/4 refresh none or one.
    /// </summary>
    internal static ShortageCoverage Coverage(IReadOnlyList<LineReelTracking> rows)
    {
        long total = 0, tracked = 0;
        var trackedLines = new List<string>();
        var untracked = new List<string>();
        foreach (var r in rows ?? Array.Empty<LineReelTracking>())
        {
            if (r is null || r.Qty <= 0) continue;
            total += r.Qty;
            tracked += Math.Max(0, r.FreshQty);
            string name = string.IsNullOrWhiteSpace(r.Line) || r.Line.Trim('-').Length == 0 ? ShortageReport.NoLine : r.Line.Trim();
            bool live = r.FreshReels > 0 && r.FreshQty * 100 >= r.Qty;    // some reels refreshed, and not a rounding artefact
            (live ? trackedLines : untracked).Add(name);
        }
        trackedLines.Sort(StringComparer.OrdinalIgnoreCase);
        untracked.Sort(StringComparer.OrdinalIgnoreCase);
        return new ShortageCoverage(total, tracked, trackedLines, untracked);
    }
}
