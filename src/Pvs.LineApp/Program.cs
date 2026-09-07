using Pvs.Core.Config;
using Pvs.Core.Data;
using Pvs.Core.Runtime;
using Pvs.Core.Verification;
using Pvs.Data;
using Pvs.LineApp.Records;
using Pvs.LineApp.Serial;
using Pvs.LineApp.Verification;

var builder = WebApplication.CreateBuilder(args);

// Persist logs to a daily file on the line PC (the PCs kept none, so silent failures left no trace).
builder.Logging.AddProvider(new Pvs.LineApp.Logging.FileLoggerProvider(
    Path.Combine(AppContext.BaseDirectory, "logs")));

// Load the per-line config (path can be overridden with --config or PVS_CONFIG).
string configPath = builder.Configuration["config"]
    ?? Environment.GetEnvironmentVariable("PVS_CONFIG")
    ?? Path.Combine(AppContext.BaseDirectory, "line.config.json");

LineConfig lineConfig = File.Exists(configPath)
    ? LineConfig.Load(configPath)
    : new LineConfig { LineId = 0, LineName = "UNCONFIGURED" };

// If the config has no DB password, read it from a local sidecar file (db-password.txt) so the
// operator can supply it on the line PC without it living in the shared config.
if (string.IsNullOrWhiteSpace(lineConfig.Central.Password))
{
    string pwFile = Path.Combine(AppContext.BaseDirectory, "db-password.txt");
    if (File.Exists(pwFile))
        lineConfig.Central.Password = File.ReadAllText(pwFile).Trim();
}

builder.Services.AddSingleton(lineConfig);

// Connection string: an explicit PVS_CONNSTR override wins (used for validation with Integrated
// Security), otherwise build it from the config's SQL login when one is present.
string? connOverride = Environment.GetEnvironmentVariable("PVS_CONNSTR");
string? conn = !string.IsNullOrWhiteSpace(connOverride) ? connOverride
    : lineConfig.Central.HasCredentials ? lineConfig.Central.ConnectionString()
    : null;

// Fail-safe: a second route to the same DB (config FallbackServer) — used only when the override isn't set.
string? connFallback = string.IsNullOrWhiteSpace(connOverride) ? lineConfig.Central.FallbackConnectionString() : null;

// DB repo: real one when a connection is available, else a null repo so the monitor still runs.
builder.Services.AddSingleton<IReelPartRepository>(_ =>
    conn is not null ? new SqlReelPartRepository(conn, connFallback) : new NullReelPartRepository());

// Stage-2 record sink: local append-only log next to the app.
builder.Services.AddSingleton<ILineRecordSink>(_ =>
    new JsonlRecordSink(Path.Combine(AppContext.BaseDirectory, "records")));

// Remembers which reel is on each feeder, persisted next to the app.
builder.Services.AddSingleton(new Pvs.LineApp.Inventory.FeederReelStore(
    Path.Combine(AppContext.BaseDirectory, "inventory.json")));

// Email reporting (pvsbangi). The gateway line SMTPs to Gmail; other lines relay to it.
builder.Services.AddSingleton<Pvs.LineApp.Runtime.EmailSender>();

builder.Services.AddSingleton<LineService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<LineService>());
// Emails the per-line shift report at each shift boundary (07:30 / 19:30), from inside the app — no operator PC.
builder.Services.AddHostedService<Pvs.LineApp.Runtime.ShiftReportScheduler>();
// Forward-looking parts-shortage monitor. Registered on every line but INERT unless this line's config turns
// it on (shortageMonitor.enabled), so five lines do not send five copies of the same factory-wide report.
builder.Services.AddSingleton<Pvs.LineApp.Runtime.ShortageMonitorService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Pvs.LineApp.Runtime.ShortageMonitorService>());
// Holds the reels an operator has scanned during a standby-reel check (in-memory; no DB writes).
builder.Services.AddSingleton<Pvs.LineApp.Runtime.StandbyScanState>();
builder.Services.AddSingleton<Pvs.LineApp.Runtime.BoardInputState>();

var app = builder.Build();

app.UseDefaultFiles();
// Never let a browser cache the HTML/JS UI — otherwise a redeploy (e.g. the Daiya date/shift dropdown) stays
// invisible behind a stale cached page. Static assets are tiny and same-origin, so no-cache is fine here.
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        var p = ctx.File.Name;
        if (p.EndsWith(".html", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
            ctx.Context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
    }
});

// ---- API ----

// Line + machine status.
// Leading model token of a program name, e.g. "L307 - B SIDE _Cell4.PW4" -> "L307".
static string ModelOfProgram(string? p)
{
    var m = System.Text.RegularExpressions.Regex.Match(p ?? "", @"^\s*([A-Za-z0-9]+)");
    return m.Success ? m.Groups[1].Value.ToUpperInvariant() : "";
}
// The C1M/C1Z data name is the program name WITHOUT the .PWx file extension (Danial, floor-verified).
static string? MachineReportName(string? program)
{
    if (string.IsNullOrWhiteSpace(program)) return null;
    int dot = program.LastIndexOf('.');
    return dot > 0 ? program[..dot] : program;
}
app.MapGet("/api/status", (LineService line) =>
{
    var machines = line.Listeners.Select(l =>
    {
        var rx = line.RxStats(l.Channel.Machine);
        return new
        {
            machine = l.Channel.Machine,
            port = l.Port,
            portOpen = l.IsOpen,
            online = l.Channel.IsOnline,
            boardsPerHour = l.Channel.BoardRate.BoardsPerHour is double b ? Math.Round(b, 1) : (double?)null,
            messages = rx.Count,
            lastMessage = rx.Last,
            lastAt = rx.At == default ? (DateTime?)null : rx.At,
            program = l.Channel.ProgramName,
            model = ModelOfProgram(l.Channel.ProgramName),
            // The machine's OWN operating state, latched from its Sony R1 stream — the honest liveness signal
            // (boardsPerHour is a productive-time rate that freezes when the line stops, so never read it as "running").
            condition = l.Channel.Condition.Condition.ToString(),
            conditionSince = l.Channel.Condition.Since == default ? (DateTime?)null : l.Channel.Condition.Since,
            running = l.Channel.Condition.IsRunning,
            skipped = line.Coordinator?.IsMachineSkipped(l.Channel.Machine) ?? false
        };
    }).ToList();

    // Mid-lot PROGRAM MISMATCH: online machines that disagree on the loaded model. A program was changed on a
    // machine without the lot being ended/changed (e.g. a changeover that missed a cell — M4 left on the old
    // model while the rest moved on). The UI flashes this so it can't be run past unnoticed.
    // Skipped machines are out of the run — never let one raise (or count toward) a program-mismatch alarm.
    var running = machines.Where(m => m.online && m.model.Length > 0 && !m.skipped).ToList();
    var models = running.Select(m => m.model).Distinct().ToList();
    object? programWarn = null;
    if (models.Count > 1)
    {
        var majority = running.GroupBy(m => m.model).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First().Key;
        var odd = running.Where(m => m.model != majority).Select(m => $"M{m.machine}:{m.model}").ToArray();
        programWarn = new
        {
            warn = true,
            kind = "machines-disagree",
            majority,
            odd,
            message = $"⚠ PROGRAM MISMATCH — {string.Join(", ", odd)} vs {majority} on the rest. A machine's program changed mid-lot. End or change the lot, or fix the program."
        };
    }

    return Results.Ok(new
    {
        line = line.Config.LineName,
        lineId = line.Config.LineId,
        time = DateTime.Now,
        verify = new
        {
            active = line.Coordinator?.HasActiveSession ?? false,
            activeMode = line.Coordinator?.Snapshot().Mode,
            currentShift = line.Coordinator?.CurrentShift,
            currentShiftKey = line.Coordinator?.CurrentShiftKey,
            shiftScan = line.Coordinator?.LastShiftScan is { } ss
                ? new { at = ss.At, model = ss.Model, result = ss.Result, shiftKey = ss.ShiftKey } : (object?)null,
            lotEnd = line.Coordinator?.LastLotEnd is { } le
                ? new { at = le.At, model = le.Model, result = le.Result, shiftKey = le.ShiftKey } : (object?)null,
            modelChange = line.Coordinator?.LastModelChange is { } mc
                ? new { at = mc.At, model = mc.Model, result = mc.Result, shiftKey = mc.ShiftKey } : (object?)null
        },
        machines,
        programWarn,
        nonCanon = line.Coordinator?.NonCanon ?? false
    });
});

// Bring a machine online: re-open its port + re-enable real-time reporting.
app.MapPost("/api/machine/reconnect", (LineService line, MachineReq req) =>
    Results.Ok(new { message = line.Reconnect(req.Machine) }));

// Ask every machine which production program (.PWB) is loaded (C3P). Read replies from /api/status.
app.MapPost("/api/programs/query", (LineService line) =>
{
    line.QueryPrograms();
    return Results.Ok(new { message = "C3P sent to all machines — program names appear in /api/status within a second." });
});

// Serial COM test: per-machine port/link diagnostic with a plain PASS/FAIL verdict, so a supervisor can check
// cabling/COM health on the floor at a glance. Read-only — same live signals as /api/status. A machine with no
// serial port (JUKI manual-trigger) is N/A; a supervisor-skipped machine is excluded from PASS/FAIL. STALE = the
// port is open and the machine last answered, but no serial message has arrived in over 10 minutes.
app.MapGet("/api/serial/test", (LineService line) =>
{
    var now = DateTime.Now;
    var machines = line.Listeners.Select(l =>
    {
        var rx = line.RxStats(l.Channel.Machine);
        bool skipped = line.Coordinator?.IsMachineSkipped(l.Channel.Machine) ?? false;
        bool manual  = string.IsNullOrWhiteSpace(l.Port);                 // JUKI manual machine: no serial port
        double? ageSec = rx.At == default ? (double?)null : (now - rx.At).TotalSeconds;
        string verdict =
            manual                          ? "n/a (manual, no serial)" :
            skipped                         ? "skipped (supervisor)"    :
            !l.IsOpen                       ? "FAIL — port not open"    :
            !l.Channel.IsOnline             ? "FAIL — no answer"        :
            (ageSec is double a && a > 600) ? $"STALE — {(int)(a / 60)}m since last msg" :
                                              "PASS";
        return new
        {
            machine = l.Channel.Machine,
            port = l.Port,
            portOpen = l.IsOpen,
            online = l.Channel.IsOnline,
            messages = rx.Count,
            lastMessageAt = rx.At == default ? (DateTime?)null : rx.At,
            ageSeconds = ageSec is double s ? Math.Round(s) : (double?)null,
            boardsPerHour = l.Channel.BoardRate.BoardsPerHour is double b ? Math.Round(b, 1) : (double?)null,
            skipped,
            manual,
            verdict,
            pass = verdict == "PASS"
        };
    }).ToList();
    var failing = machines.Where(m => !m.pass && !m.manual && !m.skipped).Select(m => $"M{m.machine}·{m.port}").ToArray();
    return Results.Ok(new { line = line.Config.LineName, generatedAt = now, allPass = failing.Length == 0, failing, machines });
});

