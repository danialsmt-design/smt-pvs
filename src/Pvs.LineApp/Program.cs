using Pvs.Core.Config;
using Pvs.Core.Data;
using Pvs.Core.Runtime;
using Pvs.Core.Verification;
using Pvs.Data;
using Pvs.LineApp.Records;
using Pvs.LineApp.Serial;
using Pvs.LineApp.Verification;

var builder = WebApplication.CreateBuilder(args);

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

// DB repo: real one when a connection is available, else a null repo so the monitor still runs.
builder.Services.AddSingleton<IReelPartRepository>(_ =>
    conn is not null ? new SqlReelPartRepository(conn) : new NullReelPartRepository());

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

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

// ---- API ----

// Line + machine status.
app.MapGet("/api/status", (LineService line) => Results.Ok(new
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
    machines = line.Listeners.Select(l =>
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
            program = l.Channel.ProgramName
        };
    })
}));

// Bring a machine online: re-open its port + re-enable real-time reporting.
app.MapPost("/api/machine/reconnect", (LineService line, MachineReq req) =>
    Results.Ok(new { message = line.Reconnect(req.Machine) }));

// Ask every machine which production program (.PWB) is loaded (C3P). Read replies from /api/status.
app.MapPost("/api/programs/query", (LineService line) =>
{
    line.QueryPrograms();
    return Results.Ok(new { message = "C3P sent to all machines — program names appear in /api/status within a second." });
});

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
    foreach (var l in line.Listeners)
    {
        var bph = l.Channel.BoardRate.BoardsPerHour;
        foreach (var f in l.Channel.Inventory.Feeders)
        {
            if (!f.IsTracked) continue;
            double? hours = (bph is double b && b > 0 && f.MountedPerBoard > 0)
                ? f.Remaining / (f.MountedPerBoard * b) : (double?)null;
            raw.Add((l.Channel.Machine, f.Feeder, f.PartNumber, f.Remaining, f.MountedPerBoard, hours));
        }
    }

    // Total per-cycle usage of each part (summed over the feeders carrying it), for the whole-lot material need.
    var perCycleByPart = raw.GroupBy(r => r.part)
        .ToDictionary(g => g.Key, g => g.Sum(x => x.perBoard), StringComparer.OrdinalIgnoreCase);
    int? cyclesWholeLot = (lotEffTarget is int et && perPanel > 0) ? et / perPanel : (int?)null;

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
            // Orange only when the reel WON'T finish the lot (times the warning to when it's actually running low)
            // AND the total issued material can't cover the lot -> a request is needed (lead time).
            bool underIssued = lotTracked && !lastsLot && neededWholeLot is int nwl && issuedPart < nwl;
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
                issued = issuedPart,       // pieces issued for the lot (this part)
                neededLot = neededWholeLot,// pieces the whole lot needs (this part)
                underIssued,               // issued reels won't cover the lot -> request more (lead time)
                shortfallBoards            // ~boards the issued material falls short by
            };
        })
        .ToList();

    // reels that will run out BEFORE lot end (known coverage, doesn't last) -> a change is needed this lot
    int? changesBeforeLotEnd = lotTracked
        ? ordered.Count(r => r.boardsLeftOnReel is int b && b < lotRemainingBoards)
        : (int?)null;
    var underIssuedParts = ordered.Where(r => r.underIssued).Select(r => r.part).Distinct().ToList();

    return Results.Ok(new
    {
        generatedAt = now,
        count = ordered.Count,
        lotNo = coord?.CurrentLotNo,
        lotTracked,
        boardsToLotEnd = lotRemainingBoards,
        changesBeforeLotEnd,
        underIssuedParts,
        rows = ordered
    });
});

