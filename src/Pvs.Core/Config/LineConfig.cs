using System.Text.Json;
using System.Text.Json.Serialization;
using Pvs.Core.Shifts;

namespace Pvs.Core.Config;

public sealed class ShiftConfig
{
    public string Name { get; set; } = "";
    public string Start { get; set; } = "";   // "HH:mm"
    public string End { get; set; } = "";
}

/// <summary>A scheduled break window (time-of-day, recurs daily). Used only as a HINT for the downtime capture:
/// a stop inside a break defaults the suggested reason to "rest". NOT auto-excluded from downtime — a line is
/// often covered and keeps running through a break, so PVS trusts the production signal, not the clock.</summary>
public sealed class BreakWindowConfig
{
    public string Start { get; set; } = "";   // "HH:mm"
    public string End { get; set; } = "";
}

public sealed class MachineConfig
{
    public int Machine { get; set; }
    public string Port { get; set; } = "";        // e.g. "COM5" (empty for a Manual machine)
    public string Model { get; set; } = "";        // "F130" / "F209"
    public bool HasTrayFeeder { get; set; }

    /// <summary>A machine on this line that is NOT read over serial (e.g. a JUKI cell, or any off-serial mounter).
    /// Its feeders STILL appear in the feeder list and count toward the Canon-BOM total, but it opens no COM port
    /// and its parts changes are scanned MANUALLY (operator-triggered), not from a parts-out signal.</summary>
    public bool Manual { get; set; }

    /// <summary>True when this machine is read over serial (has a port and isn't flagged manual).</summary>
    public bool IsSerial => !Manual && !string.IsNullOrWhiteSpace(Port);
}

public sealed class SerialConfig
{
    public int BaudRate { get; set; } = 9600;
    public int DataBits { get; set; } = 7;
    public string Parity { get; set; } = "Even";
    public int StopBits { get; set; } = 1;
}

public sealed class ForecastConfig
{
    public int RedMinutes { get; set; } = 45;
    public int AmberMinutes { get; set; } = 120;
}

public sealed class CentralConfig
{
    public string Server { get; set; } = "";
    /// <summary>Fail-over DB path — a SECOND address for the same SQL instance over a different route (e.g. the
    /// DB box's Tailscale IP when Server is its intranet IP). Empty = no fail-over. The repo tries the preferred
    /// link, falls back to this on a connection failure, and sticks to whichever answered.</summary>
    public string FallbackServer { get; set; } = "";
    public string Database { get; set; } = "ReelPart-New";
    /// <summary>SQL login for the line PC (workgroup — cannot use Integrated Security to the remote box).</summary>
    public string UserId { get; set; } = "";
    public string Password { get; set; } = "";

    /// <summary>True when a SQL login is configured (so the app should attempt DB features).</summary>
    public bool HasCredentials => !string.IsNullOrWhiteSpace(Server) && !string.IsNullOrWhiteSpace(UserId);

    /// <summary>Builds the SQL Server connection string (SQL auth) for the primary path.</summary>
    public string ConnectionString() => ConnectionStringFor(Server);

    /// <summary>The fail-over connection string, or null when no <see cref="FallbackServer"/> is set.</summary>
    public string? FallbackConnectionString() =>
        string.IsNullOrWhiteSpace(FallbackServer) ? null : ConnectionStringFor(FallbackServer);

    // Short connect timeout so a dead path fails over fast instead of stalling the write for 15s.
    private string ConnectionStringFor(string server) =>
        $"Server={server};Database={Database};User ID={UserId};Password={Password};" +
        "TrustServerCertificate=True;Connect Timeout=8;";
}

/// <summary>Where PVS raises operational alerts (currently the DB-unreachable alert). Defaults to the plant Pi
/// bridge + Danial's number so it works on every line without per-line config; override per line if needed.</summary>
public sealed class AlertConfig
{
    /// <summary>gms-wabridge Pi (Tailscale — a different route than the intranet DB, so a DB outage can still send).</summary>
    public string WhatsAppBridgeUrl { get; set; } = "http://100.90.248.92:8080";
    public string WhatsAppRecipient { get; set; } = "60122185237";
    /// <summary>Alert on WhatsApp when NEITHER DB path is reachable (both primary and fallback down).</summary>
    public bool DbDownAlert { get; set; } = true;