// Line HEALTH: one rollup of the six signals that say a line is running well, each OK / WARN / DOWN, with the
// line status = the worst active signal. Consolidates existing live signals (serial, producing, program, check
// currency, downtime) plus the DB heartbeat — read-only, no new state beyond DbHealthService.
app.MapGet("/api/health", (LineService line) =>
{
    var now = DateTime.Now;
    var co = line.Coordinator;

    // 1) Serial link — per machine, rolled up. A manual/skipped machine is N/A (not counted).
    var serialM = line.Listeners.Select(l =>
    {
        var rx = line.RxStats(l.Channel.Machine);
        bool skipped = co?.IsMachineSkipped(l.Channel.Machine) ?? false;
        bool manual = string.IsNullOrWhiteSpace(l.Port);
        double? age = rx.At == default ? (double?)null : (now - rx.At).TotalSeconds;
        string s = manual || skipped ? "na"
            : !l.IsOpen ? "down"
            : !l.Channel.IsOnline ? "down"
            : (age is double a && a > 600) ? "warn"
            : "ok";
        return new { machine = l.Channel.Machine, state = s, port = l.Port };
    }).ToList();
    string serial = serialM.Any(m => m.state == "down") ? "down" : serialM.Any(m => m.state == "warn") ? "warn" : "ok";

    // NON-CANON park: PVS isn't tracking this model, so every Canon-only signal is N/A. Report a neutral "parked"
    // verdict (NOT a fault) with serial still shown, so the line reads as intentional, not broken.
    if (co?.NonCanon == true)
        return Results.Ok(new
        {
            line = line.Config.LineName,
            lineId = line.Config.LineId,
            time = now,
            status = "parked",
            nonCanon = true,
            signals = new
            {
                serial = new { state = serial, machines = serialM },
                producing = new { state = "na" },
                program = new { state = "na", detail = "non-Canon model — PVS tracking paused" },
                check = new { state = "na" },
                db = new { state = line.DbHealth is { Enabled: true } ? (line.DbHealth.Ok == true ? "ok" : "warn") : "na" },
                downtime = new { state = "na" },
                recording = new { state = "na" }
            }
        });

    // 2) Producing — from the stop tracker. Not-yet-run-today = idle (not a fault).
    var (producing, stopped, sawBoard, openReason) = line.StopTracking?.HealthInputs() ?? (false, false, false, null);
    string prod = producing ? "ok" : !sawBoard ? "idle" : "warn";

    // 3) Program/model consistency — machines-disagree or pin mismatch = down; pin unverified = warn.
    var runningModels = line.Listeners
        .Where(l => l.Channel.IsOnline && !(co?.IsMachineSkipped(l.Channel.Machine) ?? false))
        .Select(l => ModelOfProgram(l.Channel.ProgramName)).Where(m => m.Length > 0).Distinct().ToList();
    bool disagree = runningModels.Count > 1;
    string mv = co?.ModelVerify ?? "unknown";
    string prog = disagree || mv == "mismatch" ? "down" : mv == "unverified" ? "warn" : "ok";

    // 4) Check currency — a shift check completed THIS shift.
    string curKey = co?.CurrentShiftKey ?? "";
    bool checkDone = co?.LastShiftScan is { } ss && ss.ShiftKey == curKey;
    string check = checkDone ? "ok" : "warn";

    // 5) DB connectivity — the heartbeat (N/A when no SQL login configured).
    string db = line.DbHealth is not { Enabled: true } ? "na"
        : line.DbHealth.Ok switch { true => "ok", false => "down", _ => "warn" };

    // 6) Downtime load — line up now?
    var (_, dtTotal, dtUp) = line.Downtime?.Snapshot()
        ?? ((IReadOnlyList<Pvs.Core.Runtime.DownSpan>)Array.Empty<Pvs.Core.Runtime.DownSpan>(), TimeSpan.Zero, false);
    string dt = dtUp ? "ok" : "warn";

    // 7) Production RECORDING — is the DPC write actually landing? The db signal above is only a SELECT-1 ping and
    // cannot see write failures. DOWN if a write threw recently; WARN if producing but the oldest unwritten bucket
    // is > 15 min old (a healthy flush commits every 5 min, so an aged backlog means writes aren't landing).
    string rec = "na"; long recPending = 0; double? recOldestMin = null;
    if (co is not null && line.Config.WriteProductionCount)
    {
        var (pp, oldest) = co.PendingProduction();
        recPending = pp;
        recOldestMin = oldest.HasValue ? Math.Round((now - oldest.Value).TotalMinutes, 1) : (double?)null;
        bool recentErr = co.LastProductionWriteError is { } e && (now - e) < TimeSpan.FromMinutes(10);
        bool stalled = recOldestMin is double m && m > 15;
        rec = recentErr ? "down" : stalled && producing ? "warn" : "ok";
    }

    // 8) Boards-per-panel factor known for the tracked model? An unknown model silently counts 1 board/panel
    //    (decrement, lot progress, DPC all off by the true factor). WARN so it is seen, not discovered at lot end.
    string? modelName = co?.ModelName;
    bool ppKnown = modelName is null || line.Config.HasPanelBoards(modelName);
    string ppSig = modelName is null ? "na" : ppKnown ? "ok" : "warn";

    var sigs = new[] { serial, prod, prog, check, db, dt, rec, ppSig };
    string status = sigs.Contains("down") ? "down" : sigs.Contains("warn") ? "warn" : "ok";

    return Results.Ok(new
    {
        line = line.Config.LineName,
        lineId = line.Config.LineId,
        time = now,
        status,
        signals = new
        {
            serial = new { state = serial, machines = serialM },
            producing = new { state = prod, producing, stopped, openReason },
            program = new { state = prog, disagree, modelVerify = mv, detail = co?.ModelVerifyDetail },
            check = new { state = check, doneThisShift = checkDone },
            db = new { state = db, detail = line.DbHealth?.Snapshot() },
            downtime = new { state = dt, up = dtUp, minutesToday = Math.Round(dtTotal.TotalMinutes, 1) },
            recording = new { state = rec, pendingPanels = recPending, oldestPendingMin = recOldestMin,
                              lastWriteAt = co?.LastProductionWriteAt, lastWriteError = co?.LastProductionWriteError },
            perPanel = new { state = ppSig, model = modelName, boardsPerPanel = modelName is null ? (int?)null : line.Config.PanelBoardsFor(modelName),
                             detail = ppKnown ? null : $"no panelBoards entry for {modelName} — counting 1 board per panel" }
        }
    });
});

// ---- Standby reel check ----
// The operator scans every spare reel physically on the rack; PVS confirms which are present and flags DB-believed
// spares that were never scanned as PHANTOMS (in StockOuts but not on the rack). "Believed standby" = reels issued
// to this line (ReelsAtLine) that are NOT currently loaded on a feeder. No DB write here — the only state is the
// in-memory scanned set; zeroing the phantoms is a separate, approved step.
static (IReadOnlyList<Pvs.Core.Data.IssuedReel> believed, HashSet<string> loaded) StandbyBelieved(LineService line)
{
    var loaded = new HashSet<string>(line.Reels.All().Select(r => (r.Uid ?? "").Trim()), StringComparer.OrdinalIgnoreCase);
    var atLine = line.Coordinator?.ReelsAtLine() ?? (IReadOnlyList<Pvs.Core.Data.IssuedReel>)Array.Empty<Pvs.Core.Data.IssuedReel>();
    var believed = atLine
        .Where(r => !loaded.Contains((r.Uid ?? "").Trim()))
        .GroupBy(r => (r.Uid ?? "").Trim())
        .Select(g => g.First())
        .ToList();
    return (believed, loaded);
}

app.MapGet("/api/standby/state", (LineService line, Pvs.LineApp.Runtime.StandbyScanState scan) =>
{
    var (believed, _) = StandbyBelieved(line);
    var scanned = new HashSet<string>(scan.All(), StringComparer.OrdinalIgnoreCase);
    var believedUids = new HashSet<string>(believed.Select(r => (r.Uid ?? "").Trim()), StringComparer.OrdinalIgnoreCase);

    var rows = believed
        .OrderBy(r => r.PartNumber).ThenBy(r => r.Uid)
        .Select(r => new { part = r.PartNumber, uid = r.Uid, qty = r.Qty, confirmed = scanned.Contains((r.Uid ?? "").Trim()) })
        .ToList();
    // scanned UIDs that are neither a believed standby nor currently loaded (issued elsewhere / >30 days / transferred in)
    var unexpected = scanned.Where(u => !believedUids.Contains(u)).OrderBy(u => u).ToList();

    int confirmed = rows.Count(r => r.confirmed);
    return Results.Ok(new
    {
        line = line.Config.LineName,
        startedAt = scan.StartedAt,
        believedCount = rows.Count,
        confirmed,
        pending = rows.Count - confirmed,   // still to scan while in progress; = phantom once the operator is done
        unexpectedCount = unexpected.Count,
        rows,
        unexpected
    });
});

// Record one scanned standby reel and classify it for the operator.
app.MapPost("/api/standby/scan", (LineService line, Pvs.LineApp.Runtime.StandbyScanState scan, ScanReq req) =>
{
    var uid = (req.Value ?? "").Trim();
    if (uid.Length == 0) return Results.Ok(new { ok = false, message = "empty scan" });
    scan.Add(uid);
    var loadedReel = line.Reels.All().FirstOrDefault(r => string.Equals((r.Uid ?? "").Trim(), uid, StringComparison.OrdinalIgnoreCase));
    var atLine = line.Coordinator?.ReelsAtLine() ?? (IReadOnlyList<Pvs.Core.Data.IssuedReel>)Array.Empty<Pvs.Core.Data.IssuedReel>();
    var believedReel = atLine.FirstOrDefault(r => string.Equals((r.Uid ?? "").Trim(), uid, StringComparison.OrdinalIgnoreCase));
    string kind, message;
    if (loadedReel is not null) { kind = "loaded"; message = $"⚠ {loadedReel.Part} is LOADED on M{loadedReel.Machine}·F{loadedReel.Feeder} — a running reel, not a standby."; }
    else if (believedReel is not null) { kind = "confirmed"; message = $"✓ {believedReel.PartNumber} confirmed on the rack ({believedReel.Qty} pcs)."; }
    else { kind = "unexpected"; message = $"? {uid} isn't a known standby for this line (issued elsewhere, >30 days, or transferred in) — recorded."; }
    return Results.Ok(new { ok = true, uid, kind, message });
});

app.MapPost("/api/standby/reset", (Pvs.LineApp.Runtime.StandbyScanState scan) => { scan.Reset(); return Results.Ok(new { message = "standby check reset" }); });

// Live parts-out event feed (newest first).
app.MapGet("/api/events", (LineService line) => Results.Ok(
    line.RecentEvents.Select(e => new
    {
        at = e.At,
        machine = e.Machine,
        feeder = e.Feeder,
        kind = e.Kind
    })));

// Combined top-N exhaust forecast.
app.MapGet("/api/forecast", (LineService line, int top) =>
{
    var monitor = line.Monitor;
    if (monitor is null) return Results.Ok(Array.Empty<object>());

    var schedule = line.Config.ToShiftSchedule();
    var now = DateTime.Now;
    // Night-shift toggle would come from operator state; default: day only (line 1 is day-mostly).
    bool nightRunning = false;
    Func<string, bool> running = name => name == "Day" || (name == "Night" && nightRunning);

    var rows = monitor.Forecast(top <= 0 ? 10 : top).Select(f => new
    {
        partNumber = f.PartNumber,
        remaining = f.Remaining,
        piecesPerHour = Math.Round(f.PiecesPerHour, 0),
        timeLeft = Pvs.Core.Shifts.ForecastWording.Duration(f.HoursToExhaust),
        runsOut = Pvs.Core.Shifts.ForecastWording.WallClock(now, f.HoursToExhaust, schedule, running),
        locations = f.Locations.Select(l => $"M{l.Machine}·{l.Feeder}")
    });
    return Results.Ok(rows);
});