// Reels ISSUED (StockOuts) for this line's current lot but NOT yet loaded on a machine — staged / waiting to go on.
// The exhaust forecast only shows loaded reels; this shows the ones on the shelf so the operator/planner can see
// what's ready vs what still has to be requested. issued − currently-loaded UIDs.
app.MapGet("/api/staged", (LineService line) =>
{
    var coord = line.Coordinator;
    var issued = coord?.LotIssuedReels() ?? (IReadOnlyList<Pvs.Core.Data.IssuedReel>)Array.Empty<Pvs.Core.Data.IssuedReel>();
    var loadedUids = new HashSet<string>(line.Reels.All().Select(r => (r.Uid ?? "").Trim()), StringComparer.OrdinalIgnoreCase);
    var staged = issued.Where(r => !loadedUids.Contains((r.Uid ?? "").Trim())).ToList();
    var parts = staged.GroupBy(r => r.PartNumber, StringComparer.OrdinalIgnoreCase)
        .Select(g => new
        {
            part = g.Key,
            reels = g.Count(),
            qty = g.Sum(x => x.Qty),
            uids = g.OrderByDescending(x => x.Qty).Select(x => new { uid = x.Uid, qty = x.Qty }).ToList()
        })
        .OrderByDescending(p => p.qty).ThenBy(p => p.part).ToList();
    return Results.Ok(new
    {
        lotNo = coord?.CurrentLotNo,
        issuedReels = issued.Count,
        loadedReels = loadedUids.Count,
        stagedReels = staged.Count,
        stagedQty = staged.Sum(r => r.Qty),
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

    var (spans, total, up) = line.Downtime?.Snapshot() ?? (Array.Empty<Pvs.Core.Runtime.DownSpan>(), TimeSpan.Zero, false);

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
        machines
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
    lotNo = line.Coordinator?.CurrentLotNo
}));

// Internal re-pull of a model's feeder map (used by "Refresh from DB"); does NOT pin a manual override.
app.MapPost("/api/model", async (LineService line, ModelReq req) =>
    Results.Ok(new { message = await line.Coordinator!.SelectModelAsync(req.ProductId, req.Side) }));

// Supervisor pins the running model from the dropdown (badge-gated); PVS verifies it against the machine.
app.MapPost("/api/model/manual", async (LineService line, ModelManualReq req) =>
    Results.Ok(new { message = await line.Coordinator!.SelectModelManualAsync(req.ProductId, req.Side, req.Badge) }));
// Supervisor releases the pin and returns the line to auto-detect (badge-gated).
app.MapPost("/api/model/auto", async (LineService line, BadgeReq req) =>
    Results.Ok(new { message = await line.Coordinator!.ClearManualModelAsync(req.Badge) }));

// Lot progress (boards produced vs PO target) + supervisor add-qty.
app.MapGet("/api/lot", (LineService line) => Results.Ok(line.Coordinator?.LotProgress() ?? (object)new { lotNo = "" }));
app.MapPost("/api/lot/addqty", async (LineService line, LotAddReq req) =>
    Results.Ok(new { message = await line.Coordinator!.AddLotQtyAsync(req.Qty, req.Badge) }));
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

// ---- machine feeder inventory ----

// Feeders for a machine of the currently-selected model, with the reel on each + its live StockOuts qty.
app.MapGet("/api/inventory", async (LineService line, IReelPartRepository repo, int machine) =>
{
    var model = line.Coordinator?.Model;
    if (model is null) return Results.Ok(new { model = (string?)null, machine, feeders = Array.Empty<object>() });
    var side = line.Coordinator!.Side;
    var map = await repo.GetFeederMapAsync(model.ProductId, side, line.Config.LineId);
    var ch = line.Listeners.FirstOrDefault(l => l.Channel.Machine == machine)?.Channel;
    var feeders = new List<object>();
    foreach (var f in map.Where(f => f.Machine == machine && f.Position.IsAssigned && f.Position.Number is int)
                          .OrderBy(f => f.Position.Number))
    {
        int feeder = f.Position.Number!.Value;
        var reel = line.Reels.Get(machine, feeder);
        // Prefer the LIVE remaining (machine inventory, decremented per board) so this matches the exhaust
        // forecast; fall back to the StockOuts qty when the feeder isn't being tracked live.
        var fs = ch?.Inventory.Get(feeder);
        bool live = fs is { IsTracked: true };
        int? qty = live ? fs!.Remaining
                        : (reel is null ? null : await repo.FindStockOutQtyAsync(reel.Uid, f.PartNumber));
        feeders.Add(new { feeder, part = f.PartNumber, uid = reel?.Uid, qty, live });
    }
    return Results.Ok(new { model = model.Name, side, machine, feeders });
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

// Force the current lot count to a machine-reported value (M4 = PCB-out, the trusted number). ?panels=N or
// ?boards=N. The reconciler does this automatically when M4 reads ahead; this is the manual/immediate lever.
app.MapPost("/api/lot/adopt", (LineService line, int? panels, int? boards) =>
{
    if (line.Coordinator is null) return Results.Ok(new { ok = false, message = "coordinator not ready" });
    int pp = line.Coordinator.PerPanel;
    int p = panels ?? (boards is int b && pp > 0 ? (int)Math.Round((double)b / pp) : -1);
    if (p < 0) return Results.Ok(new { ok = false, message = "pass ?panels=N or ?boards=N" });
    int applied = line.Coordinator.AdoptLotCount(p, "manual /api/lot/adopt");
    return Results.Ok(new { ok = applied >= 0, panels = applied, boards = applied * pp,
        message = applied >= 0 ? "adopted machine count" : "no current lot to adopt" });
});

// Supervisor force-ends the running lot (finalises the count + clears it for the next lot). Badge-gated (L2+).
app.MapPost("/api/lot/end", async (LineService line, BadgeReq req) =>
    Results.Ok(new { message = line.Coordinator is null ? "coordinator not ready" : await line.Coordinator.ForceEndLotAsync(req.Badge) }));

app.Run();

record SendMailReq(string? Key, string[]? To, string? Subject, string? Body);
record ShutdownReq(string Password = "");
record MachineReq(int Machine);
record InvAdjustReq(int Machine, int Feeder, int Quantity, string Badge);
record ModelReq(int ProductId, string Side);
record ModelManualReq(int ProductId, string Side, string Badge = "");
record BadgeReq(string Badge = "");
record ScanReq(string Value);
record ReelReq(string PartNumber, string Uid);
record QtyReq(int Quantity);
record StartReq(string Mode);
record ReelQtyReq(string Uid, int Quantity, string Badge);
record FeederReq(int Machine, int Feeder);
record LotAddReq(int Qty, string Badge);
record LotSelectReq(string LotNo, string Badge = "", int Produced = -1);