    /// <summary>Alert the production manager on WhatsApp when a feeder's C1Z pickup rate falls below
    /// <see cref="PickupAlertRatePct"/>. Fires ONCE per feeder per lot/reel (re-arms on lot or reel change), and only
    /// once the feeder has at least <see cref="PickupAlertMinPicks"/> attempts so a low-sample feeder can't false-alarm.</summary>
    public bool PickupAlert { get; set; } = true;
    /// <summary>Pickup-rate threshold (percent). A feeder whose C1Z PR drops below this alerts. Default 99.8.</summary>
    public double PickupAlertRatePct { get; set; } = 99.8;
    /// <summary>Minimum attempted pickups (VC) before a feeder can raise a pickup-rate alert. Default 1000.</summary>
    public int PickupAlertMinPicks { get; set; } = 1000;
    /// <summary>WhatsApp recipients for the pickup-rate alert (comma-separated). Default: Raja Rao + Danish + Rezman.</summary>
    public string PickupAlertRecipients { get; set; } = "60163327003,60122445237,60126816059";
}

public sealed class BadgeConfig
{
    /// <summary>Reserved prefix that distinguishes a badge UID from a reel UID (interim: "9999").</summary>
    public string UidPrefix { get; set; } = "";
}

/// <summary>One report/alert recipient (a line's Person-In-Charge) and the role they cover, so alerts can be
/// routed by type (e.g. cycle-time → Production; parts shortage → Parts Control).</summary>
public sealed class EmailRecipient
{
    public string Email { get; set; } = "";
    public string Role { get; set; } = "";
}

/// <summary>
/// Email reporting. All PVS reports/alerts are emailed from a single Gmail (pvsbangi). Only the ONE line with
/// internet actually talks to Gmail (SMTP) — it is the "gateway"; the others POST their message to the gateway's
/// <c>/api/sendmail</c> over Tailscale so everything still originates from a line's PVS.
/// </summary>
public sealed class EmailConfig
{
    public bool Enabled { get; set; }
    public string SmtpHost { get; set; } = "smtp.gmail.com";
    public int SmtpPort { get; set; } = 587;
    /// <summary>The sender account (pvsbangi@gmail.com). Its app password is read from the sidecar
    /// <c>email-password.txt</c> next to the app (only the gateway line needs it).</summary>
    public string From { get; set; } = "pvsbangi@gmail.com";
    public string FromName { get; set; } = "PVS";
    /// <summary>Empty = THIS line is the gateway (has internet; sends via SMTP). Set (e.g.
    /// <c>http://100.69.81.105:5199</c>) = relay each message to that PVS's /api/sendmail instead.</summary>
    public string GatewayUrl { get; set; } = "";
    /// <summary>Shared secret the /api/sendmail endpoint requires, so only the plant's lines can relay through it.</summary>
    public string GatewayKey { get; set; } = "";
    /// <summary>This line's PICs — who its lot-complete/alert emails go to.</summary>
    public List<EmailRecipient> Recipients { get; set; } = new();

    public bool IsGateway => string.IsNullOrWhiteSpace(GatewayUrl);
}

/// <summary>
/// The forward-looking parts-shortage monitor: when it runs, how far ahead it looks, who hears about it,
/// and how often it is willing to repeat itself.
/// <para>
/// <b><see cref="Enabled"/> is off by default and must be turned on for exactly ONE line.</b> Every line PC
/// runs the same build against the same shared database, so five enabled lines means five identical emails.
/// The gateway line is the natural host (it has internet), but nothing here assumes that — the role can move.
/// </para>
/// </summary>
public sealed class ShortageMonitorConfig
{
    /// <summary>Run the daily shortage check on THIS line. Off by default — enable on one line only.</summary>
    public bool Enabled { get; set; }

    /// <summary>Local time of the daily run, "HH:mm". Default 07:45 — just after the day shift starts.</summary>
    public string DailyTime { get; set; } = "07:45";

    /// <summary>
    /// How many days ahead of a lot's delivery date a predicted shortage counts as URGENT — the window in
    /// which the customer can still be asked to ship. Shortages further out are still reported, but as
    /// information. Configurable because the useful lead time depends on how fast the customer can move.
    /// </summary>
    public int LeadTimeDays { get; set; } = 2;

    /// <summary>How far ahead to look for lots at all. Beyond this the order book is too soft to act on.</summary>
    public int HorizonDays { get; set; } = 21;