// Per-feeder exhaust estimate (Machine / Feeder / Part + remaining qty + time-to-exhaust), soonest-first.
app.MapGet("/api/exhaust", (LineService line) =>
{
    var schedule = line.Config.ToShiftSchedule();
    var now = DateTime.Now;
    bool nightRunning = false;   // line 1 is day-mostly; night wall-clock stays conservative
    Func<string, bool> running = name => name == "Day" || (name == "Night" && nightRunning);

    // Lot context for the "lasts this lot" + material-coverage intelligence.
    var coord = line.Coordinator;
    var (lotRemainingBoards, lotEffTarget) = coord?.LotBoards() ?? (null, null);
    int perPanel = coord?.PerPanel ?? 1;
    bool lotTracked = lotRemainingBoards is int;
    var issued = coord?.LotIssued() ?? (IReadOnlyDictionary<string, int>)new Dictionary<string, int>();

    var raw = new List<(int machine, int feeder, string part, int remaining, int perBoard, double? hours)>();
    // Board rate per machine: a serial machine has its own; a manual (JUKI) machine has none, so use the LINE rate
    // (any serial machine's bph) — the same boards pass through it inline. Read feeders from the coordinator so
    // manual/JUKI machines are INCLUDED (line.Listeners is serial-only).
    var bphByMachine = line.Listeners.ToDictionary(l => l.Channel.Machine, l => l.Channel.BoardRate.BoardsPerHour);
    double? lineBph = bphByMachine.Values.FirstOrDefault(b => b is double d && d > 0);
    foreach (var (mach, f) in coord?.AllFeederStates() ?? new List<(int, Pvs.Core.Inventory.FeederState)>())
    {
        if (!f.IsTracked) continue;
        double? bph = (bphByMachine.TryGetValue(mach, out var mb) && mb is double) ? mb : lineBph;
        double? hours = (bph is double b && b > 0 && f.MountedPerBoard > 0)
            ? f.Remaining / (f.MountedPerBoard * b) : (double?)null;
        raw.Add((mach, f.Feeder, f.PartNumber, f.Remaining, f.MountedPerBoard, hours));
    }

    // Total per-cycle usage of each part (summed over the feeders carrying it), for the whole-lot material need.
    var perCycleByPart = raw.GroupBy(r => r.part)
        .ToDictionary(g => g.Key, g => g.Sum(x => x.perBoard), StringComparer.OrdinalIgnoreCase);
    int? cyclesWholeLot = (lotEffTarget is int et && perPanel > 0) ? et / perPanel : (int?)null;

    // Standby/spare reels per part: reels issued (or otherwise at this line) but NOT currently loaded on a feeder.
    // A feeder whose loaded reel won't last the lot is only a real request if there is NO spare of that part ready.
    var loadedUids = new HashSet<string>(line.Reels.All().Select(r => (r.Uid ?? "").Trim()), StringComparer.OrdinalIgnoreCase);
    var atLineReels = coord?.ReelsAtLine() ?? (IReadOnlyList<Pvs.Core.Data.IssuedReel>)Array.Empty<Pvs.Core.Data.IssuedReel>();
    var lotIssuedReels = coord?.LotIssuedReels() ?? (IReadOnlyList<Pvs.Core.Data.IssuedReel>)Array.Empty<Pvs.Core.Data.IssuedReel>();
    var spareSource = atLineReels.Count > 0 ? atLineReels : lotIssuedReels;
    var sparesByPart = spareSource
        .Where(r => !loadedUids.Contains((r.Uid ?? "").Trim()))
        .GroupBy(r => (r.PartNumber ?? "").Trim(), StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => (reels: g.Count(), qty: g.Sum(x => x.Qty)), StringComparer.OrdinalIgnoreCase);

    var ordered = raw
        .OrderBy(r => r.hours ?? double.PositiveInfinity)
        .ThenBy(r => r.machine).ThenBy(r => r.feeder)
        .Select(r =>
        {
            // Child boards THIS reel can still cover = (remaining / per-cycle usage) cycles × per-panel.
            int? boardsLeftOnReel = r.perBoard > 0 ? (int)Math.Floor((double)r.remaining / r.perBoard) * perPanel : (int?)null;
            bool lastsLot = lotTracked && boardsLeftOnReel is int ble && ble >= lotRemainingBoards;
            // Material coverage: pieces issued (StockOuts) for the lot vs pieces the WHOLE lot needs of this part.
            int issuedPart = issued.TryGetValue(r.part, out var iv) ? iv : 0;
            int? neededWholeLot = (cyclesWholeLot is int cwl && perCycleByPart.TryGetValue(r.part, out var pcu)) ? cwl * pcu : (int?)null;
            // Standby: is a spare reel of this part already staged at the line (ready to swap in)?
            int spareReels = sparesByPart.TryGetValue(r.part, out var sp) ? sp.reels : 0;
            int spareQty = spareReels > 0 ? sparesByPart[r.part].qty : 0;
            bool spareReady = spareReels > 0;
            // Orange only when the reel WON'T finish the lot (times the warning to when it's actually running low)
            // AND the total issued material can't cover the lot -> a request is needed (lead time).
            bool underIssued = lotTracked && !lastsLot && neededWholeLot is int nwl && issuedPart < nwl;
            // A change is "covered" if a spare is staged; it only needs a fresh request when NO spare is ready.
            bool needsRequest = !lastsLot && !spareReady && underIssued;
            int? shortfallBoards = (underIssued && neededWholeLot is int nwl2 && perCycleByPart.TryGetValue(r.part, out var pcu2) && pcu2 > 0)
                ? (int)Math.Round((double)(nwl2 - issuedPart) / pcu2 * perPanel) : (int?)null;
            return new
            {
                machine = r.machine,
                feeder = r.feeder,
                part = r.part,
                remaining = r.remaining,
                perBoard = r.perBoard,
                minutes = r.hours is double h ? Math.Round(h * 60) : (double?)null,
                timeLeft = r.hours is double h2 ? Pvs.Core.Shifts.ForecastWording.Duration(h2) : "—",
                runsOut = r.hours is double h3 ? Pvs.Core.Shifts.ForecastWording.WallClock(now, h3, schedule, running) : "rate unknown",
                boardsLeftOnReel,          // child boards this reel still covers
                lastsLot,                  // reel will finish the current lot -> no change needed this lot
                spareReady,                // a standby reel of this part is staged at the line
                spareReels,                // how many standby reels are ready
                spareQty,                  // total pieces on those standby reels
                needsRequest,              // won't last, no spare staged, under-issued -> actually request material
                issued = issuedPart,       // pieces issued for the lot (this part)
                neededLot = neededWholeLot,// pieces the whole lot needs (this part)
                underIssued,               // issued reels won't cover the lot -> request more (lead time)
                shortfallBoards            // ~boards the issued material falls short by
            };
        })
        .ToList();

    // reels that will run out BEFORE lot end (known coverage, doesn't last) -> a reel change is due this lot
    int? changesBeforeLotEnd = lotTracked
        ? ordered.Count(r => r.boardsLeftOnReel is int b && b < lotRemainingBoards)
        : (int?)null;
    // of those changes, how many are already covered by a staged spare vs actually need material requested
    int? changesCovered = lotTracked ? ordered.Count(r => !r.lastsLot && r.spareReady) : (int?)null;
    int? requestsNeeded = lotTracked ? ordered.Count(r => r.needsRequest) : (int?)null;
    var underIssuedParts = ordered.Where(r => r.underIssued).Select(r => r.part).Distinct().ToList();
    var requestParts = ordered.Where(r => r.needsRequest).Select(r => r.part).Distinct().ToList();

    return Results.Ok(new
    {
        generatedAt = now,
        count = ordered.Count,
        lotNo = coord?.CurrentLotNo,
        lotTracked,
        boardsToLotEnd = lotRemainingBoards,
        changesBeforeLotEnd,       // reels that won't last the lot (a swap is due)
        changesCovered,            // ...of those, how many have a spare staged (calm)
        requestsNeeded,            // ...how many actually need material requested (no spare + under-issued)
        underIssuedParts,
        requestParts,
        rows = ordered
    });
});

// Reels ISSUED (StockOuts) for this line's current lot but NOT yet loaded on a machine — staged / waiting to go on.
// The exhaust forecast only shows loaded reels; this shows the ones on the shelf so the operator/planner can see
// what's ready vs what still has to be requested. issued − currently-loaded UIDs.
app.MapGet("/api/staged", (LineService line) =>
{
    var coord = line.Coordinator;
    // EVERY reel at this line, not just the current lot's. A reel issued under a previous lot is still on the
    // rack; scoping by lot showed "nothing staged" for a feeder an hour from empty while two full reels of that
    // part sat waiting (Line 1 F120, 2026-08-07). Falls back to the lot-scoped list if the wider query is empty.
    var atLine = coord?.ReelsAtLine() ?? (IReadOnlyList<Pvs.Core.Data.IssuedReel>)Array.Empty<Pvs.Core.Data.IssuedReel>();
    var lotIssued = coord?.LotIssuedReels() ?? (IReadOnlyList<Pvs.Core.Data.IssuedReel>)Array.Empty<Pvs.Core.Data.IssuedReel>();
    var issued = atLine.Count > 0 ? atLine : lotIssued;

    var loadedUids = new HashSet<string>(line.Reels.All().Select(r => (r.Uid ?? "").Trim()), StringComparer.OrdinalIgnoreCase);
    var lotUids = new HashSet<string>(lotIssued.Select(r => (r.Uid ?? "").Trim()), StringComparer.OrdinalIgnoreCase);
    var staged = issued.Where(r => !loadedUids.Contains((r.Uid ?? "").Trim())).ToList();

    var parts = staged.GroupBy(r => r.PartNumber, StringComparer.OrdinalIgnoreCase)
        .Select(g => new
        {
            part = g.Key,
            reels = g.Count(),
            qty = g.Sum(x => x.Qty),
            // forThisLot=false means the reel was drawn against an earlier lot — still on the rack, still usable
            uids = g.OrderByDescending(x => x.Qty)
                    .Select(x => new { uid = x.Uid, qty = x.Qty, forThisLot = lotUids.Contains((x.Uid ?? "").Trim()) })
                    .ToList()
        })
        .OrderByDescending(p => p.qty).ThenBy(p => p.part).ToList();

    return Results.Ok(new
    {
        lotNo = coord?.CurrentLotNo,
        scope = atLine.Count > 0 ? "all reels at this line (any lot)" : "current lot only (wider query unavailable)",
        issuedReels = issued.Count,
        loadedReels = loadedUids.Count,
        stagedReels = staged.Count,
        stagedQty = staged.Sum(r => r.Qty),
        stagedFromOtherLots = staged.Count(r => !lotUids.Contains((r.Uid ?? "").Trim())),
        parts
    });
});

// The machine's OWN completed-PWB counter, read live via the Sony C1M production report (authoritative;
// survives PVS being off). POST triggers a fresh read (optionally for one ?machine=N), waits for the serial
// reports to stream back, and returns the counts. Read-only on the machine (C1M000 does NOT clear).
// ?pwb=<name>  request the summary for one PWB file (name WITHOUT extension); ?pwb=auto uses each
//              machine's own loaded program name. Omit for the Entire Machine Status.
// ?waitMs=N    how long to let the report stream back (default 10s).
// ?timeoutSec=N  channel collection window (default 8s — enough for the early PC count). The machine's OWN
//              Cycle Time (ET) and PWB-Waiting Time (PT) fields are near the END of the ~30s report, so to
//              read those pass ?pwb=auto&timeoutSec=45&waitMs=50000.
app.MapPost("/api/machinecount", async (LineService line, int? machine, string? pwb, int? waitMs, int? timeoutSec) =>
{
    var now = DateTime.Now;
    var targets = line.Listeners.Where(l => machine is null || l.Channel.Machine == machine).ToList();
    foreach (var l in targets)
    {
        var name = pwb;
        if (string.Equals(pwb, "auto", StringComparison.OrdinalIgnoreCase))
        {
            // Strip the cell-specific extension (".PW1".."PW4"/".PWB") — the manual wants the name only.
            var prog = l.Channel.ProgramName;
            var dot = prog?.LastIndexOf('.') ?? -1;
            name = dot > 0 ? prog![..dot] : prog;
        }
        l.Channel.RequestProductionCount(now, name, timeoutSec ?? 8);
    }
    await Task.Delay(waitMs ?? 10000);   // let the C1M report(s) stream back over serial
    return Results.Ok(new
    {
        readAt = DateTime.Now,
        machines = targets.Select(l => new
        {
            machine = l.Channel.Machine,
            completedPwbs = l.Channel.CompletedPwbs,
            at = l.Channel.CompletedPwbsAt == default ? (DateTime?)null : l.Channel.CompletedPwbsAt,
            // The machine's OWN reported per-lot Cycle Time (ET) and PWB-Waiting/starvation time (PT), seconds.
            cycleTimeSec = Pvs.Core.Serial.SonyProductionReport.Field(l.Channel.RawProductionReport, "ET"),
            pwbWaitSec = Pvs.Core.Serial.SonyProductionReport.Field(l.Channel.RawProductionReport, "PT"),
            online = l.Channel.IsOnline,
            sent = l.Channel.LastReportCommand,
            error = l.Channel.LastReportError,
            program = l.Channel.ProgramName,
            rawReport = l.Channel.RawProductionReport
        }).ToList()
    });
});

// C1Z per-feeder report: each machine's OWN pickup counts per supply location (attempted/successful pickups,
// miss/abnormal/recognition errors, parts-out times). Phase 1 returns the RAW report text so the field layout
// is confirmed against real hardware before a parser is committed. ?machine=N one machine; ?pwb=auto uses each
// machine's C3P program name (minus extension); ?waitMs default 65s (per-feeder reports are large).
app.MapPost("/api/supplyreport", async (LineService line, int? machine, string? pwb, int? waitMs) =>
{
    var now = DateTime.Now;
    var targets = line.Listeners.Where(l => machine is null || l.Channel.Machine == machine).ToList();
    foreach (var l in targets)
    {
        var name = pwb;
        if (string.Equals(pwb, "auto", StringComparison.OrdinalIgnoreCase))
        {
            var prog = l.Channel.ProgramName;
            var dot = prog?.LastIndexOf('.') ?? -1;
            name = dot > 0 ? prog![..dot] : prog;
        }
        l.Channel.RequestSupplyReport(now, name);
    }
    await Task.Delay(waitMs ?? 65000);   // let the (large) C1Z report stream back over serial
    return Results.Ok(new
    {
        readAt = DateTime.Now,
        machines = targets.Select(l => new
        {
            machine = l.Channel.Machine,
            online = l.Channel.IsOnline,
            sent = l.Channel.LastReportCommand,
            error = l.Channel.LastReportError,
            program = l.Channel.ProgramName,
            reportLength = l.Channel.RawSupplyReport?.Length ?? 0,
            report = l.Channel.RawSupplyReport,
            at = l.Channel.SupplyReportAt == default ? (DateTime?)null : l.Channel.SupplyReportAt
        }).ToList()
    });
});

// MANUAL send/receive console (diagnostic page) — the operator types a Sony command and sees the raw reply, so
// they can probe C1Z / C1M000P<name> / C3P by hand on a stopped line. READ-ONLY: the payload MUST be a report or
// query read (C1* production reports, C3* program queries). Anything else — real-time control (C5*), writes — is
// refused HERE so the console can never send a control command to a machine. No parsing, no count/feeder effects.
app.MapPost("/api/console/send", async (LineService line, int? machine, string? cmd, int? waitMs) =>
{
    var payload = (cmd ?? "").Trim();
    if (payload.Length < 2)
        return Results.BadRequest(new { error = "Empty command. Try C1Z000, C1M000P<name>, or C3P." });
    // Read-only guard: only C1 (production reports) and C3 (program queries) may leave PVS.
    bool readOnly = payload.StartsWith("C1", StringComparison.OrdinalIgnoreCase)
                 || payload.StartsWith("C3", StringComparison.OrdinalIgnoreCase);
    if (!readOnly)
        return Results.BadRequest(new { error = $"Refused: '{payload}' is not a read command. PVS is read-only — only C1* (reports) and C3* (queries) are allowed." });

    var m = machine ?? 1;
    var l = line.Listeners.FirstOrDefault(x => x.Channel.Machine == m);
    if (l is null) return Results.NotFound(new { error = $"Machine {m} not configured / not serial." });

    var now = DateTime.Now;
    l.Channel.RequestConsole(now, payload, timeoutSec: ((waitMs ?? 65000) / 1000.0) - 2);
    await Task.Delay(waitMs ?? 65000);   // let any D0 dump stream back
    var rx = line.RxStats(m);
    return Results.Ok(new
    {
        machine = m,
        online = l.Channel.IsOnline,
        sent = l.Channel.LastReportCommand,
        error = l.Channel.LastReportError,               // A4E00 / A5xx / A3 if the machine refused
        program = l.Channel.ProgramName,
        reportLength = l.Channel.RawConsoleReport?.Length ?? 0,
        report = l.Channel.RawConsoleReport,             // the raw D0 dump, verbatim
        at = l.Channel.ConsoleReportAt == default ? (DateTime?)null : l.Channel.ConsoleReportAt,
        lastRxPayload = rx.Last,
        lastRxAt = rx.At == default ? (DateTime?)null : rx.At
    });
});

// Per-feeder machine statistics: the machine's OWN C1Z "Summary by Supply Location" — attempted/successful
// pickups, missed/abnormal/recognition errors, parts-out stops and pickup rate PER FEEDER — parsed from the
// LAST captured report (fast; no serial wait). Each feeder is joined to the part loaded on it. Use
// POST /api/supplyreport?machine=N first to pull a fresh capture; this GET then serves the parsed result.
// Freshness is explicit via capturedAt so a stale cache is never mistaken for live.
app.MapGet("/api/feederstats", (LineService line, int? machine) =>
{
    var coord = line.Coordinator;
    var targets = line.Listeners.Where(l => machine is null || l.Channel.Machine == machine).ToList();
    var machines = targets.Select(l =>
    {
        int m = l.Channel.Machine;
        var parsed = Pvs.Core.Serial.SonySupplyReport.Parse(l.Channel.RawSupplyReport);
        var feeders = parsed.Feeders.Select(f =>
        {
            string? part = coord?.FeederStateOf(m, f.SupplyLocation)?.PartNumber;
            return new
            {
                feeder = f.SupplyLocation,
                part,
                attempted = f.Attempted,
                successful = f.Successful,
                missed = f.Missed,
                abnormal = f.Abnormal,
                recognition = f.Recognition,
                errors = f.Errors,
                partsOut = f.PartsOut,
                ratePct = Math.Round(f.RateHundredthsPct / 100.0, 2)
            };
        })
        // Show a feeder if it has a part loaded OR it recorded activity — hides the machine's many empty positions.
        .Where(x => x.part is not null || x.attempted > 0 || x.errors > 0 || x.partsOut > 0)
        .OrderBy(x => x.feeder)
        .ToList();
        return new
        {
            machine = m,
            online = l.Channel.IsOnline,
            program = l.Channel.ProgramName,
            capturedAt = l.Channel.SupplyReportAt == default ? (DateTime?)null : l.Channel.SupplyReportAt,
            windowStart = parsed.Start,
            windowEnd = parsed.End,
            error = l.Channel.LastReportError,
            feeders
        };
    }).ToList();
    return Results.Ok(new { model = coord?.Model?.Name, side = coord?.Side, lot = coord?.CurrentLotNo, machines });
});

// The machine's own PRODUCTION REPORT (C1M000P<program name>) parsed into the exact fields the machine shows on
// its "Machine Status" screen, so PVS can display it the same way. Pulls it live (retries a few times through the
// transient A4E00 machine-state blocks), then parses every 2-letter field. Counts as-is, PR as a %, time fields
// (seconds) formatted HhMmSs. Field order + labels follow SI-F manual Table 6-8.
app.MapPost("/api/machinereport", async (LineService line, int? machine, int? retries) =>
{
    string SecToHms(long s) => $"{s / 3600:00}h {(s % 3600) / 60:00}m {s % 60:00}s";
    // (code, label, kind) — kind: c=count, r=rate(1/100%), t=time(sec). Order = machine Machine-Status screen.
    var fields = new (string Code, string Label, char Kind)[]
    {
        ("PC","Completed PWB",'c'), ("VC","Attempted Pickups",'c'), ("TC","Successful Pickups",'c'),
        ("MC","Missed Pickup Errors",'c'), ("DC","Abnormal Pickup Errors",'c'), ("RC","Recognition Errors",'c'),
        ("PR","Successful Pickup Rate",'r'), ("SC","No. of Detected Bad Marks",'c'), ("NC","No. of Detected Adhesions",'c'),
        ("EP","Parts-Out Stops",'c'), ("MP","Mark Recog. Error Stops",'c'), ("TP","PWB Transport Error Stops",'c'),
        ("PP","Pickup Error Stops",'c'), ("BP","Machine Troubles",'c'),
        ("AT","Operation Time",'t'), ("MT","Mounting Time",'t'), ("NT","Mounting Idle Time",'t'), ("WT","PWB Waiting Time",'t'),
        ("DT","Stopped Time",'t'), ("ET","Parts-Out Waiting Time",'t'), ("GT","Parts Refill Time",'t'),
        ("BT","Recovery Waiting Time",'t'), ("ST","Normal Stopped Time",'t'), ("PT","Power ON Time",'t'),
    };
    var targets = line.Listeners.Where(l => machine is null || l.Channel.Machine == machine).ToList();
    int tries = Math.Clamp(retries ?? 4, 1, 8);
    foreach (var l in targets)
    {
        for (int a = 0; a < tries; a++)
        {
            var at = DateTime.Now;
            l.Channel.RequestProductionCount(at, MachineReportName(l.Channel.ProgramName), timeoutSec: 45);
            await Task.Delay(48000);
            if (l.Channel.RawProductionReport is { Length: > 0 } && l.Channel.CompletedPwbsAt >= at) break;   // landed
            if (l.Channel.LastReportError is null) break;   // answered (even if empty) — stop
        }
    }
    string? Stamp(string raw, string c)
    {
        var m = System.Text.RegularExpressions.Regex.Match(raw, @"(?<![A-Za-z])" + c + @"(\d{10})");
        if (!m.Success) return null; var s = m.Groups[1].Value;
        return $"20{s[..2]}-{s[2..4]}-{s[4..6]} {s[6..8]}:{s[8..10]}";
    }
    string curLot = line.Coordinator?.CurrentLotNo ?? "";
    var machines = targets.Select(l =>
    {
        string raw = l.Channel.RawProductionReport ?? "";
        // SUM BY LOT: this lot's numbers (current C1M reading minus the lot-start baseline), NOT the machine's
        // lifetime accumulation. The reconciler holds/persists the baseline and re-baselines on a new lot.
        var perLot = !string.IsNullOrEmpty(raw) && line.Reconciler is not null
            ? line.Reconciler.PerLotFields(l.Channel.Machine, curLot, raw)
            : new System.Collections.Generic.Dictionary<string, long>();
        long GL(string c) => perLot.TryGetValue(c, out var v) ? v : 0;
        bool has = perLot.Count > 0;
        var rows = fields.Select(f =>
        {
            string disp;
            if (!has) disp = "—";
            else if (f.Kind == 'r')
            {
                long vc = GL("VC");
                disp = vc > 0 ? $"{(vc - GL("MC") - GL("DC") - GL("RC")) * 100.0 / vc:0.00}%" : "—";   // rate recomputed for THIS lot
            }
            else disp = f.Kind == 't' ? SecToHms(GL(f.Code)) : GL(f.Code).ToString("#,0");
            return new { code = f.Code, label = f.Label, value = disp, kind = f.Kind.ToString() };
        }).ToList();
        return new
        {
            machine = l.Channel.Machine,
            program = l.Channel.ProgramName,
            error = l.Channel.LastReportError,
            capturedAt = l.Channel.CompletedPwbsAt == default ? (DateTime?)null : l.Channel.CompletedPwbsAt,
            windowStart = string.IsNullOrEmpty(raw) ? null : Stamp(raw, "SD"),
            windowEnd = string.IsNullOrEmpty(raw) ? null : Stamp(raw, "ED"),
            boardsProduced = GL("PC"),
            attrition = GL("MC") + GL("DC"),
            rows
        };
    }).ToList();
    return Results.Ok(new { lot = curLot, machines });
});

// Board-count reconciliation: PVS's live count vs each machine's OWN counter (C1M). The two fail in
// opposite ways — PVS misses boards while blind; the machine's counter is reset by the operator at every
// lot end (SOP) — so comparing them catches both. Observe-and-record only; nothing is auto-corrected.
app.MapGet("/api/reconcile", (LineService line) =>
{
    var svc = line.Reconciler;
    if (svc is null) return Results.Ok(new { message = "reconciler not ready", runs = Array.Empty<object>() });
    return Results.Ok(new { intervalMinutes = svc.IntervalMinutes, running = svc.IsRunning, runs = svc.Recent });
});

// Run a pass NOW (lot start/end, shift change, after a restart or a machine coming back online).
// Takes ~45s: a C1M report streams for ~30s at 9600 baud.
app.MapPost("/api/reconcile/run", async (LineService line, string? trigger) =>
{
    if (line.Reconciler is null) return Results.Ok(new { message = "reconciler not ready" });
    var run = await line.Reconciler.RunAsync(string.IsNullOrWhiteSpace(trigger) ? "manual" : trigger!);
    return Results.Ok(run);
});

// Manually re-baseline the live inventory from the confirmed reels (feeds the exhaust forecast).
app.MapPost("/api/exhaust/refresh", async (LineService line) =>
{
    if (line.Coordinator is null) return Results.Ok(new { message = "coordinator not ready" });
    await line.Coordinator.RefreshInventoryAsync();
    return Results.Ok(new { message = "inventory baselined" });
});

// Record live per-feeder remaining to the local remaining.json on the line PC (StockOuts write-back is off).
app.MapPost("/api/inventory/record", async (LineService line) =>
{
    if (line.Coordinator is null) return Results.Ok(new { message = "coordinator not ready", recorded = 0 });
    int n = await line.Coordinator.RecordRemainingAsync();
    return Results.Ok(new { message = $"recorded {n} reels locally", recorded = n });
});

// Mirror live per-feeder remaining balances to StockOuts.Quantity in the DB (when SyncStockOuts is enabled).
// Always records locally first. On-demand trigger for the same work the 5-min timer does.
app.MapPost("/api/inventory/sync", async (LineService line) =>
{
    if (line.Coordinator is null) return Results.Ok(new { message = "coordinator not ready", written = 0 });
    await line.Coordinator.RecordRemainingAsync();
    if (!line.Config.SyncStockOuts)
        return Results.Ok(new { message = "recorded locally; StockOuts write-back is disabled (set SyncStockOuts=true)", written = 0 });
    int n = await line.Coordinator.SyncRemainingToStockOutsAsync();
    return Results.Ok(new { message = $"wrote {n} reel balances to StockOuts", written = n });
});

// Current model's feeders grouped by machine — for the manual part-change picker.
app.MapGet("/api/feeders", (LineService line) =>
{
    var list = line.Coordinator?.FeederList() ?? Array.Empty<(int Machine, int Feeder, string Part)>();
    var grouped = list.GroupBy(x => x.Machine).OrderBy(g => g.Key)
        .Select(g => new { machine = g.Key, feeders = g.OrderBy(x => x.Feeder).Select(x => new { feeder = x.Feeder, part = x.Part }) });
    return Results.Ok(grouped);
});

// Manual feeder-list override (pen-drive CSV, for a breakdown reshuffle). Supervisor-gated; per-lot; no DB write.
app.MapGet("/api/feeders/manual/status", (LineService line) =>
    Results.Ok(line.Coordinator?.ManualFeederState() ?? (object)new { active = false, machines = Array.Empty<object>(), loadedTotal = 0 }));
app.MapPost("/api/feeders/manual", async (LineService line, IReelPartRepository repo, ManualFeederReq req) =>
{
    var badge = await repo.FindBadgeAsync(req.Badge ?? "") ?? new Pvs.Core.People.Badge("?", req.Badge ?? "", "");
    return Results.Ok(new { message = await line.Coordinator!.LoadManualFeedersAsync(req.Machine, req.Csv ?? "", badge, req.Force) });
});
// Supervisor takes a machine out of this run (bypassed cell / mounter down) or puts it back — its feeders leave
// or rejoin the checklist. Same badge gate as the feeder-list load.
app.MapPost("/api/feeders/manual/skip", async (LineService line, IReelPartRepository repo, MachineSkipReq req) =>
{
    var badge = await repo.FindBadgeAsync(req.Badge ?? "") ?? new Pvs.Core.People.Badge("?", req.Badge ?? "", "");
    return Results.Ok(new { message = await line.Coordinator!.SetMachineSkippedAsync(req.Machine, req.Skip, badge) });
});
app.MapPost("/api/feeders/manual/clear", async (LineService line, IReelPartRepository repo, BadgeReq req) =>
{
    var badge = await repo.FindBadgeAsync(req.Badge ?? "") ?? new Pvs.Core.People.Badge("?", req.Badge ?? "", "");
    return Results.Ok(new { message = await line.Coordinator!.ClearManualFeedersAsync(badge) });
});