    /// <summary>
    /// Only count reels issued within this many days as still being at a line. StockOuts keeps a row per reel
    /// forever, and a reel consumed before PVS began writing balances back still shows its issued quantity —
    /// without a bound, months-old empty reels read as full stock and every shortage disappears.
    /// </summary>
    public int ReelDaysBack { get; set; } = 30;

    /// <summary>A reel's balance counts as actively TRACKED if PVS refreshed it within this many hours.</summary>
    public int ReelFreshHours { get; set; } = 72;

    /// <summary>
    /// Recipient <see cref="EmailRecipient.Role"/> keywords this report goes to — matched as a
    /// case-insensitive substring, so "Production", "production PIC" and "Parts Control" all hit. Deliberately
    /// a list, not one hard-coded spelling. If nothing matches, the report falls back to every recipient
    /// rather than going nowhere.
    /// </summary>
    public List<string> RecipientRoles { get; set; } = new() { "production", "parts", "material", "planner", "manager" };

    /// <summary>Hours before an unchanged, still-outstanding shortage is worth repeating. Default 24.</summary>
    public int ReminderHours { get; set; } = 24;

    /// <summary>How far the shortfall must move (percent of the last reported figure) to be worth an email.</summary>
    public double MaterialChangePercent { get; set; } = 5;

    /// <summary>
    /// Days between "nothing is short" emails. 0 (the default) = never send one. A report that usually says
    /// all-fine gets filtered within a week, and then the real warning is filtered too.
    /// </summary>
    public int AllClearDays { get; set; }

    public TimeSpan ReminderInterval => TimeSpan.FromHours(ReminderHours > 0 ? ReminderHours : 24);

    /// <summary>The configured run time, or 07:45 when it is missing or unparseable.</summary>
    public TimeOnly RunAt() => TimeOnly.TryParse(DailyTime, out var t) ? t : new TimeOnly(7, 45);