// Manually start a Mode B parts-change for a chosen machine + feeder (then the normal scan flow follows).
app.MapPost("/api/session/partschange", (LineService line, FeederReq req) =>
    Results.Ok(new { message = line.Coordinator!.StartManualPartsChange(req.Machine, req.Feeder) }));

// Daily report: model runs, qty produced, line downtime, and per-machine feeder usage vs production.
app.MapGet("/api/report/daily", async (LineService line, IReelPartRepository repo, string? date) =>
{
    string day = string.IsNullOrWhiteSpace(date) ? DateTime.Now.ToString("yyyy-MM-dd") : date!.Trim();
    string lineNo = line.Config.LineId.ToString();

    var runs = await repo.GetDailyProductionAsync(lineNo, day);

    // Aggregate boards per (model, side) so we can weight the BOM feeder usage.
    var byModel = runs.GroupBy(r => (r.Model, r.Side))
        .Select(g => new { g.Key.Model, g.Key.Side, Boards = g.Sum(x => x.Quantity), Excess = g.Sum(x => x.ExcessQuantity) })
        .Where(m => !string.IsNullOrWhiteSpace(m.Model))
        .ToList();

    // Feeder usage: expected placements = boards produced x per-board count (ProductBOM), summed across
    // every model/side that ran today. Keyed by machine + supply position + part.
    // NOTE: DailyProductionCount.Quantity is a CHILD-BOARD count (verified: a lot sums to its board target
    // per side, and the per-run counts are too high to be panels given the ~50s machine cycle). So usage is
    // per-board × boards directly — do NOT multiply by the panel factor (that over-counted by ×panel before).
    var feeders = new Dictionary<(int Machine, string Pos, string Part), (int PerBoard, long Expected)>();
    foreach (var m in byModel)
    {
        if (m.Boards <= 0) continue;
        var bom = await repo.GetBomUsageAsync(m.Model, m.Side, line.Config.LineId);
        foreach (var f in bom)
        {
            var key = (f.Machine, f.SupplyPosition, f.PartNumber);
            int effPerBoard = f.PerBoard;                    // placements per child board (Quantity is boards)
            long add = (long)effPerBoard * m.Boards;
            feeders[key] = feeders.TryGetValue(key, out var cur)
                ? (effPerBoard, cur.Expected + add)
                : (effPerBoard, add);
        }
    }

    var machines = feeders
        .GroupBy(kv => kv.Key.Machine)
        .OrderBy(g => g.Key)
        .Select(g => new
        {
            machine = g.Key,
            feeders = g.OrderBy(kv => kv.Key.Pos).Select(kv => new
            {
                position = kv.Key.Pos,
                part = kv.Key.Part,
                perBoard = kv.Value.PerBoard,
                expectedUsed = kv.Value.Expected
            })
        });

    // Downtime + stop capture for the REQUESTED day (calendar day window), not whatever is in memory today (audit M5).
    var dayStart = DateTime.TryParse(day, out var dd) ? dd.Date : DateTime.Today;
    var (spans, total, up) = line.Downtime?.Snapshot(dayStart, dayStart.AddDays(1)) ?? (Array.Empty<Pvs.Core.Runtime.DownSpan>(), TimeSpan.Zero, false);

    return Results.Ok(new
    {
        date = day,
        line = line.Config.LineName,
        lineId = line.Config.LineId,
        generatedAt = DateTime.Now,
        currentShift = line.Coordinator?.CurrentShift,
        runs = runs.Select(r => new
        {
            model = r.Model, side = r.Side, lotNo = r.LotNo, shift = r.Shift,
            startTime = r.StartTime, endTime = r.EndTime, quantity = r.Quantity, excess = r.ExcessQuantity
        }),
        totalBoards = runs.Sum(r => r.Quantity),
        totalExcess = runs.Sum(r => r.ExcessQuantity),
        models = byModel.Select(m => new { model = m.Model, side = m.Side, boards = m.Boards, excess = m.Excess }),
        downtime = new
        {
            up,
            totalMinutes = Math.Round(total.TotalMinutes, 1),
            spans = spans.Select(s => new
            {
                start = s.Start,
                end = s.End,
                minutes = Math.Round(s.Duration(DateTime.Now).TotalMinutes, 1)
            })
        },
        // Reason-tagged stops (under each title) + per-cell parts-exhaust recovery.
        stopCapture = line.StopTracking?.Report(dayStart, dayStart.AddDays(1)),
        machines
    });
});

// Manual trigger for the shift report email (to test without waiting for 07:30/19:30). Optional ?key=yyyy-MM-dd|Shift
// picks a specific ended shift; otherwise the one just before the current boundary. Sends to this line's PICs.
app.MapPost("/api/report/shift-email", async (LineService line, IReelPartRepository repo, Pvs.LineApp.Runtime.EmailSender email, string? key) =>
{
    if (line.ShiftUptime is null) return Results.Ok(new { ok = false, message = "uptime service not ready" });
    var now = DateTime.Now;
    var shifts = line.Config.ToShiftSchedule();
    string endedKey;
    if (!string.IsNullOrWhiteSpace(key)) endedKey = key!;
    else { var cur = shifts.ShiftAt(now); var cs = now.Date + cur.Start.ToTimeSpan(); if (cs > now) cs = cs.AddDays(-1); endedKey = shifts.ShiftKey(cs.AddSeconds(-1)); }
    line.ShiftUptime.EnsureArchivedThrough(now);
    var summary = line.ShiftUptime.GetSummary(endedKey) ?? line.ShiftUptime.Current();
    var report = await Pvs.LineApp.Runtime.ShiftReport.BuildAsync(line.Config, repo, summary);
    var ok = await email.SendToPicsAsync(Pvs.LineApp.Runtime.ShiftReport.Subject(report), Pvs.LineApp.Runtime.ShiftReport.Body(report));
    return Results.Ok(new { ok, key = endedKey, subject = Pvs.LineApp.Runtime.ShiftReport.Subject(report), boards = report.TotalBoards, lots = report.Lots.Count });
});

// Runs the forward-looking parts-shortage check now instead of waiting for the daily time. Defaults to a DRY
// RUN: pass ?send=true to actually email. The response carries the exact subject and body either way, so the
// report can be reviewed before anyone is on the distribution list.
app.MapPost("/api/shortage/run", async (Pvs.LineApp.Runtime.ShortageMonitorService monitor, bool? send) =>
{
    var r = await monitor.RunAsync(send == true);
    return Results.Ok(new
    {
        ok = true,
        sent = r.Sent,
        message = r.Message,
        shortages = r.Forecast.Findings.Count,
        urgent = r.Forecast.Urgent.Count,
        lots = r.Forecast.LotsConsidered,
        parts = r.Forecast.PartsConsidered,
        trackedPercent = r.Forecast.Coverage.TrackedPercent,
        untrackedLines = r.Forecast.Coverage.UntrackedLines,
        subject = r.Subject,
        body = r.Body,
    });
});

// What the shortage check currently sees, without touching alert state or sending anything.
app.MapGet("/api/shortage", async (Pvs.LineApp.Runtime.ShortageMonitorService monitor) =>
{
    var r = await monitor.RunAsync(send: false);
    return Results.Ok(new
    {
        generated = r.Forecast.GeneratedAt,
        lots = r.Forecast.LotsConsidered,
        alreadyBuilt = r.Forecast.LotsAlreadyBuilt,
        unknownSize = r.Forecast.LotsUnknownSize,
        parts = r.Forecast.PartsConsidered,
        modelsWithoutBom = r.Forecast.ModelsWithoutBom,
        trackedPercent = r.Forecast.Coverage.TrackedPercent,
        untrackedLines = r.Forecast.Coverage.UntrackedLines,
        findings = r.Forecast.Findings.Select(f => new
        {
            f.PartNumber, f.LotNo, f.Model, f.DeliveryDate, f.Available,
            f.DemandThroughLot, f.ShortBy, f.LotBoards, f.QtyPerBoard,
            f.DaysToDelivery, f.Urgent, value = f.ShortValue,
        }),
    });
});

// ---- verification API (Stage 2) ----

app.MapGet("/api/models", async (LineService line) =>
{
    var products = await line.Coordinator!.GetModelsAsync();
    return Results.Ok(products.Select(p => new { p.ProductId, p.Name }));
});

// The model currently loaded (auto-detected from the running lot, unless a supervisor pinned it).
app.MapGet("/api/currentmodel", (LineService line) => Results.Ok(new
{
    model = line.Coordinator?.Model?.Name,
    productId = line.Coordinator?.Model?.ProductId,
    side = line.Coordinator?.Side,
    auto = line.Coordinator?.AutoModel ?? false,
    manual = line.Coordinator?.ManualModel is not null,
    verify = line.Coordinator?.ModelVerify,
    verifyDetail = line.Coordinator?.ModelVerifyDetail,
    lotNo = line.Coordinator?.CurrentLotNo,
    nonCanon = line.Coordinator?.NonCanon ?? false
}));

// Internal re-pull of a model's feeder map (used by "Refresh from DB"); does NOT pin a manual override.
app.MapPost("/api/model", async (LineService line, ModelReq req) =>
{
    // "Refresh from DB": re-read the feeder list, then re-baseline so the live inventory follows an amended BOM
    // immediately (audit H3 — it used to keep the old feeders until the next baseline).
    var msg = await line.Coordinator!.SelectModelAsync(req.ProductId, req.Side);
    try { await line.Coordinator.RefreshInventoryAsync(); } catch { /* baseline failure must not fail the select */ }
    return Results.Ok(new { message = msg });
});

// Supervisor pins the running model from the dropdown (badge-gated); PVS verifies it against the machine.
app.MapPost("/api/model/manual", async (LineService line, ModelManualReq req) =>
    Results.Ok(new { message = await line.Coordinator!.SelectModelManualAsync(req.ProductId, req.Side, req.Badge) }));
// Supervisor releases the pin and returns the line to auto-detect (badge-gated).
app.MapPost("/api/model/auto", async (LineService line, BadgeReq req) =>
    Results.Ok(new { message = await line.Coordinator!.ClearManualModelAsync(req.Badge) }));
// Supervisor parks the line as running a NON-CANON model (badge-gated): stands down Canon tracking, keeps the
// machine's own C1Z pickup data by feeder position. Release it with /api/model/auto.
app.MapPost("/api/model/noncanon", async (LineService line, BadgeReq req) =>
    Results.Ok(new { message = await line.Coordinator!.SetNonCanonAsync(req.Badge) }));

// Lot progress (boards produced vs PO target) + supervisor add-qty.
app.MapGet("/api/lot", (LineService line) => Results.Ok(line.Coordinator?.LotProgress() ?? (object)new { lotNo = "" }));
app.MapPost("/api/lot/addqty", async (LineService line, LotAddReq req) =>
    Results.Ok(new { message = await line.Coordinator!.AddLotQtyAsync(req.Qty, req.Badge) }));
// Lot component-usage verification (read-only): simulated usage (mount-points/board × lot board count) per feeder,
// and whether each machine's board tally matches the lot count so the actual usage matches the simulation. Also
// written to a lot-usage/<lot>.json file at lot completion. Empty when no lot is tracked.
app.MapGet("/api/lot/usagecheck", (LineService line) => Results.Ok(line.Coordinator?.LotUsageCheck() ?? (object)new { lotNo = "" }));
// Offline badge cache status/diagnostic: how many badges are cached locally + the last preload outcome. Also
// forces a preload so the offline operator/supervisor auth is ready even right after a restart.
app.MapGet("/api/badges/status", async (IReelPartRepository repo) =>
{
    int justCached = await repo.PreloadBadgesAsync();
    var sql = repo as Pvs.Data.SqlReelPartRepository;
    return Results.Ok(new { cached = sql?.CachedBadgeCount ?? 0, justPreloaded = justCached, outcome = sql?.LastPreloadOutcome ?? "n/a (no SQL repo)" });
});
// Learned exhaust-accuracy (shadow): per part, how far real reel exhaust drifts from PVS's prediction — the
// tell-tale that the per-board count is off. Positive err/board = PVS under-counts (reel empties early). Read-only.
app.MapGet("/api/calibration", (LineService line) => Results.Ok(new
{
    parts = (line.Coordinator?.Calibration() ?? Array.Empty<Pvs.Core.Inventory.PartCalibration>())
        .Select(p => new { part = p.Part, errPerBoard = Math.Round(p.PerBoardErrorEwma, 3), samples = p.Samples,
                           lastErrPerBoard = Math.Round(p.LastErrorPerBoard, 3), updatedAt = p.UpdatedAt })
}));
// Manual lot selection: list candidate lots + set the running lot from the dropdown.
app.MapGet("/api/lot/options", async (LineService line) =>
    Results.Ok(line.Coordinator is null ? (object)new { selected = "", manual = false, options = Array.Empty<object>() }
                                        : await line.Coordinator.LotOptionsAsync()));