    /// <summary>True when a recipient's role matches any configured keyword. Blank keyword list =&gt; everyone.</summary>
    public bool RoleMatches(string? role)
    {
        if (RecipientRoles is null || RecipientRoles.Count == 0) return true;
        string r = (role ?? "").Trim();
        if (r.Length == 0) return false;
        foreach (var k in RecipientRoles)
        {
            if (string.IsNullOrWhiteSpace(k)) continue;
            if (r.Contains(k.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}

/// <summary>
/// The per-line configuration. One JSON file per line PC; the build is identical, only this differs.
/// </summary>
public sealed class LineConfig
{
    public int LineId { get; set; }
    public string LineName { get; set; } = "";
    public List<ShiftConfig> ShiftTimes { get; set; } = new();
    public List<MachineConfig> Machines { get; set; } = new();
    public SerialConfig Serial { get; set; } = new();
    public ForecastConfig Forecast { get; set; } = new();
    public CentralConfig Central { get; set; } = new();
    public AlertConfig Alerts { get; set; } = new();
    /// <summary>Carry-over/incomplete lots (PO numbers) to FORCE into this line's manual lot dropdown even though
    /// they fall outside the normal 7-day window — e.g. a July lot whose B-side was never run. Still gated to the
    /// selected model + a non-Delivered status, so it only ever surfaces the exact POs listed here.</summary>
    public List<string> ReopenLots { get; set; } = new();
    public BadgeConfig Badge { get; set; } = new();
    public EmailConfig Email { get; set; } = new();
    /// <summary>Forward-looking parts-shortage monitor. Disabled unless this line is the designated sender.</summary>
    public ShortageMonitorConfig ShortageMonitor { get; set; } = new();

    /// <summary>Password that authorises the on-screen "Shut down PVS" button (a clean stop frees the COM ports,
    /// e.g. to run serial-port test software). Defaults to "100732" when not set in the config.</summary>
    public string ShutdownPassword { get; set; } = "100732";

    /// <summary>
    /// Write live per-feeder remaining balances (by reel UID) back to StockOuts.Quantity every few minutes.
    /// Requires the pvs_ro login to have UPDATE on StockOuts.Quantity (already granted). Off by default.
    /// </summary>
    public bool SyncStockOuts { get; set; }

    /// <summary>
    /// Write PVS's live board count for this line to DailyProductionCount as an incremental row every 30 min
    /// (PVS becomes the production-count writer for this line). Requires pvs_ro INSERT on DailyProductionCount.
    /// Off by default until that grant is in place.
    /// </summary>
    public bool WriteProductionCount { get; set; }

    /// <summary>
    /// The lot lifecycle is SUPERVISOR-controlled only. When true (the agreed policy), PVS never auto-follows the
    /// production system's current lot (DailyProductionCount) and never auto-ends/clears a lot on a machine model
    /// change — only a supervisor badge selects, changes, or ends the lot. PVS still counts boards and writes the
    /// production count against whatever lot the supervisor set; it just never picks or drops a lot itself. On by
    /// default. (Prevents the "PVS (auto)" incident where PVS bound and wrote a stale auto-detected lot.)
    /// </summary>
    public bool SupervisorLotOnly { get; set; } = true;

    /// <summary>
    /// How often (minutes) to read each machine's own production counter (C1M) and reconcile it against
    /// PVS's board count. The heartbeat matters because the operator RESETS every machine's counter at end
    /// of lot (SOP): read only after that and the lot's final count is gone, so this bounds the worst-case
    /// loss to one interval. A read takes ~30s over serial, so don't set this low. 0/absent => 20.
    /// </summary>
    public int ReconcileMinutes { get; set; } = 20;

    /// <summary>
    /// How many extra times to re-ask a machine for its C1M production counter within one reconcile pass when it
    /// REFUSED the read (A4E00 "can't execute right now" / A5xx busy). The refusal is transient — the machine is
    /// mid-cycle or its panel is busy — so a few short-spaced retries turn an intermittent read into a reliable
    /// one (proven on the floor: all four L1 machines answer within ~2 min). Only the machines that actually
    /// refused are retried; a machine that already answered is left alone. 0 => no retry (old behaviour).
    /// </summary>
    public int ReconcileRetries { get; set; } = 4;

    /// <summary>Seconds to wait for a refused machine to answer on each retry attempt. Shorter than the first
    /// pass's 45s: a retry only needs the small PC field, and short gaps let more attempts fit the window.</summary>
    public int ReconcileRetrySeconds { get; set; } = 12;

    /// <summary>
    /// SHADOW mode for the C1Z pickup-based feeder consumption model ([[pvs-feeder-consumption-c1z]]). When on,
    /// every C1Z capture LOGS how far PVS's per-board feeder decrement has drifted from the machine's own pickup
    /// count (attempted VC) per feeder — writing NOTHING to stock. Use it to validate the model on a live line
    /// (e.g. the F114 80-piece gap) before trusting it. Off by default; safe to leave on (observe-only).
    /// </summary>
    public bool C1zShadow { get; set; } = false;

    /// <summary>Master switch for the AUTOMATIC per-feeder (C1Z) rotation — the background sweep that reads one
    /// machine's per-feeder pickups every ~45s. When OFF, PVS never auto-fires C1Z; the report is read ONLY on
    /// demand from the inventory page ("Stop machine → Read"). Runtime-togglable from the setup page (persisted to
    /// auto-commands.json, applied live without a restart). OFF by default: PVS is receive-mostly and does not poll
    /// the machine for per-feeder data — read C1Z on demand from the serial page. See CounterReconcilerService.</summary>
    public bool AutoC1z { get; set; } = false;

    /// <summary>Master switch for the AUTOMATIC production-count (C1M) reads — the periodic reconcile pass AND the
    /// board-complete-triggered capture that keep PVS's board count anchored to the machine's own counter. When
    /// OFF, PVS never auto-fires C1M; it is read ONLY on demand from the inventory page. Runtime-togglable from the
    /// setup page (persisted, applied live). OFF by default: PVS keeps its board count from the machine's own
    /// realtime R0 board-complete frames (receive), not by polling C1M — read C1M on demand from the serial page.
    /// See CounterReconcilerService.</summary>
    public bool AutoC1m { get; set; } = false;

    /// <summary>
    /// APPLY the C1Z pickup model: snap each feeder's remaining to <c>start − attempted-since-load</c> on every
    /// capture and accumulate attrition as <c>VC − TC</c>, with the per-board decrement as the live fallback
    /// between captures / when C1Z is refused. Off by default; turn on only AFTER shadow mode confirms the model.
    /// </summary>
    public bool C1zDecrement { get; set; } = false;

    /// <summary>Lot-completion accuracy: how far the actual component draw-down may exceed the SIMULATED bible
    /// (mount-points/board × lot size) before the lot is "way out of range" and a comprehensive check + recalc
    /// is triggered. Percent. Danial's contract: most components stay ≥99.95% (≤0.05% off); some run up to 0.2%.
    /// A machine whose board count deviates from the lot count by more than this % is recalculated against the
    /// bible at lot end (audited, reversible). Default 0.2.</summary>
    public double UsageToleranceOverPct { get; set; } = 0.2;

    /// <summary>Lot-completion accuracy TARGET (informational): the band most components should sit inside
    /// (≥99.95% = ≤0.05% off the bible). Percent. Default 0.05.</summary>
    public double UsageTargetPct { get; set; } = 0.05;

    /// <summary>Trust the last machine's (M4/Cell4 = PCB-out) own counter as the authoritative lot count: when a
    /// reconcile read shows the machine AHEAD of PVS (PVS missed boards while off/restarting), re-anchor PVS's
    /// lot count up to the machine's. A lower machine value (an operator lot-end reset — a backwards step) is
    /// NEVER auto-adopted. On by default. See CounterReconcilerService.</summary>
    public bool TrustMachineCount { get; set; } = true;

    /// <summary>
    /// Child boards per panel, by model name. One machine cycle (a board-complete / R0) mounts a full
    /// PANEL, so per-cycle consumption of a feeder = ProductBOM.Quantity (per child board) × this factor.
    /// e.g. L307 = 4 (a 4-up panel). Missing/0 => 1 (single board per cycle).
    /// </summary>
    public Dictionary<string, int> PanelBoards { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Seconds with no board-complete before the line counts as STOPPED PRODUCING — the threshold that surfaces
    /// the downtime reason prompt. Board cycle is ~40-48s, so the default 90 (about 2 missed cycles) ignores the
    /// normal inter-board gap. This only PROMPTS; a manual stop is recorded only when the operator taps a reason.
    /// </summary>
    public int StopDetectSeconds { get; set; } = 90;

    /// <summary>
    /// Scheduled break windows (day shift). Staggered lunch = two slots (12:00-12:45, 12:45-13:30) + an
    /// afternoon break (15:30-15:45). Used only as a HINT: a stop inside a break defaults the reason to "rest".
    /// Night shift is on-demand, so no fixed night breaks are configured here.
    /// </summary>
    public List<BreakWindowConfig> Breaks { get; set; } = new()
    {
        new() { Start = "12:00", End = "12:45" },
        new() { Start = "12:45", End = "13:30" },
        new() { Start = "15:30", End = "15:45" },
    };

    /// <summary>Builds the break-window helper from config (rows with a valid Start &lt; End).</summary>
    public Pvs.Core.Runtime.BreakWindows ToBreakWindows()
    {
        var windows = new List<(TimeOnly, TimeOnly)>();
        foreach (var b in Breaks)
            if (TimeOnly.TryParse(b.Start, out var s) && TimeOnly.TryParse(b.End, out var e) && e > s)
                windows.Add((s, e));
        return new Pvs.Core.Runtime.BreakWindows(windows);
    }

    /// <summary>Child boards per panel for a model (defaults to 1 when not configured).</summary>
    public int PanelBoardsFor(string? model) =>
        !string.IsNullOrWhiteSpace(model) && PanelBoards.TryGetValue(model.Trim(), out var n) && n > 0 ? n : 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static LineConfig Parse(string json) =>
        JsonSerializer.Deserialize<LineConfig>(json, Options)
            ?? throw new InvalidOperationException("Line config is empty or invalid.");

    public static LineConfig Load(string path) => Parse(File.ReadAllText(path));

    /// <summary>Builds the shift schedule from the configured shift times.</summary>
    public ShiftSchedule ToShiftSchedule() =>
        new(ShiftTimes
            .Select(s => new ShiftDefinition(s.Name, TimeOnly.Parse(s.Start), TimeOnly.Parse(s.End)))
            .ToList());

    /// <summary>True if a scanned UID looks like a badge (per the configured prefix). Empty prefix => never.</summary>
    public bool LooksLikeBadge(string uid) =>
        !string.IsNullOrEmpty(Badge.UidPrefix) &&
        (uid ?? "").TrimStart().StartsWith(Badge.UidPrefix, StringComparison.OrdinalIgnoreCase);
}