app.MapPost("/api/lot/select", async (LineService line, LotSelectReq req) =>
    Results.Ok(new { message = await line.Coordinator!.SelectLotAsync(req.LotNo, req.Badge, req.Produced) }));
// On-demand DailyProductionCount flush (the 30-min timer does this automatically when WriteProductionCount is on).
app.MapPost("/api/production/flush", async (LineService line) =>
{
    if (line.Coordinator is null) return Results.Ok(new { message = "coordinator not ready" });
    if (!line.Config.WriteProductionCount) return Results.Ok(new { message = "WriteProductionCount is disabled" });
    await line.Coordinator.FlushProductionCountAsync();
    return Results.Ok(new { message = "production count flushed" });
});

app.MapGet("/api/session", (LineService line) => Results.Ok(line.Coordinator!.Snapshot()));

app.MapPost("/api/session/badge", async (LineService line, ScanReq req) =>
    Results.Ok(await line.Coordinator!.ScanBadgeAsync(req.Value)));

app.MapPost("/api/session/reel", async (LineService line, ReelReq req) =>
    Results.Ok(await line.Coordinator!.ScanReelAsync(req.PartNumber, req.Uid)));

app.MapPost("/api/session/quantity", async (LineService line, QtyReq req) =>
    Results.Ok(await line.Coordinator!.EnterQuantityAsync(req.Quantity)));

app.MapPost("/api/session/skip", async (LineService line) =>
    Results.Ok(await line.Coordinator!.SkipAsync()));

app.MapPost("/api/session/cancel", (LineService line) =>
    Results.Ok(line.Coordinator!.Cancel()));

app.MapPost("/api/session/feeder/clear", async (LineService line, FeederReq req) =>
    Results.Ok(await line.Coordinator!.ClearFeederAsync(req.Machine, req.Feeder)));

app.MapPost("/api/session/machine/confirm", async (LineService line, MachineReq req) =>
    Results.Ok(await line.Coordinator!.ConfirmMachineAsync(req.Machine)));

app.MapPost("/api/session/machine/scan", (LineService line, MachineReq req) =>
    Results.Ok(line.Coordinator!.GoToMachine(req.Machine)));

app.MapPost("/api/session/start", (LineService line, StartReq req) =>
{
    if (req.Mode?.Equals("ModelChange", StringComparison.OrdinalIgnoreCase) == true)
        return Results.Ok(new { message = line.Coordinator!.StartModelChange() });
    var purpose = req.Mode?.Equals("LotEnd", StringComparison.OrdinalIgnoreCase) == true
        ? ScanPurpose.LotEnd : ScanPurpose.ShiftChange;
    return Results.Ok(new { message = line.Coordinator!.StartFullScan(purpose) });
});

// Model-change quantity step: operator confirms/flags, supervisor corrects.
app.MapPost("/api/session/qty/confirm", async (LineService line) =>
    Results.Ok(await line.Coordinator!.ConfirmQtyAsync()));

app.MapPost("/api/session/qty/reject", async (LineService line) =>
    Results.Ok(await line.Coordinator!.RejectQtyAsync()));

app.MapPost("/api/session/qty/correct", async (LineService line, QtyReq req) =>
    Results.Ok(await line.Coordinator!.EnterCorrectedQtyAsync(req.Quantity)));

app.MapPost("/api/session/prefill", async (LineService line, ReelReq req) =>
    Results.Ok(new { quantity = await line.Coordinator!.PrefillQuantityAsync(req.PartNumber, req.Uid) }));

// ---- reel quantity editor (supervisor-gated) ----

app.MapGet("/api/reel", async (IReelPartRepository repo, string uid) =>
{
    var reel = await repo.FindStockOutReelAsync(uid ?? "");
    return Results.Ok(reel is null
        ? (object)new { found = false }
        : new { found = true, uid = reel.PartUid, part = reel.PartNumber, qty = reel.RemainingQty });
});

app.MapPost("/api/reel/qty", async (IReelPartRepository repo, ILineRecordSink records, LineService line, ReelQtyReq req) =>
{
    var badge = await repo.FindBadgeAsync(req.Badge ?? "");
    if (badge is null || !badge.CanReleaseInterlock)
        return Results.Ok(new { ok = false, message = "Scan a SUPERVISOR badge (L2+) to change a reel quantity." });
    var reel = await repo.FindStockOutReelAsync(req.Uid ?? "");
    if (reel is null)
        return Results.Ok(new { ok = false, message = $"Reel UID '{req.Uid}' not found in StockOuts." });
    if (req.Quantity < 0)
        return Results.Ok(new { ok = false, message = "Quantity cannot be negative." });
    int rows;
    try { rows = await repo.UpdateReelQtyAsync(reel.PartUid, reel.PartNumber, req.Quantity); }
    catch (Exception ex) { return Results.Ok(new { ok = false, message = "DB write failed (has pvs_ro been granted UPDATE on StockOuts?): " + ex.Message }); }
    await records.WriteAsync(new VerificationRecord(DateTime.Now, line.Config.LineName, "ReelQtyEdit", 0, 0,
        rows > 0 ? "Updated" : "NoRow", Supervisor: badge.Name, NewReelUid: reel.PartUid, NewReelPart: reel.PartNumber,
        Quantity: req.Quantity, Overridden: true, Note: $"qty {reel.RemainingQty}->{req.Quantity}"));
    return Results.Ok(new { ok = rows > 0, oldQty = reel.RemainingQty,
        message = rows > 0
            ? $"{reel.PartNumber} ({reel.PartUid}): qty {reel.RemainingQty} -> {req.Quantity} by {badge.Name}."
            : "Reel row not found to update." });
});

// Bring back a reel that was auto-retired as a spent remnant — restores its StockOut qty + closes the record. Supervisor-gated.
app.MapPost("/api/reel/restore", async (LineService line, RestoreReelReq req) =>
    Results.Ok(new { message = await line.Coordinator!.RestoreConsumedReelAsync(req.Uid ?? "", req.Badge ?? "") }));

// Whole-line UNLOAD (supervisor L2+): month-end return-to-store, or a completely new model. Takes EVERY reel off
// the feeders — clears the feeder→reel mapping and stops tracking — WITHOUT touching StockOut (each UID keeps its
// remaining count so the store counts StockIn accurately). Reversible via /api/unload/restore. Returns the manifest.
app.MapPost("/api/unload", async (LineService line, IReelPartRepository repo, ILineRecordSink records, BadgeReq req) =>
{
    var badge = await repo.FindBadgeAsync(req.Badge ?? "");
    if (badge is null || !badge.CanReleaseInterlock)
        return Results.Ok(new { ok = false, message = "Scan a SUPERVISOR badge (L2+) to unload all feeders." });
    var reels = line.Coordinator!.UnloadAll(badge.Name);
    await records.WriteAsync(new VerificationRecord(DateTime.Now, line.Config.LineName, "UnloadAll", 0, 0, "Unloaded",
        Supervisor: badge.Name, Note: $"{reels.Count} reels off feeders; StockOut untouched", LotNo: line.Coordinator?.CurrentLotNo));
    return Results.Ok(new
    {
        ok = true,
        count = reels.Count,
        reels = reels.Select(r => new { machine = r.Machine, feeder = r.Feeder, part = r.Part, uid = r.Uid, remaining = r.Remaining }),
        message = $"Unloaded {reels.Count} reels off the feeders by {badge.Name}. StockOut counts kept — reels ready to return to store."
    });
});

// Reverse the last unload (supervisor L2+): re-load the feeders from the saved snapshot and re-baseline inventory.
app.MapPost("/api/unload/restore", async (LineService line, IReelPartRepository repo, ILineRecordSink records, BadgeReq req) =>
{
    var badge = await repo.FindBadgeAsync(req.Badge ?? "");
    if (badge is null || !badge.CanReleaseInterlock)
        return Results.Ok(new { ok = false, message = "Scan a SUPERVISOR badge (L2+) to restore an unload." });
    var reels = await line.Coordinator!.RestoreLastUnloadAsync(badge.Name);
    if (reels.Count == 0)
        return Results.Ok(new { ok = false, count = 0, message = "Nothing to restore — no unload snapshot found." });
    await records.WriteAsync(new VerificationRecord(DateTime.Now, line.Config.LineName, "UnloadRestore", 0, 0, "Restored",
        Supervisor: badge.Name, Note: $"{reels.Count} reels re-loaded onto feeders", LotNo: line.Coordinator?.CurrentLotNo));
    return Results.Ok(new { ok = true, count = reels.Count,
        message = $"Restored {reels.Count} reels back onto the feeders by {badge.Name}." });
});

// ---- machine feeder inventory ----

// Feeders for a machine — driven by the LOADED FEEDER LIST (manual pen-drive load if present, else ProductBOM),
// NOT by the scan. Every feeder on the list is shown regardless of scan status: the scan only fills in which reel
// is on it and how much is left. An un-scanned feeder appears with scanned=false and a null reel/qty, so the
// machine-inventory view always mirrors the feeder list. (The feeder list is the single source of truth — the
// same list the scan check interlocks against.)
app.MapGet("/api/inventory", async (LineService line, IReelPartRepository repo, int machine) =>
{
    var coord = line.Coordinator;
    var list = coord?.FeederList() ?? Array.Empty<(int Machine, int Feeder, string Part)>();
    var feeders = new List<object>();
    foreach (var f in list.Where(x => x.Machine == machine).OrderBy(x => x.Feeder))
    {
        var reel = line.Reels.Get(machine, f.Feeder);
        // Prefer the LIVE remaining (machine inventory, decremented per board) so this matches the exhaust
        // forecast; fall back to the StockOuts qty when the feeder isn't being tracked live; null when un-scanned.
        // Read from the coordinator (includes manual/JUKI machines) — NOT line.Listeners, which is serial-only.
        var fs = coord?.FeederStateOf(machine, f.Feeder);
        bool live = fs is { IsTracked: true };
        int? qty = live ? fs!.Remaining
                        : (reel is null ? null : await repo.FindStockOutQtyAsync(reel.Uid, f.Part));
        feeders.Add(new { feeder = f.Feeder, part = f.Part, uid = reel?.Uid, qty, live, scanned = reel is not null });
    }
    return Results.Ok(new { model = coord?.Model?.Name, side = coord?.Side, machine, feeders });
});

// Adjust a feeder's reel quantity at the line (supervisor-authorised; writes StockOuts).
app.MapPost("/api/inventory/adjust", async (LineService line, IReelPartRepository repo, ILineRecordSink records, InvAdjustReq req) =>
{
    var badge = await repo.FindBadgeAsync(req.Badge ?? "");
    if (badge is null || !badge.CanReleaseInterlock)
        return Results.Ok(new { ok = false, message = "Scan a SUPERVISOR badge (L2+) to adjust a feeder quantity." });
    var reel = line.Reels.Get(req.Machine, req.Feeder);
    if (reel is null)
        return Results.Ok(new { ok = false, message = $"No reel known on M{req.Machine} feeder {req.Feeder} — scan it in a shift/model change first." });
    if (req.Quantity < 0) return Results.Ok(new { ok = false, message = "Quantity cannot be negative." });
    // Update the LIVE inventory + the line-local record only — no StockOuts write.
    bool applied = line.Coordinator?.SetFeederRemaining(req.Machine, req.Feeder, req.Quantity) ?? false;
    await records.WriteAsync(new VerificationRecord(DateTime.Now, line.Config.LineName, "FeederAdjust", req.Machine, req.Feeder,
        applied ? "Updated" : "NotTracked", Supervisor: badge.Name, NewReelUid: reel.Uid, NewReelPart: reel.Part,
        Quantity: req.Quantity, Overridden: true, Note: $"qty -> {req.Quantity} (recorded on line PC)", LotNo: line.Coordinator?.CurrentLotNo));
    return Results.Ok(new { ok = applied,
        message = applied ? $"M{req.Machine} feeder {req.Feeder} ({reel.Part}): qty -> {req.Quantity} by {badge.Name} (line PC record)."
                          : "Feeder not tracked yet — run a shift/model check first." });
});

// Password-protected shutdown: a clean stop frees the COM ports (e.g. to run serial-port test software).
// The process exits 0, so the scheduled task does NOT auto-restart it — it comes back on the next reboot or a
// manual Start. Password defaults to "100732" (config: shutdownPassword).
app.MapPost("/api/shutdown", (LineService line, IHostApplicationLifetime lifetime, ShutdownReq req) =>
{
    var pw = string.IsNullOrWhiteSpace(line.Config.ShutdownPassword) ? "100732" : line.Config.ShutdownPassword;
    if ((req?.Password ?? "") != pw)
        return Results.Ok(new { ok = false, message = "Wrong password — PVS not shut down." });
    // Reply first, then stop the host a moment later so the browser gets the response.
    _ = Task.Run(async () => { await Task.Delay(600); lifetime.StopApplication(); });
    return Results.Ok(new { ok = true, message = "PVS is shutting down — the serial ports will be released. It restarts on the next reboot." });
});

// Email relay: a non-gateway line POSTs its outbound message here; the GATEWAY line (the one with internet)
// SMTPs it via pvsbangi. Requires the shared key so only the plant's lines can use it.
app.MapPost("/api/sendmail", async (Pvs.LineApp.Runtime.EmailSender email, SendMailReq req) =>
{
    if (!email.IsGateway) return Results.Ok(new { ok = false, message = "this line is not the email gateway" });
    if (!email.KeyOk(req.Key)) return Results.Ok(new { ok = false, message = "bad key" });
    if (req.To is null || req.To.Length == 0) return Results.Ok(new { ok = false, message = "no recipients" });
    var ok = await email.SendSmtpAsync(req.To, req.Subject ?? "(no subject)", req.Body ?? "");
    return Results.Ok(new { ok, message = ok ? "sent" : "send failed (see gateway logs)" });
});

// Send a test email through the normal path (SMTP if this line is the gateway, else relayed to it).
app.MapPost("/api/email/test", async (Pvs.LineApp.Runtime.EmailSender email, LineService line, string? to) =>
{
    if (!email.Enabled) return Results.Ok(new { ok = false, message = "email disabled in config" });
    var subject = $"PVS {line.Config.LineName} — email test";
    var body = $"Test email from PVS on {line.Config.LineName} at {DateTime.Now:yyyy-MM-dd HH:mm:ss}. Gateway={email.IsGateway}.\r\nIf you received this, {line.Config.LineName}'s reports can be emailed.";
    bool ok = string.IsNullOrWhiteSpace(to)
        ? await email.SendToPicsAsync(subject, body)
        : await email.SendAsync(new[] { to }, subject, body);
    return Results.Ok(new { ok, gateway = email.IsGateway, message = ok ? "sent" : "not sent (see logs)" });
});

// Supervisor sets the current lot count to a machine-reported value (M4 = PCB-out). Badge-gated (L2+) and
// capped at the lot target — an over-target value is refused (an un-reset machine counter) unless force=true.
// The reconciler adopts automatically (also capped) when M4 reads ahead; this is the manual/immediate lever.
app.MapPost("/api/lot/adopt", async (LineService line, AdoptReq req) =>
{
    if (line.Coordinator is null) return Results.Ok(new { message = "coordinator not ready" });
    int pp = line.Coordinator.PerPanel;
    int p = req.Panels ?? (req.Boards is int b && pp > 0 ? (int)Math.Round((double)b / pp) : -1);
    if (p < 0) return Results.Ok(new { message = "pass panels or boards" });
    return Results.Ok(new { message = await line.Coordinator.AdoptLotCountBadgedAsync(req.Badge, p, req.Force) });
});

// Per-machine board-count sync (monitor page): the operator keys a machine's Sony HMI count (PANELS = completed
// PWBs); PVS corrects THAT machine's feeder draw-down by the difference from its own tally. This is the manual
// upkeep for the fact that PVS can't read the machine counter over serial while producing (Appendix F: A4E00).
// Each machine is independent — its own board-out count decrements its own feeders. Badge-gated (L2+).
app.MapGet("/api/machine/counts", (LineService line) =>
    Results.Ok(line.Coordinator is null
        ? (object)new { machines = Array.Empty<object>() }
        : new { machines = line.Coordinator.MachineBoardCounts()
            .Select(m => new { machine = m.Machine, boards = m.BoardsApplied, trackedFeeders = m.TrackedFeeders, needsBadge = m.NeedsBadge }) }));
app.MapPost("/api/machine/synccount", async (LineService line, MachineCountReq req) =>
{
    if (line.Coordinator is null) return Results.Ok(new { message = "coordinator not ready" });
    return Results.Ok(new { message = await line.Coordinator.SyncMachineBoardCountAsync(req.Machine, req.Panels, req.Badge) });
});

// OPERATOR manual read (inventory page): the operator STOPS the machine, then triggers a fresh C1M (board count)
// + C1Z (per-feeder) read. Fed through the serial pipeline — C1M and C1Z fire ONE AT A TIME (the second only
// after the first finishes), and the background rotation stands aside (ManualReadActive) so the reads get a clean
// line. Reports land reliably in Normal Stop (Appendix G: Possible). Read-only; never sends a control command.
app.MapPost("/api/machine/readnow", async (LineService line, int? machine) =>
{
    int mno = machine ?? 1;
    var l = line.Listeners.FirstOrDefault(x => x.Channel.Machine == mno);
    if (l is null) return Results.NotFound(new { error = $"Machine {mno} is not configured / not serial." });
    var ch = l.Channel;
    var name = MachineReportName(ch.ProgramName);

    async Task<bool> Fire(Func<DateTime, bool> landed, Action<DateTime> send)
    {
        var at = DateTime.Now; send(at);
        var deadline = at.AddSeconds(30);
        while (DateTime.Now < deadline)
        {
            await Task.Delay(1000);
            if (landed(at)) return true;
            if (ch.LastReportError is string e && (e.StartsWith("A4", StringComparison.Ordinal) || e.StartsWith("A5", StringComparison.Ordinal) || e == "A3"))
                return false;   // refused (machine still running / wrong state) — stop, report why
        }
        return false;
    }

    if (line.Reconciler is not null) line.Reconciler.ManualReadActive = true;   // pause the rotation — this read owns the pipeline
    string? c1mErr = null, c1zErr = null; long boards = 0; bool gotM = false, gotZ = false;
    try
    {
        // 1) C1M — board count (Sum by Lot)
        gotM = await Fire(at => ch.RawProductionReport is { Length: > 0 } && ch.CompletedPwbsAt >= at,
                          at => ch.RequestProductionCount(at, name, timeoutSec: 25));
        c1mErr = ch.LastReportError;
        if (gotM && ch.RawProductionReport is { Length: > 0 } rawM && line.Reconciler is not null)
        {
            var perLot = line.Reconciler.PerLotFields(mno, line.Coordinator?.CurrentLotNo ?? "", rawM);
            boards = perLot.TryGetValue("PC", out var pc) ? pc : 0;
        }
        // 2) C1Z — per-feeder (only AFTER C1M finished — one at a time through the pipeline)
        gotZ = await Fire(at => ch.RawSupplyReport is { Length: > 50 } && ch.SupplyReportAt >= at,
                          at => ch.RequestSupplyReport(at, name, timeoutSec: 25));
        c1zErr = ch.LastReportError;
    }
    finally { if (line.Reconciler is not null) line.Reconciler.ManualReadActive = false; }

    var parsed = Pvs.Core.Serial.SonySupplyReport.Parse(ch.RawSupplyReport);
    return Results.Ok(new
    {
        machine = mno, program = ch.ProgramName,
        c1m = new { got = gotM, boards, error = c1mErr },
        c1z = new { got = gotZ, feeders = parsed.Feeders?.Count ?? 0, error = c1zErr }
    });
});

// SETUP: master on/off for the AUTOMATIC C1Z (per-feeder) and C1M (board-count) reads. When a switch is OFF, PVS
// never fires that command on its own — the operator reads it on demand from the inventory page. Applied live
// (next tick), persisted across restarts. Read-only either way — these govern only WHEN PVS reads, never a control.
app.MapGet("/api/setup", (LineService line) =>
{
    var r = line.Reconciler;
    return Results.Ok(new { autoC1z = r?.AutoC1z ?? true, autoC1m = r?.AutoC1m ?? true, tallySync = r?.TallySyncMode ?? "off", ready = r is not null });
});
app.MapPost("/api/setup", (LineService line, SetupReq req) =>
{
    var r = line.Reconciler;
    if (r is null) return Results.Ok(new { message = "reconciler not ready", autoC1z = true, autoC1m = true, tallySync = "off" });
    if (req.AutoC1z is bool z) r.AutoC1z = z;
    if (req.AutoC1m is bool m) r.AutoC1m = m;
    if (req.TallySync is string t) r.TallySyncMode = t;
    return Results.Ok(new { autoC1z = r.AutoC1z, autoC1m = r.AutoC1m, tallySync = r.TallySyncMode,
        message = $"Auto C1Z rotation {(r.AutoC1z ? "ON" : "OFF")} · Auto C1M reads {(r.AutoC1m ? "ON" : "OFF")} · Machine-tally sync {r.TallySyncMode.ToUpperInvariant()}" });
});

// MACHINE-TALLY SYNC state: per machine, the last C1Z evaluation (machine's own panel count vs PVS's board tally)
// + recent history. Read-only; the mode (off/shadow/apply) is set on /api/setup.
app.MapGet("/api/tallysync", (LineService line) =>
    Results.Ok(line.Coordinator?.TallySyncState(line.Reconciler?.TallySyncMode ?? "off") ?? new { mode = "off", machines = Array.Empty<object>() }));

// LIVE SERIAL TRACE: the last TX/RX frames for one machine (send commands + decoded replies), for the real-time
// serial log on the inventory page. Poll with ?after=<last seq> to fetch only new lines. Read-only.
app.MapGet("/api/serial/trace", (LineService line, int? machine, long? after) =>
{
    var l = line.Listeners.FirstOrDefault(x => x.Channel.Machine == (machine ?? 1));
    if (l is null) return Results.NotFound(new { error = $"Machine {(machine ?? 1)} is not a serial machine." });
    var evts = l.TraceSince(after ?? 0);
    return Results.Ok(new
    {
        machine = l.Channel.Machine,
        entries = evts.Select(e => new { seq = e.Seq, t = e.T.ToString("HH:mm:ss.fff"), dir = e.Dir, text = e.Text, frame = e.Frame })
    });
});

// Supervisor force-ends the running lot (finalises the count + clears it for the next lot). Badge-gated (L2+).
app.MapPost("/api/lot/end", async (LineService line, BadgeReq req) =>
    Results.Ok(new { message = line.Coordinator is null ? "coordinator not ready" : await line.Coordinator.ForceEndLotAsync(req.Badge) }));

// Downtime capture: live state for the operator screen (open reason + elapsed, and whether the line is
// stopped-producing so the UI can raise the prompt) and per-cell open recoveries.
app.MapGet("/api/stop/state", (LineService line) =>
    line.StopTracking is { } st ? Results.Ok(st.State()) : Results.Ok(new { ready = false }));

// Operator COMMENTS on the auto-captured stop: their reason classification and/or a free note. The downtime
// itself is auto-recorded from the machine's R1 signal — this only annotates the current (or most-recent) span.
app.MapPost("/api/stop/reason", (LineService line, StopReasonReq req) =>
{
    if (line.StopTracking is not { } st) return Results.Ok(new { ok = false, message = "stop tracker not ready" });
    return st.AddComment(req.Reason, req.Note)
        ? Results.Ok(new { ok = true, state = st.State() })
        : Results.Ok(new { ok = false, message = "no open stop to comment on (or empty comment)" });
});

// ---- Board input tracking (count-only, no interlock) ----
// First side = bare-board PACKS (scan reel UID → part+qty from StockOuts); second side = MAGAZINES (scan the MCS
// slip QR → pcs = QTY÷N). Deduped, accumulated toward the lot; also reports whether the line is producing.
app.MapPost("/api/boardinput/scan", async (LineService line, IReelPartRepository repo, Pvs.LineApp.Runtime.BoardInputState bi, ScanReq req) =>
{
    var coord = line.Coordinator;
    bi.EnsureLot(coord?.CurrentLotNo);
    bool producing = line.StopTracking?.HealthInputs().Producing ?? false;
    var v = (req.Value ?? "").Trim();
    if (v.Length == 0) return Results.Ok(new { ok = false, message = "empty scan" });

    // Magazine slip (second side)?
    var slip = Pvs.Core.Boards.MagazineSlipParser.TryParse(v);
    if (slip is not null)
    {
        string lot = coord?.CurrentLotNo ?? "";
        if (lot.Length > 0 && !string.Equals(slip.Po, lot, StringComparison.OrdinalIgnoreCase))
            return Results.Ok(new { ok = false, message = $"⚠ wrong lot — slip PO {slip.Po}, running lot {lot}", state = bi.State(producing) });
        var (added, msg) = bi.AddMagazine(slip);
        return Results.Ok(new { ok = added, message = msg, state = bi.State(producing) });
    }

    // Otherwise a bare-board PACK by reel UID (first side) — resolve part + qty from StockOuts.
    var reel = await repo.FindStockOutReelAsync(v);
    if (reel is null) return Results.Ok(new { ok = false, message = "not a magazine slip, and UID not found in StockOuts", state = bi.State(producing) });
    var (a2, m2) = bi.AddPack(reel.PartUid, reel.PartNumber, reel.RemainingQty);
    return Results.Ok(new { ok = a2, message = m2, state = bi.State(producing) });
});

app.MapGet("/api/boardinput/state", (LineService line, Pvs.LineApp.Runtime.BoardInputState bi) =>
{
    bi.EnsureLot(line.Coordinator?.CurrentLotNo);
    bool producing = line.StopTracking?.HealthInputs().Producing ?? false;
    return Results.Ok(bi.State(producing));
});

app.MapPost("/api/boardinput/reset", (Pvs.LineApp.Runtime.BoardInputState bi) => { bi.Reset(); return Results.Ok(new { ok = true }); });

// Daiya Graph (GMS-QP-15F06) — assembled READ-ONLY from PVS for the current shift (printable at /daiya.html).
app.MapGet("/api/daiya", async (LineService line, IReelPartRepository repo, string? date, string? shift) =>
{
    var co = line.Coordinator;
    if (co is null) return Results.Ok(new { ready = false });
    var now = DateTime.Now;
    string today = now.ToString("yyyy-MM-dd");
    var shifts = line.Config.ToShiftSchedule();
    string curShift = shifts.DpcName(now);   // ONE clock: the configured shiftTimes (audit M1)
    // Requested date/shift (defaults: today + current shift). History pulls from the DB so the sheet is never
    // stale — the in-memory Daiya log has no day boundary, so reading it directly showed the last day that ran.
    string reqDate = string.IsNullOrWhiteSpace(date) ? today : date!.Trim();
    string reqShift = string.IsNullOrWhiteSpace(shift) ? (reqDate == today ? curShift : "Morning") : shift!.Trim();
    bool isLive = reqDate == today && string.Equals(reqShift, curShift, StringComparison.OrdinalIgnoreCase);

    // Production is ALWAYS from DailyProductionCount (date + shift scoped) — authoritative, never stale.
    string lineNo = line.Config.LineId.ToString();
    List<ProductionRun> runs;
    try { runs = (await repo.GetDailyProductionAsync(lineNo, reqDate)).ToList(); } catch { runs = new(); }
    runs = runs.Where(r => string.Equals(r.Shift?.Trim(), reqShift, StringComparison.OrdinalIgnoreCase)).ToList();

    // :30-aligned slot hour (8..19 window) from an "HH:mm:ss" string; -1 if unparseable/out of window.
    static int SlotOf(string? hms)
    {
        if (string.IsNullOrWhiteSpace(hms) || !TimeSpan.TryParse(hms, out var t)) return -1;
        int h = t.Minutes >= 30 ? t.Hours : t.Hours - 1;
        return h >= 8 && h <= 19 ? h : -1;
    }

    // ONE Daiya per day/shift can hold SEVERAL lots. Group per lot, ordered by when each last ran, so the sheet
    // lists every lot of the day and the header reflects the CURRENT (latest) lot — not whichever lot has the most
    // boards (that kept the header stuck on a finished lot while a new one was running).
    var lotGroups = runs.GroupBy(r => r.LotNo)
        .Select(g => new {
            Lot = g.Key, Model = g.First().Model, Side = g.First().Side,
            Boards = g.Sum(x => x.Quantity),
            LastEnd = g.Select(x => x.EndTime).Where(x => !string.IsNullOrWhiteSpace(x) && x != "00:00:00").DefaultIfEmpty("").Max()
        })
        .OrderBy(x => x.LastEnd).ToList();
    // Per-lot rows (each lot's own target + output), and the day target = SUM of the lots' targets.
    var lots = new List<object>();
    int dayTargetSum = 0; bool anyTarget = false;
    foreach (var lg in lotGroups)
    {
        int pp = Math.Max(1, line.Config.PanelBoardsFor(lg.Model));
        int? lt = null;
        if (!string.IsNullOrWhiteSpace(lg.Lot)) { try { lt = await repo.GetLotTargetAsync(lg.Lot); } catch { } }
        if (lt.HasValue) { dayTargetSum += lt.Value; anyTarget = true; }
        lots.Add(new { lotNo = lg.Lot, model = string.IsNullOrWhiteSpace(lg.Side) ? lg.Model : $"{lg.Model} {lg.Side} SIDE",
                       target = lt, boards = lg.Boards, panels = lg.Boards / pp });
    }
    var current = lotGroups.LastOrDefault();   // most recent lot = header
    string headModel = current?.Model ?? "";
    string headSide = current?.Side ?? "";
    string lot = current?.Lot ?? "";
    int perPanel = Math.Max(1, line.Config.PanelBoardsFor(headModel));
    int? target = anyTarget ? dayTargetSum : (int?)null;   // day target = sum of all lots' targets

    int[] slots = { 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19 };
    static string Lbl(int h) { int hr = h % 12; if (hr == 0) hr = 12; return $"{hr}.30{(h < 12 ? "am" : "pm")}"; }
    var boardsBySlot = new Dictionary<int, int>();
    foreach (var r in runs) { int sl = SlotOf(r.EndTime); if (sl < 0) continue; boardsBySlot[sl] = (boardsBySlot.TryGetValue(sl, out var v) ? v : 0) + r.Quantity; }
    var hourly = slots.Select(h => { int b = boardsBySlot.TryGetValue(h, out var v) ? v : 0; return new { label = Lbl(h), panel = b / perPanel, pcs = b }; }).ToList();

    int totalBoards = runs.Sum(r => r.Quantity);
    int panels = totalBoards / perPanel;
    string? firstStart = runs.Select(r => r.StartTime).Where(x => !string.IsNullOrWhiteSpace(x) && x != "00:00:00").DefaultIfEmpty(null).Min();
    string? lastEnd = runs.Select(r => r.EndTime).Where(x => !string.IsNullOrWhiteSpace(x) && x != "00:00:00").DefaultIfEmpty(null).Max();

    // Operators are live-only (not persisted historically). Downtime is clipped to THIS sheet's shift window
    // (date + shift), so the Night sheet never carries the Morning's stops and a past shift within the tracker's
    // 48 h retention still shows its downtime (audit M3).
    List<string> operators = new(); string? leader = null;
    object[] downtime = System.Array.Empty<object>(); object[] lostByMachine = System.Array.Empty<object>();
    if (isLive)
    {
        var d = co.Daiya;
        var ops = d.Operators();
        leader = ops.FirstOrDefault(o => o.Level >= 2)?.Name;
        operators = ops.Where(o => o.Level < 2).Select(o => o.Name).Take(4).ToList();
        if (operators.Count == 0 && leader is null && ops.Count > 0) operators = ops.Select(o => o.Name).Take(4).ToList();
    }
    var sheetWindow = DateTime.TryParse(reqDate, out var sheetDate) ? shifts.Window(sheetDate, reqShift) : null;
    if (sheetWindow is { } sw && line.StopTracking is { } tracker)
    {
        var code = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        { {"rest","B"},{"scheduled","B"},{"waiting_part","K"},{"no_air","J"},{"machine","I"},{"estop","N"},{"error","J"},{"starved","O"},{"stopped","O"},{"offline","N"} };
        var spans = tracker.SpansDetailed(sw.From, sw.To);
        downtime = spans.Select(s => (object)new { start = s.Start, end = s.End, reason = s.Reason,
                               machineReason = s.MachineReason, machines = s.Machines,
                               code = code.TryGetValue(s.Reason, out var c) ? c : "O",
                               minutes = Math.Round(((s.End ?? now) - s.Start).TotalMinutes, 1) }).ToArray();
        // Total lost time attributed to each machine that was in the stop (a span can name >1 machine).
        var lm = new Dictionary<int, double>();
        foreach (var s in spans)
        {
            double mins = ((s.End ?? now) - s.Start).TotalMinutes;
            foreach (var mc in s.Machines) lm[mc] = (lm.TryGetValue(mc, out var v) ? v : 0) + mins;
        }
        lostByMachine = lm.OrderBy(k => k.Key)
            .Select(k => (object)new { machine = k.Key, minutes = Math.Round(k.Value, 1) }).ToArray();
    }

    return Results.Ok(new
    {
        ready = true, live = isLive, availableShifts = new[] { "Morning", "Night" },
        line = line.Config.LineName, lineId = line.Config.LineId, date = reqDate,
        shift = reqShift,
        model = string.IsNullOrWhiteSpace(headSide) ? headModel : $"{headModel} {headSide} SIDE",
        // PO NO names EVERY lot on this sheet (a day/shift can run several), in run order; LOT SIZE is their summed target.
        po = string.Join(", ", lotGroups.Select(g => g.Lot).Where(l => !string.IsNullOrWhiteSpace(l))), lotSize = target, board = "",
        leader, operators,
        startTime = firstStart is null ? null : $"{reqDate}T{firstStart}",
        endTime = lastEnd is null ? null : $"{reqDate}T{lastEnd}",
        machineCounter = panels, totalOutput = totalBoards, perPanel,
        targetShift = target, targetHour = target.HasValue ? (int?)Math.Ceiling(target.Value / 12.0) : null,
        hourly, lots, downtime, lostByMachine,
        legend = new object[]
        {
            new { c = "A", t = "Daily / Monthly Maintenance", g = "Planned" }, new { c = "B", t = "Schedule Stop / Break Time", g = "Planned" },
            new { c = "C", t = "New Model / Test Run", g = "Planned" }, new { c = "D", t = "Meeting / Shifting Check", g = "Planned" },
            new { c = "E", t = "Stock Take / Inventory", g = "Planned" }, new { c = "F", t = "Major Model Change", g = "Planned" },
            new { c = "G", t = "Minor Model Change", g = "Planned" }, new { c = "H", t = "Warm Up Machine", g = "Planned" },
            new { c = "I", t = "Machine Breakdown", g = "Unplanned" }, new { c = "J", t = "Machine Adjustment", g = "Unplanned" },
            new { c = "K", t = "Waiting Part", g = "Unplanned" }, new { c = "L", t = "Reflow Problem", g = "Unplanned" },
            new { c = "M", t = "AOI Adjustment", g = "Unplanned" }, new { c = "N", t = "Emergency Stop / Power Trip", g = "Unplanned" },
            new { c = "O", t = "Intermittent Stop", g = "Unplanned" }
        }
    });
});

// DEV-ONLY (Development environment; NOT mapped on the line PCs, which run under the default/Production env):
// inject a machine operating condition so the status-bar colours can be designed without a serial link. It only
// updates the READ-ONLY condition tracker via a synthetic R1 message — no DB, no machine control, no count/lot change.
// Double-gated so it can NEVER be live on a real line: Development env AND a "DEV"-named config (the line PCs
// run Production with names like LINE1PVS, so this endpoint is not even mapped there).
if (app.Environment.IsDevelopment() && (lineConfig.LineName?.Contains("DEV", StringComparison.OrdinalIgnoreCase) ?? false))
{
    app.MapPost("/api/dev/condition", (LineService line, DevCondReq req) =>
    {
        var ch = line.Listeners.FirstOrDefault(l => l.Channel.Machine == req.Machine)?.Channel;
        if (ch is null) return Results.Ok(new { ok = false, message = $"no machine {req.Machine}" });
        var payload = string.Equals(req.Code, "R0", StringComparison.OrdinalIgnoreCase)
            ? "R0CT" : "R1" + (req.Code ?? "").Trim().ToUpperInvariant();
        ch.Condition.Observe(Pvs.Core.Serial.SonyMessage.Parse(payload), DateTime.Now);
        return Results.Ok(new { ok = true, machine = req.Machine, condition = ch.Condition.Condition.ToString() });
    });
}

app.Run();

record DevCondReq(int Machine, string Code);
record StopReasonReq(string? Reason = null, string? Note = null);
record SendMailReq(string? Key, string[]? To, string? Subject, string? Body);
record ShutdownReq(string Password = "");
record MachineReq(int Machine);
record InvAdjustReq(int Machine, int Feeder, int Quantity, string Badge);
record ModelReq(int ProductId, string Side);
record ModelManualReq(int ProductId, string Side, string Badge = "");
record BadgeReq(string Badge = "");
record ManualFeederReq(int Machine, string? Csv, string Badge = "", bool Force = false);
record MachineSkipReq(int Machine, bool Skip, string Badge = "");
record RestoreReelReq(string? Uid, string Badge = "");
record AdoptReq(string Badge = "", int? Panels = null, int? Boards = null, bool Force = false);
record MachineCountReq(int Machine, int Panels, string Badge = "");
record SetupReq(bool? AutoC1z = null, bool? AutoC1m = null, string? TallySync = null);
record ScanReq(string Value);
record ReelReq(string PartNumber, string Uid);
record QtyReq(int Quantity);
record StartReq(string Mode);
record ReelQtyReq(string Uid, int Quantity, string Badge);
record FeederReq(int Machine, int Feeder);
record LotAddReq(int Qty, string Badge);
record LotSelectReq(string LotNo, string Badge = "", int Produced = -1);
