using Pvs.Core.Config;
using Pvs.Core.Data;
using Pvs.Core.Events;
using Pvs.Core.People;
using Pvs.Core.Runtime;
using Pvs.Core.Verification;

namespace Pvs.LineApp.Verification;

/// <summary>Unified view of whatever verification is in progress, for the operator UI.</summary>
public sealed record SessionSnapshot(
    string Mode,
    string State,
    string Message,
    int? Machine = null,
    int? Feeder = null,
    string? ExpectedPart = null,
    string? Operator = null,
    IReadOnlyList<object>? Checklist = null,
    int? PrefillQuantity = null,
    IReadOnlyList<object>? Pending = null,
    IReadOnlyList<int>? Confirmed = null,
    string? OldReelPart = null,
    string? OldReelUid = null,
    string? NewReelPart = null,
    string? NewReelUid = null,
    int? Quantity = null,
    bool ProbableFalse = false,
    int? FeederRemaining = null);

/// <summary>A completed verification (full scan or model change), surfaced on the monitor page.</summary>
public sealed record VerifyCompletion(string Mode, DateTime At, string Model, string Result, string ShiftKey = "");

/// <summary>
/// Orchestrates verification on the line: resolves data from ReelPart-New and drives the (tested)
/// session objects, recording outcomes.
///
/// PARTS-OUTS ARE A PENDING LIST, NOT A LOCK (redesigned 2026-07-24 after a stuck session was found
/// blocking all parts-outs in production). A reported parts-out is added to a de-duplicated,
/// timestamped pending list — it never holds the session lock waiting for an operator. When an
/// operator badges in with nothing else active, a Mode B change is created for the OLDEST pending
/// parts-out (SOP: simultaneous exhaust, earliest first). Pending entries expire after a TTL so a
/// reel changed the old way doesn't prompt hours later. Any incomplete session also auto-clears
/// after an idle timeout, so an abandoned check can't block the line.
///
/// Stage 2: reads are live; records go to the local sink (no production DB writes).
/// </summary>
public sealed class SessionCoordinator : IDisposable
{
    private readonly IReelPartRepository _repo;
    private readonly IReadOnlyDictionary<int, MachineChannel> _channels;
    private readonly ILineRecordSink _records;
    private readonly LineConfig _config;
    private readonly ILogger _log;
    private readonly Pvs.LineApp.Inventory.FeederReelStore _reels;
    private readonly Pvs.LineApp.Inventory.RemainingStore _remaining;
    private readonly Pvs.Core.Inventory.ExhaustCalibration _calibration;   // shadow: learns per-part exhaust drift
    private readonly HashSet<string> _calibratedReels = new();             // machine|feeder|uid already sampled
    private DateTime _calibrationSavedAt;
    private static string CalibrationPath => System.IO.Path.Combine(AppContext.BaseDirectory, "calibration.json");

    private readonly Dictionary<(int machine, int feeder), string> _expected = new();

    // Supervisor-loaded feeder lists (from pen-drive Sony CSVs) that OVERRIDE the DB ProductBOM for the current
    // lot — used when a machine breaks down and its feeders are reshuffled across the running cells. Keyed by
    // machine. Non-empty => MANUAL MODE: _expected is built from these (scoped to configured machines), and the
    // DB auto-load stops overwriting it. Persisted to survive a restart; cleared by Reload-from-DB.
    private readonly Dictionary<int, List<Pvs.Core.Feeders.SonyFeederCsv.Entry>> _manualFeeders = new();
    private readonly Dictionary<int, string> _manualFeederLabels = new();

    // Machines the SUPERVISOR has taken out of this lot's run (cell bypassed, mounter down). Their feeders are
    // dropped from _expected, so the checklist and the interlock stop asking for parts nobody is loading.
    // Persisted with the manual feeder lists; cleared per machine, or wholesale by Reload-from-DB.
    private readonly HashSet<int> _skippedMachines = new();
    private readonly Dictionary<(int machine, int feeder), int> _expectedQty = new();  // per-child-board Mount Step (QtyPerUnit); cached with _expected for the forecast rate when the DB is unreachable
    private readonly Dictionary<(int machine, int feeder), PartsOutEvent> _pending = new();
    private readonly object _gate = new();
    private readonly System.Threading.Timer _housekeeping;
    private readonly System.Threading.Timer _autoModelTimer;
    private readonly System.Threading.Timer _syncTimer;
    private readonly System.Threading.Timer _dpcTimer;
    private readonly System.Threading.Timer _startupTimer;   // one-shot: baseline the inventory ~8s after start
    private bool _autoModel;

    /// <summary>Raised with the OUTGOING lot number the moment a lot changes — BEFORE the operator resets the
    /// machines for the next lot (which starts a fresh C1Z report page and destroys the finishing lot's per-feeder
    /// pickup/error data). A listener saves that lot's final machine data to a text file. Fire-and-forget.</summary>
    public event Action<string>? LotFinalizing;

    private string? _lastAutoLot;   // last production lot key we auto-switched to (so a manual override isn't clobbered)
    private string _currentLotNo = "";   // current production lot number (PONumber) — tags records (esp. consumed reels)
    private string? _manualLotNo;        // operator-picked lot (dropdown); overrides auto. null = follow the current running lot.
    private string? _manualLotModel;     // "model|side" the manual lot was chosen for. The lot APPLIES only for that model
                                         // (dormant, not deleted, for other models) so a model flip never destroys the choice.
    // Supervisor-selected model (dropdown). When set, auto-detect does NOT override it — it VERIFIES it against the
    // machine's C3P program once a machine is online. null = follow the machine (auto-detect).
    private (int ProductId, string Name, string Side)? _manualModel;
    // Supervisor-set "running a NON-CANON model" park: PVS has no BOM/feeders for it, so all Canon-only tracking
    // (auto-detect, model-mismatch, feeder verify, StockOut sync, DPC writing, exhaust) stands down and the line
    // shows a clear "non-Canon — tracking paused" state instead of a confusing blank. Serial + the machine's own
    // C1Z per-feeder pickup/error counts (by supply position, no part names) keep flowing. Sticky across restarts.
    private bool _nonCanon;
    private string _modelVerify = "unknown";   // "verified" | "mismatch" | "unverified" (machine offline) | "unknown"
    private string _modelVerifyDetail = "";
    // Per-model feeder map cache ("productId|side" -> feeders), so a model select populates feeders locally even if
    // the DB is unreachable AND the machine is still offline. Filled whenever a model's map is read from the DB.
    private readonly Dictionary<string, List<ModelCacheFeeder>> _feederMaps = new(StringComparer.OrdinalIgnoreCase);
    // Lot-progress tracking: boards produced (live off the last machine) vs the PO target, for the end-of-lot alert.
    private int _lastMachine;             // line output = highest machine number (last on the line)
    private string _lotCountFor = "";     // the lot the panel counter belongs to
    private string _lotSideFor = "";      // the SIDE that count belongs to — a 2-sided lot shares one lot number, so a
                                          // B->A change (same lot no.) is a NEW run and must reset the count too
    // Produced panels for the current lot run are DERIVED, not incremented: LotPanels = _m4PanelsTotal - _lotAnchorTotal.
    // The anchor is captured (from the persisted monotonic M4 total) when a lot/side run starts, so a restart or a
    // model flip recomputes the count from the machine's own board total instead of losing a resettable counter.
    private long _lotAnchorTotal;         // _m4PanelsTotal at this lot-run's start
    private int? _lotTarget;              // PO target boards (from DeliveryDocuments)
    private int _lotExtra;                // supervisor-added extra boards (local only)
    private const int LotEndThreshold = 10;
    // Material coverage: pieces ISSUED (StockOuts) per part for the current lot/side — to warn when the issued
    // reels won't cover the whole lot (a re-request is needed, which has lead time). Refreshed on change + on a timer.
    private readonly Dictionary<string, int> _lotIssued = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Pvs.Core.Data.IssuedReel> _lotIssuedReels = new();   // individual issued reels (for the staged view)
    private readonly List<Pvs.Core.Data.IssuedReel> _lineReels = new();        // EVERY reel at this line, any lot
    // DailyProductionCount writer: when enabled, PVS is the line's production-count source and appends an
    // incremental board-count row every 5 min. Boards are bucketed by the (lot,model,side) they were produced
    // under — one DB row per bucket per window — so a changeover mid-window never misattributes boards, and
    // enabling the flag only writes boards produced FROM THEN ON (not the whole monotonic history).
    private long _m4PanelsTotal;          // monotonic panels off the last machine (never reset) — lot-count anchor base
    private readonly Pvs.Core.Runtime.ProductionCountLedger _dpc = new();   // unwritten panels, keyed by production context
    private readonly HashSet<string> _shiftTriggerDone = new();   // shift keys whose shift-change trigger has fired or been satisfied
    private readonly Pvs.Core.Shifts.ShiftSchedule _shifts;

    private PartsChangeSession? _change;
    private FullScanSession? _scan;
    private RecountSession? _recount;
    private ModelChangeSession? _modelChange;
    private string _lastMessage = "";
    private DateTime _lastActivity = DateTime.Now;
    private VerifyCompletion? _lastShiftScan;
    private VerifyCompletion? _lastLotEnd;
    private VerifyCompletion? _lastModelChange;

    /// <summary>Pending parts-outs older than this are dropped (assumed handled the old way).</summary>
    public TimeSpan PartsOutTtl { get; set; } = TimeSpan.FromMinutes(20);
    /// <summary>An incomplete session with no operator activity for this long is auto-cleared.</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(5);

    public SessionCoordinator(
        IReelPartRepository repo,
        IEnumerable<MachineChannel> channels,
        ILineRecordSink records,
        LineConfig config,
        ILogger log,
        Pvs.LineApp.Inventory.FeederReelStore reels,
        Pvs.LineApp.Inventory.RemainingStore remaining)
    {
        _repo = repo;
        _channels = channels.ToDictionary(c => c.Machine);
        _records = records;
        _config = config;
        _log = log;
        _reels = reels;
        _remaining = remaining;
        _shifts = config.ToShiftSchedule();
        LoadModelCache();     // restore the last model so the UI isn't stuck if serial/DB is down at startup
        _dpc.SlotOf = DpcSlot;   // one DPC bucket per shift/day slot, so a row never straddles 07:35 / 19:35 / midnight (audit H9)
        LoadDpcState();       // restore the monotonic M4 total FIRST (the lot count is derived from it) + DPC counters
        LoadMachineTally();   // restore per-machine tally offsets (HMI / C1Z corrections) so a re-baseline keeps them (audit H6)
        LoadLotProgress();    // restore the lot anchor so a restart mid-lot recomputes the count (needs _m4PanelsTotal)
        LoadManualLot();      // restore the operator's manual lot choice so it survives a restart
        LoadFeederMapCache(); // per-model feeder maps (local copy of ProductBOM) — BEFORE the manual list, whose forced loads take counts from it
        LoadManualFeeders();  // restore any pen-drive feeder-list overrides (they win over the model cache)
        LoadManualModel();    // restore a supervisor-pinned model so a restart doesn't drop back to auto-detect
        LoadNonCanon();       // restore the non-Canon park so a restart doesn't resume Canon tracking on a non-Canon run
        LoadScanProgress();   // restore an in-progress check so a restart never loses scan progress
        LoadCheckStatus();    // restore the last shift/lot/model-change completion so the monitor GREEN survives a restart
        _calibration = LoadCalibration();   // restore the learned per-part exhaust-drift (shadow accuracy signal)

        foreach (var ch in _channels.Values)
            ch.PartsOutDetected += OnPartsOut;

        // Serial machines drive the lot count + the manual-machine decrement (a manual/JUKI channel never fires a
        // board-complete of its own).
        var serialMachines = _config.Machines.Where(m => m.IsSerial).Select(m => m.Machine)
            .Where(m => _channels.ContainsKey(m)).OrderBy(m => m).ToList();

        // count completed panels off the LAST SERIAL machine (line output) toward the current lot
        _lastMachine = serialMachines.Count > 0 ? serialMachines.Max() : _channels.Keys.DefaultIfEmpty(0).Max();
        if (_channels.TryGetValue(_lastMachine, out var lastCh))
            lastCh.BoardCompleted += _ => OnLotBoardComplete();

        // Manual (non-serial, e.g. Line-3 JUKI) machines get no board-complete of their own — but the same boards
        // pass through them on the inline line. So as long as the line is producing, decrement their feeders off a
        // serial machine's board-completes (M2/M3/M4 are equivalent — Danial). Reference = the first serial machine.
        var manualMachines = _channels.Keys.Where(m => !serialMachines.Contains(m)).ToList();
        if (manualMachines.Count > 0 && serialMachines.Count > 0 &&
            _channels.TryGetValue(serialMachines.First(), out var refCh))
            refCh.BoardCompleted += _ =>
            {
                foreach (var mm in manualMachines)
                    if (_channels.TryGetValue(mm, out var mc)) mc.Inventory.OnBoardComplete();
            };

        _housekeeping = new System.Threading.Timer(_ => { Housekeep(); CheckShiftTrigger(); }, null,
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        // Model auto-detect / C3P program refresh. Every 3 MIN (was 30 s) — a changeover happens a few times a
        // shift, not every 30 s, and the frequent C3P was flooding the serial and stepping on C1M/C1Z report reads
        // (serial pipeline: C3P is low-priority housekeeping). The C1Z rotation still self-requests C3P on demand
        // when a machine's program is unknown, so a fresh program name is never starved.
        _autoModelTimer = new System.Threading.Timer(_ => { _ = AutoDetectModelAsync(); }, null,
            TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(3));
        // Every 5 min (first run after 2 min): record live remaining to remaining.json AND, when enabled,
        // mirror those balances to StockOuts.Quantity in the DB.
        _syncTimer = new System.Threading.Timer(_ => { _ = RecordAndSyncAsync(); }, null,
            TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5));
        // Every 5 min: when enabled, append PVS's incremental board count to DailyProductionCount (one row per
        // lot/model/side produced in the window). PVS is the line's production-count source when this is on.
        _dpcTimer = new System.Threading.Timer(_ => { _ = FlushProductionCountAsync(); }, null,
            TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        // One-shot ~8s after start (model restored from cache by then): baseline the live inventory so the exhaust
        // forecast is NEVER empty after a restart/changeover — it pulls each loaded reel's remaining from the DB by
        // UID (authoritative), no manual refresh or check needed. Fixes "exhaust not showing" after a restart.
        _startupTimer = new System.Threading.Timer(async _ =>
        {
            try { if (Model is not null) await RefreshInventoryAsync(); }
            catch (Exception ex) { _log.LogDebug(ex, "Startup inventory baseline failed."); }
            try { await SeedDpcForCurrentLotAsync(); }
            catch (Exception ex) { _log.LogDebug(ex, "DPC seed failed."); }
        }, null, TimeSpan.FromSeconds(8), System.Threading.Timeout.InfiniteTimeSpan);
    }

    /// <summary>True when the current model was auto-detected from the production system (not manually picked).</summary>
    public bool AutoModel { get { lock (_gate) { return _autoModel; } } }

    /// <summary>
    /// Auto-load the line's running model+side. PREFERS the machine's own loaded program read via C3P
    /// (real-time, exact); falls back to the production system's current lot when no machine answers.
    /// Skips while a check is in progress, and won't clobber a manual override (only switches on a real change).
    /// </summary>
    public async Task AutoDetectModelAsync(CancellationToken ct = default)
    {
        if (NonCanon) return;   // parked on a non-Canon model — never auto-pin or verify a Canon model
        try
        {
            string? model = null, side = null, source = null;
            string? machineModel = null, machineSide = null;   // machine C3P program only (for verifying a pinned model)

            // 1) machine program via C3P (from any machine that answered a prior query). A SKIPPED machine is out
            // of the run, so its program must never drive auto-detect OR the pinned-model verify (no false mismatch
            // from a cell the supervisor deliberately left on a different program).
            foreach (var ch in _channels.Values)
            {
                if (IsMachineSkipped(ch.Machine)) continue;
                if (ParseProgram(ch.ProgramName) is (string m, string s))
                { machineModel = m; machineSide = s; model = m; side = s; source = $"machine {ch.Machine} program"; break; }
            }
            // 2) production system's current lot — used only as the MODEL fallback when no machine answered C3P.
            CurrentLot? lot = null;
            try { lot = await _repo.GetCurrentLotAsync(_config.LineId, ct); } catch { /* DB unreachable */ }
            if (model is null && lot is not null) { model = lot.Model; side = lot.Side?.Trim().ToUpperInvariant() == "B" ? "B" : "A"; source = "production lot"; }

            // C3P (program name) is FIXED for the whole lot — nobody changes a machine's program mid-lot — so we
            // only ASK when we don't yet know it (startup, or just after a changeover: the machine coming back
            // online clears the cached name, see MachineChannel R1OL). Once known it is never re-polled; the model
            // verify above reuses the cached name. This kills the pointless every-3-min C3P that was stepping on
            // the C1M/C1Z report reads on the shared serial line.
            foreach (var ch in _channels.Values)
                if (string.IsNullOrWhiteSpace(ch.ProgramName)) ch.RequestProgram();

            // --- SUPERVISOR-PINNED MODEL: never auto-switch away from it; instead VERIFY it against the machine ---
            (int ProductId, string Name, string Side)? pinned; lock (_gate) pinned = _manualModel;
            if (pinned is not null)
            {
                await VerifyAndTrackPinnedModelAsync(pinned.Value, machineModel, machineSide, lot, ct);
                return;
            }

            if (model is null) return;
            side ??= "A";

            // If the model/side changed, reset the lot COUNTER (the old count/target belonged to the previous model;
            // carrying them over applies the new model's panel factor to the old count and can fire a false "lot end").
            // Two guards keep this from wiping a supervisor's manual lot on a restart:
            //  1) act ONLY on a MACHINE-CONFIRMED change (machineModel via C3P) — right after a restart the model is
            //     briefly detected from the DPC lot/cache before C3P answers; that settling transition is NOT a change.
            //  2) A same-MODEL side flip keeps _manualLotNo (dormant, can reactivate). A change to a DIFFERENT model
            //     drops it (a lot is model-specific), so an old model's lot never lingers on the new model.
            bool modelChanged = false, droppedLot = false;
            lock (_gate)
            {
                if (!_config.SupervisorLotOnly && machineModel is not null && _lastAutoLot is not null && _lastAutoLot != (model + "|" + side))
                {
                    modelChanged = true;
                    _currentLotNo = ""; _lotCountFor = ""; _lotAnchorTotal = _m4PanelsTotal; _lotExtra = 0; _lotTarget = null;
                    droppedLot = DropManualLotIfOtherModel(model);
                }
            }
            if (modelChanged) SaveLotProgress();
            if (droppedLot) SaveManualLot();

            // 3) the running LOT number is chosen MANUALLY from the dropdown (persisted _manualLotNo, tagged with its
            //    model). It applies only for THAT model; for any other model it is dormant and PVS falls back to the
            //    lot the production system says is running now (DailyProductionCount) so it still mirrors reality.
            try
            {
                string? po = null;
                lock (_gate)
                {
                    if (ManualLotApplies(model, side)) po = _manualLotNo;
                }
                if (!_config.SupervisorLotOnly && po is null && lot is not null && !string.IsNullOrWhiteSpace(lot.LotNo)
                    && string.Equals(lot.Model?.Trim(), model, StringComparison.OrdinalIgnoreCase))
                    po = lot.LotNo;   // (SupervisorLotOnly off) default to the production lot ONLY when it is for THIS model (not a stale cross-model lot)
                if (!string.IsNullOrWhiteSpace(po)) lock (_gate) { _currentLotNo = po!; }
            }
            catch { /* DB unreachable — keep last lot */ }
            await RefreshLotAsync(ct);   // reset counter + fetch PO target when the lot changes
            string lotKey = model + "|" + side;
            lock (_gate)
            {
                if (HasActiveSession) return;      // never switch mid-check
                if (_lastAutoLot == lotKey) return; // unchanged -> leave a manual override alone
            }
            var products = await _repo.GetProductsAsync(ct);
            var prod = products.FirstOrDefault(p => string.Equals(p.Name?.Trim(), model, StringComparison.OrdinalIgnoreCase));
            if (prod is null) { _log.LogInformation("Auto-detect: model '{Model}' ({Src}) not in Products.", model, source); return; }
            await SelectModelAsync(prod.ProductId, side, ct);
            lock (_gate) { _autoModel = true; _lastAutoLot = lotKey; }
            await RefreshInventoryAsync(ct);   // baseline exhaust forecast for the new model
            _log.LogInformation("Auto-detected model {Model} ({Side}) from {Src} on line {Line}.", model, side, source, _config.LineId);
        }
        catch (Exception ex) { _log.LogDebug(ex, "Auto model-detect failed."); }
    }

    /// <summary>
    /// While a supervisor has pinned a model, PVS does NOT auto-switch. This keeps the pinned model loaded,
    /// tracks its lot exactly like auto mode, and VERIFIES it against the machine's C3P program once a machine
    /// is online (verified / mismatch / unverified) — surfaced on the monitor so a wrong pin is caught.
    /// </summary>
    private async Task VerifyAndTrackPinnedModelAsync(
        (int ProductId, string Name, string Side) pinned, string? machineModel, string? machineSide, CurrentLot? lot, CancellationToken ct)
    {
        // 1) verify the pin against the machine program (if any machine answered)
        lock (_gate)
        {
            if (machineModel is null)
            { _modelVerify = "unverified"; _modelVerifyDetail = "no machine online yet — cannot verify"; }
            else if (string.Equals(machineModel, pinned.Name, StringComparison.OrdinalIgnoreCase)
                     && string.Equals(machineSide ?? "A", pinned.Side, StringComparison.OrdinalIgnoreCase))
            { _modelVerify = "verified"; _modelVerifyDetail = $"machine program matches ({machineModel} {machineSide})"; }
            else
            { _modelVerify = "mismatch"; _modelVerifyDetail = $"machine is running {machineModel} {machineSide} — PVS is pinned to {pinned.Name} {pinned.Side}"; }
        }

        // 2) make sure the pinned model's feeders are actually loaded (e.g. after a restart, or if the first
        //    load happened while the DB was down) — but never mid-check.
        bool loaded, busy;
        lock (_gate)
        {
            loaded = Model?.ProductId == pinned.ProductId
                     && string.Equals(Side, pinned.Side, StringComparison.OrdinalIgnoreCase) && _expected.Count > 0;
            busy = HasActiveSession;
        }
        if (!loaded && !busy)
        {
            await SelectModelAsync(pinned.ProductId, pinned.Side, ct);
            await RefreshInventoryAsync(ct);
        }
        lock (_gate) _autoModel = false;

        // 3) track the lot for the pinned model (manual lot when it is tagged for this model, else the production lot)
        try
        {
            string? po = null;
            lock (_gate) { if (ManualLotApplies(pinned.Name, pinned.Side)) po = _manualLotNo; }
            // SupervisorLotOnly (default): NEVER fall back to the production system's lot — that stale DB lot was
            // overwriting a supervisor's fresh pick every cycle, making a same-model lot change "sticky" (you had to
            // force-end to get past it). Only mirror the production lot when the line is explicitly NOT supervisor-only.
            if (!_config.SupervisorLotOnly && po is null && lot is not null && !string.IsNullOrWhiteSpace(lot.LotNo)
                && string.Equals(lot.Model?.Trim(), pinned.Name, StringComparison.OrdinalIgnoreCase))
                po = lot.LotNo;
            if (!string.IsNullOrWhiteSpace(po)) lock (_gate) { _currentLotNo = po!; }
        }
        catch { /* DB unreachable — keep last lot */ }
        await RefreshLotAsync(ct);
    }

    /// <summary>Parse a Sony program name like "L307_L313 - B SIDE _Cell4.PW4" into (model, side). Null if no model token.</summary>
    private static (string model, string side)? ParseProgram(string? program)
    {
        if (string.IsNullOrWhiteSpace(program)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(program, @"L\d{3}", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        string side = System.Text.RegularExpressions.Regex.IsMatch(program, @"B\s*SIDE", System.Text.RegularExpressions.RegexOptions.IgnoreCase) ? "B" : "A";
        return (m.Value.ToUpperInvariant(), side);
    }

    public Product? Model { get; private set; }
    public string Side { get; private set; } = "";
    public bool HasActiveSession => _change is not null || _scan is not null || _recount is not null || _modelChange is not null;

    /// <summary>The last completed shift-change scan (for the monitor), or null.</summary>
    public VerifyCompletion? LastShiftScan { get { lock (_gate) { return _lastShiftScan; } } }
    /// <summary>The last completed lot-end scan (for the monitor), or null.</summary>
    public VerifyCompletion? LastLotEnd { get { lock (_gate) { return _lastLotEnd; } } }
    /// <summary>The last completed model-change verification (for the monitor), or null.</summary>
    public VerifyCompletion? LastModelChange { get { lock (_gate) { return _lastModelChange; } } }

    public Task<IReadOnlyList<Product>> GetModelsAsync(CancellationToken ct = default) => _repo.GetProductsAsync(ct);

    // ---- model selection (with change diff) ----

    public async Task<string> SelectModelAsync(int productId, string side, CancellationToken ct = default)
    {
        var products = await _repo.GetProductsAsync(ct);
        var product = products.FirstOrDefault(p => p.ProductId == productId);
        if (product is null) return $"Model {productId} not found.";

        // Read the feeder map with a hard timeout; tolerate a flaky DB by keeping the cached map instead of blanking.
        Dictionary<(int, int), string>? fresh = null;
        var freshQty = new Dictionary<(int, int), int>();
        var map = await TryReadFeederMapAsync(productId, side, 8, ct);
        if (map is not null)
        {
            fresh = new Dictionary<(int, int), string>();
            foreach (var f in map.Where(f => f.Position.IsAssigned && f.Machine > 0 && f.Position.Number is int && IsUsableMachine(f.Machine)))
            {
                fresh[(f.Machine, f.Position.Number!.Value)] = f.PartNumber;
                freshQty[(f.Machine, f.Position.Number!.Value)] = f.QtyPerUnit;
            }
        }

        lock (_gate)
        {
            if (fresh is null)
            {
                // DB unreachable — populate from the per-model feeder-map cache (local copy of ProductBOM) for
                // THIS model+side, so a model select still works offline / with the machine down.
                if (_feederMaps.TryGetValue(MapKey(productId, side), out var cachedMap) && cachedMap.Count > 0)
                {
                    fresh = new Dictionary<(int, int), string>();
                    foreach (var f in cachedMap)
                    {
                        if (!IsUsableMachine(f.Machine)) continue;
                        fresh[(f.Machine, f.Feeder)] = f.Part;
                        if (f.QtyPerUnit > 0) freshQty[(f.Machine, f.Feeder)] = f.QtyPerUnit;
                    }
                    // fall through and apply `fresh` exactly like a DB read
                }
                else
                {
                    bool cached = Model?.ProductId == productId &&
                                  string.Equals(Side, side, StringComparison.OrdinalIgnoreCase) && _expected.Count > 0;
                    if (!cached) return $"{product.Name} ({side}): no feeder map (DB unreachable and not cached) — not loaded.";
                    Model = product; Side = side; _autoModel = false;
                    return $"{product.Name} ({side}): DB unreachable — kept cached {_expected.Count} feeders.";
                }
            }

            bool same = Model?.ProductId == productId &&
                        string.Equals(Side, side, StringComparison.OrdinalIgnoreCase) && _expected.Count > 0;
            string result;
            if (same)
            {
                int added = fresh.Keys.Count(k => !_expected.ContainsKey(k));
                int removed = _expected.Keys.Count(k => !fresh.ContainsKey(k));
                int changed = fresh.Count(kv => _expected.TryGetValue(kv.Key, out var old) &&
                                                !string.Equals(old, kv.Value, StringComparison.OrdinalIgnoreCase));
                result = (added + removed + changed) == 0
                    ? $"{product.Name} ({side}): no changes — {fresh.Count} feeders."
                    : $"{product.Name} ({side}): UPDATED — {changed} changed, {added} added, {removed} removed ({fresh.Count} feeders).";
            }
            else result = $"Model {product.Name} ({side}) loaded — {fresh.Count} feeders mapped.";

            if (_manualFeeders.Count == 0)   // MANUAL MODE: supervisor-loaded feeders override the DB — keep them
            {
                _expected.Clear();
                foreach (var kv in fresh) _expected[kv.Key] = kv.Value;
                _expectedQty.Clear();
                foreach (var kv in freshQty) _expectedQty[kv.Key] = kv.Value;
            }
            Model = product;
            Side = side;
            _autoModel = false;   // a manual (or auto) select; AutoDetect re-flags it afterwards
            // Cache this model+side's feeder map (from ProductBOM) so it can be re-populated offline later — the DB
            // read itself, never the manual/skipped-filtered _expected (that wrote a pen-drive list or a map missing
            // a skipped machine into the offline cache; audit H3/M7).
            _feederMaps[MapKey(productId, side)] = fresh
                .Select(kv => new ModelCacheFeeder(kv.Key.Item1, kv.Key.Item2, kv.Value,
                    freshQty.TryGetValue(kv.Key, out var q) ? q : 0)).ToList();
            SaveModelCache();      // remember the model + feeder map so a restart isn't stuck with no model
            SaveFeederMapCache();  // persist the per-model feeder map for offline model selection
            // If a check is in progress, refresh ITS checklist from the updated map (mid-check ProductBOM amendment)
            // — keeps already-scanned feeders whose part didn't change, resumes AT the amended feeder.
            if (_modelChange is not null || _scan is not null)
            {
                var syncList = _expected.Select(kv => (kv.Key.machine, kv.Key.feeder, kv.Value)).ToList();
                if (_modelChange is not null) _lastMessage = _modelChange.SyncFeeders(syncList).Message;
                else if (_scan is not null) _lastMessage = _scan.SyncFeeders(syncList).Message;
                SaveScanProgress();
            }
            return result;
        }
    }

    // ---- supervisor-selected model (badge-gated override, verified against the machine) ----

    /// <summary>The supervisor-pinned model+side, or null when following the machine (auto-detect).</summary>
    public (int ProductId, string Name, string Side)? ManualModel { get { lock (_gate) return _manualModel; } }
    /// <summary>Verification of the pinned model vs the machine's C3P program: verified | mismatch | unverified | unknown.</summary>
    public string ModelVerify { get { lock (_gate) return _modelVerify; } }
    public string ModelVerifyDetail { get { lock (_gate) return _modelVerifyDetail; } }

    private static string ManualModelPath => System.IO.Path.Combine(AppContext.BaseDirectory, "manual-model.json");
    private sealed record ManualModelData(int ProductId, string Name, string Side);

    private void SaveManualModel()
    {
        (int ProductId, string Name, string Side)? mm; lock (_gate) mm = _manualModel;
        try
        {
            if (mm is null) { if (System.IO.File.Exists(ManualModelPath)) System.IO.File.Delete(ManualModelPath); }
            else System.IO.File.WriteAllText(ManualModelPath,
                System.Text.Json.JsonSerializer.Serialize(new ManualModelData(mm.Value.ProductId, mm.Value.Name, mm.Value.Side)));
        }
        catch (Exception ex) { _log.LogDebug(ex, "Manual model save failed."); }
    }

    private void LoadManualModel()
    {
        try
        {
            if (!System.IO.File.Exists(ManualModelPath)) return;
            var d = System.Text.Json.JsonSerializer.Deserialize<ManualModelData>(
                System.IO.File.ReadAllText(ManualModelPath), new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (d is not null) { _manualModel = (d.ProductId, d.Name, d.Side); _modelVerify = "unverified"; _modelVerifyDetail = "restored — pending machine verification"; }
        }
        catch (Exception ex) { _log.LogDebug(ex, "Manual model load failed."); }
    }

    /// <summary>Supervisor pins the running model+side from the dropdown (REQUIRES an L2+ badge). Populates the
    /// feeders immediately (from DB or the local per-model cache — works even with the machine offline) and stops
    /// auto-detect from switching away; PVS then VERIFIES the choice against the machine's C3P program once online.</summary>
    public async Task<string> SelectModelManualAsync(int productId, string side, string badgeUid, CancellationToken ct = default)
    {
        var badge = await _repo.FindBadgeAsync(badgeUid ?? "", ct);
        if (badge is null || !badge.CanReleaseInterlock) return "Scan a SUPERVISOR badge (L2+) to set the model.";
        // A supervisor setting the model is an explicit override, so it SUPERSEDES any in-progress check rather
        // than being refused — otherwise a shift-change re-scan started for the OLD/stale model deadlocks the
        // change (can't finish the check because the model is wrong, can't fix the model because a check is live).
        // The L2+ badge verified above is the authority for cancelling that check.
        bool supersededCheck;
        lock (_gate)
        {
            supersededCheck = HasActiveSession;
            if (supersededCheck)
            {
                _change = null; _scan = null; _recount = null; _modelChange = null;
                _lastMessage = "In-progress check superseded by a supervisor model change.";
            }
        }
        if (supersededCheck) SaveScanProgress();   // clear the saved progress file for the cancelled check

        var msg = await SelectModelAsync(productId, side, ct);   // load feeders (DB or cache)
        if (Model?.ProductId != productId)
            return msg;   // could not load (e.g. no map, DB down + uncached) — don't pin a model with no feeders

        bool droppedLot;
        lock (_gate)
        {
            _manualModel = (productId, Model?.Name ?? side, side);
            _autoModel = false;
            _modelVerify = "unverified";
            _modelVerifyDetail = "pinned — waiting for a machine to come online to verify";
            _lastAutoLot = null;   // let auto-detect re-run its verify/lot pass against the pinned model
            // A lot is model-specific — pinning a DIFFERENT model drops the old model's lot (else it lingers/shows).
            droppedLot = DropManualLotIfOtherModel(Model?.Name);
        }
        SaveManualModel();
        if (droppedLot) { SaveManualLot(); SaveLotProgress(); await RefreshLotAsync(ct); }
        await _records.WriteAsync(new VerificationRecord(DateTime.Now, _config.LineName, "ModelSelect", 0, 0,
            $"{Model?.Name} {side}", Supervisor: badge.Name, Overridden: true,
            Note: supersededCheck ? "superseded an in-progress check" : null, LotNo: _currentLotNo), ct);
        _ = AutoDetectModelAsync(ct);   // immediate verify + lot refresh against the machine
        return (supersededCheck ? "Cancelled the in-progress check. " : "")
            + $"{Model?.Name} ({side}) pinned by {badge.Name}. {msg}";
    }

    /// <summary>True when the supervisor has parked the line as running a NON-CANON model (no Canon BOM/tracking).</summary>
    public bool NonCanon { get { lock (_gate) return _nonCanon; } }

    private static string NonCanonPath => System.IO.Path.Combine(AppContext.BaseDirectory, "non-canon.flag");
    private void SaveNonCanon()
    {
        try
        {
            bool on; lock (_gate) on = _nonCanon;
            if (on) System.IO.File.WriteAllText(NonCanonPath, "1");
            else if (System.IO.File.Exists(NonCanonPath)) System.IO.File.Delete(NonCanonPath);
        }
        catch (Exception ex) { _log.LogDebug(ex, "Non-canon flag save failed."); }
    }
    private void LoadNonCanon()
    {
        try { if (System.IO.File.Exists(NonCanonPath)) lock (_gate) _nonCanon = true; }
        catch (Exception ex) { _log.LogDebug(ex, "Non-canon flag load failed."); }
    }

    /// <summary>Supervisor parks the line as running a NON-CANON model (REQUIRES an L2+ badge). Clears the pinned
    /// model, stops auto-detect from re-pinning, and stands down all Canon-only tracking — while serial and the
    /// machine's own C1Z pickup/error counts keep flowing. Clear it with <see cref="ClearManualModelAsync"/>.</summary>
    public async Task<string> SetNonCanonAsync(string badgeUid, CancellationToken ct = default)
    {
        var badge = await _repo.FindBadgeAsync(badgeUid ?? "", ct);
        if (badge is null || !badge.CanReleaseInterlock) return "Scan a SUPERVISOR badge (L2+) to set non-Canon.";
        bool superseded;
        lock (_gate)
        {
            superseded = HasActiveSession;
            if (superseded) { _change = null; _scan = null; _recount = null; _modelChange = null; }
            _nonCanon = true;
            _manualModel = null;
            _autoModel = false;
            Model = null;                                 // no Canon model tracked
            _modelVerify = "na"; _modelVerifyDetail = "non-Canon model — PVS tracking paused";
            _currentLotNo = ""; _lotCountFor = ""; _lotSideFor = ""; _manualLotNo = null;
        }
        foreach (var ch in _channels.Values) ch.Inventory.Clear();   // drop any stale feeders
        if (superseded) SaveScanProgress();
        SaveNonCanon(); SaveManualModel(); SaveManualLot();
        await _records.WriteAsync(new VerificationRecord(DateTime.Now, _config.LineName, "NonCanon", 0, 0,
            "non-Canon model", Supervisor: badge.Name, Overridden: true, Note: "tracking paused (non-Canon)", LotNo: ""), ct);
        _log.LogInformation("Line set to NON-CANON by {Sup} — Canon tracking paused.", badge.Name);
        return $"Line set to NON-CANON (tracking paused) by {badge.Name}. Machine pickup data still shows by feeder position.";
    }

    /// <summary>Supervisor releases the manual pin and returns the line to auto-detect (REQUIRES an L2+ badge).</summary>
    public async Task<string> ClearManualModelAsync(string badgeUid, CancellationToken ct = default)
    {
        var badge = await _repo.FindBadgeAsync(badgeUid ?? "", ct);
        if (badge is null || !badge.CanReleaseInterlock) return "Scan a SUPERVISOR badge (L2+) to return to auto.";
        lock (_gate) { _nonCanon = false; _manualModel = null; _modelVerify = "unknown"; _modelVerifyDetail = ""; _lastAutoLot = null; }
        SaveNonCanon();
        SaveManualModel();
        await _records.WriteAsync(new VerificationRecord(DateTime.Now, _config.LineName, "ModelSelect", 0, 0,
            "(auto)", Supervisor: badge.Name, Overridden: true, LotNo: _currentLotNo), ct);
        _ = AutoDetectModelAsync(ct);
        return $"Returned to auto-detect by {badge.Name}.";
    }

    // ---- model cache (survive a restart / serial or DB outage) ----
    private sealed record ModelCacheData(int ProductId, string Name, string Side, System.Collections.Generic.List<ModelCacheFeeder> Expected);
    private sealed record ModelCacheFeeder(int Machine, int Feeder, string Part, int QtyPerUnit = 0);

    private static string ModelCachePath => System.IO.Path.Combine(AppContext.BaseDirectory, "model-cache.json");

    /// <summary>Persist the loaded model + feeder map locally so PVS still knows the model after a restart
    /// even if the DB/serial is temporarily unreachable. Caller may hold _gate (re-entrant).</summary>
    private void SaveModelCache()
    {
        try
        {
            ModelCacheData data;
            lock (_gate)
            {
                if (Model is null) return;
                data = new ModelCacheData(Model.ProductId, Model.Name, Side ?? "A",
                    _expected.Select(kv => new ModelCacheFeeder(kv.Key.machine, kv.Key.feeder, kv.Value,
                        _expectedQty.TryGetValue(kv.Key, out var q) ? q : 0)).ToList());
            }
            System.IO.File.WriteAllText(ModelCachePath,
                System.Text.Json.JsonSerializer.Serialize(data, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { _log.LogDebug(ex, "Model cache save failed."); }
    }

    /// <summary>Load the cached model + feeder map at startup (before auto-detect), so the UI works offline.</summary>
    private void LoadModelCache()
    {
        try
        {
            if (!System.IO.File.Exists(ModelCachePath)) return;
            var data = System.Text.Json.JsonSerializer.Deserialize<ModelCacheData>(
                System.IO.File.ReadAllText(ModelCachePath), new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (data is null || data.Expected is null || data.Expected.Count == 0) return;
            Model = new Product(data.ProductId, data.Name);
            Side = data.Side;
            _expected.Clear();
            _expectedQty.Clear();
            foreach (var f in data.Expected)
            {
                if (!IsUsableMachine(f.Machine)) continue;
                _expected[(f.Machine, f.Feeder)] = f.Part;
                if (f.QtyPerUnit > 0) _expectedQty[(f.Machine, f.Feeder)] = f.QtyPerUnit;
            }
            _log.LogInformation("Restored cached model {Model} ({Side}), {N} feeders — before serial/DB auto-detect.", data.Name, data.Side, _expected.Count);
        }
        catch (Exception ex) { _log.LogDebug(ex, "Model cache load failed."); }
    }

    // ---- per-model feeder-map cache (a local copy of ProductBOM per model+side) ----
    private static string MapKey(int productId, string side) =>
        productId + "|" + ((side ?? "").Trim().ToUpperInvariant() == "B" ? "B" : "A");
    private static string FeederMapCachePath => System.IO.Path.Combine(AppContext.BaseDirectory, "feedermap-cache.json");

    /// <summary>Persist the per-model feeder maps so selecting a model populates feeders locally even when the
    /// DB is unreachable and the machine is still offline. Filled whenever a model's map is read from ProductBOM.</summary>
    private void SaveFeederMapCache()
    {
        Dictionary<string, List<ModelCacheFeeder>> snap;
        lock (_gate) snap = new Dictionary<string, List<ModelCacheFeeder>>(_feederMaps);
        try { System.IO.File.WriteAllText(FeederMapCachePath, System.Text.Json.JsonSerializer.Serialize(snap)); }
        catch (Exception ex) { _log.LogDebug(ex, "Feeder-map cache save failed."); }
    }

    private void LoadFeederMapCache()
    {
        try
        {
            if (!System.IO.File.Exists(FeederMapCachePath)) return;
            var d = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, List<ModelCacheFeeder>>>(
                System.IO.File.ReadAllText(FeederMapCachePath), new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (d is null) return;
            _feederMaps.Clear();
            foreach (var kv in d) if (kv.Value is not null) _feederMaps[kv.Key] = kv.Value;
            _log.LogInformation("Loaded feeder-map cache: {N} model/side sets.", _feederMaps.Count);
        }
        catch (Exception ex) { _log.LogDebug(ex, "Feeder-map cache load failed."); }
    }

    // ---- lot progress (boards produced vs PO target; end-of-lot alert) ----
    private sealed record LotProgressData(string LotNo, int Panels, int Extra, int? Target, long? AnchorTotal = null, string? Side = null);
    private static string LotProgressPath => System.IO.Path.Combine(AppContext.BaseDirectory, "lot-progress.json");

    // ---- per-machine tally OFFSETS (audit H6) ----------------------------------------------------------------
    // A machine's board tally is corrected by the operator's HMI key-in, by a C1Z tally sync (apply), or by the
    // lot-end recalc — but every re-baseline (shift check, model-change check, restart, manual refresh) reseeded the
    // tally to the M4-derived lot count, throwing the correction away; the next sync then re-applied the same delta
    // (double decrement). Keep each machine's correction as an OFFSET from the lot count (tally − lot panels at the
    // moment of the correction), persist it per lot, and seed = lot panels + offset at every baseline. Offsets are
    // cleared at lot change / force-end (the tally restarts at 0 for the new run).
    private static string MachineTallyPath => System.IO.Path.Combine(AppContext.BaseDirectory, "machine-tally.json");
    private sealed record MachineTallyData(string LotNo, Dictionary<int, int> Offsets);
    private readonly Dictionary<int, int> _tallyOffset = new();   // guarded by _gate

    /// <summary>Record machine <paramref name="machine"/>'s tally as an offset from the lot count (caller: right after
    /// a correction). Persisted so it survives a restart and every re-baseline of the same lot.</summary>
    private void NoteTallyOffset(int machine, int boardsApplied)
    {
        string lot; lock (_gate)
        {
            lot = _currentLotNo;
            int lotPanels = (!string.IsNullOrWhiteSpace(lot) && _lotCountFor == lot) ? LotPanels() : 0;
            _tallyOffset[machine] = boardsApplied - lotPanels;
        }
        SaveMachineTally();
    }

    private void ClearTallyOffsets()
    {
        lock (_gate) _tallyOffset.Clear();
        SaveMachineTally();
    }

    private void SaveMachineTally()
    {
        try
        {
            MachineTallyData d; lock (_gate) d = new MachineTallyData(_currentLotNo, new Dictionary<int, int>(_tallyOffset));
            System.IO.File.WriteAllText(MachineTallyPath, System.Text.Json.JsonSerializer.Serialize(d));
        }
        catch (Exception ex) { _log.LogDebug(ex, "machine-tally save failed."); }
    }

    private void LoadMachineTally()
    {
        try
        {
            if (!System.IO.File.Exists(MachineTallyPath)) return;
            var d = System.Text.Json.JsonSerializer.Deserialize<MachineTallyData>(System.IO.File.ReadAllText(MachineTallyPath));
            if (d is null || d.Offsets is null) return;
            lock (_gate)
            {
                // Offsets belong to ONE lot: keep them only when the restored lot count is for that same lot.
                if (string.IsNullOrWhiteSpace(d.LotNo) || d.LotNo != _lotCountFor) return;
                _tallyOffset.Clear();
                foreach (var kv in d.Offsets) _tallyOffset[kv.Key] = kv.Value;
            }
            _log.LogInformation("Restored machine tally offsets for lot {Lot}: {Offsets}.", d.LotNo,
                string.Join(", ", d.Offsets.Select(kv => $"M{kv.Key}:{kv.Value:+#;-#;0}")));
        }
        catch (Exception ex) { _log.LogDebug(ex, "machine-tally load failed."); }
    }

    /// <summary>Produced panels for the current lot run, DERIVED from the monotonic M4 total. Caller holds _gate.</summary>
    private int LotPanels() => (int)Math.Max(0, _m4PanelsTotal - _lotAnchorTotal);

    /// <summary>Live inputs the Daiya Graph needs (hourly line-out grid, first/last board, operator roster).</summary>
    public Pvs.Core.Runtime.DaiyaLog Daiya { get; } = new();

    /// <summary>Header values for the Daiya Graph, read atomically: model, side, lot (PO), target, per-panel.</summary>
    public (string Model, string Side, string Lot, int? Target, int PerPanel) DaiyaHeader()
    {
        lock (_gate)
            return (Model?.Name ?? "", Side ?? "", _currentLotNo, _lotTarget,
                    Model is not null ? _config.PanelBoardsFor(Model.Name) : 1);
    }

    private void OnLotBoardComplete()
    {
        lock (_gate)
        {
            // Monotonic M4 total (the lot count is DERIVED from this: LotPanels = total - anchor).
            _m4PanelsTotal++;
            Daiya.OnBoard(1, DateTime.Now);   // line-out board -> Daiya Graph hourly grid + first/last board
            // Bucket this panel under the lot/model/side it was produced under (for the DPC writer). Only when
            // there IS a lot+model context — boards produced before a lot is selected aren't attributable.
            // Only while PVS IS the count source: with the flag off the operator app writes the rows, so
            // accumulating here would just build a backlog that double-counts that shift if it ever flushed.
            if (_config.WriteProductionCount)
                _dpc.AddPanel(new Pvs.Core.Runtime.ProductionKey(_currentLotNo, Model?.Name ?? "", Side ?? ""), DateTime.Now);
        }
        // Persist the monotonic total every board so the derived lot count is restart-accurate to the last board.
        SaveDpcState();
    }

    // ---- DailyProductionCount writer (PVS as the line's production-count source; config-gated) ----
    // The unwritten-panel accounting lives in Pvs.Core.Runtime.ProductionCountLedger (pure + unit-tested);
    // this half is only the persistence and the DB write.
    private sealed record DpcBucketRow(string Lot, string Model, string Side, long Panels, DateTime FirstAt);
    private sealed record DpcStateData(long M4PanelsTotal, List<DpcBucketRow>? Pending = null);
    private static string DpcStatePath => System.IO.Path.Combine(AppContext.BaseDirectory, "dpc-state.json");
    // ONE shift clock: the configured shiftTimes (LineConfig -> ShiftSchedule). The DPC label, the DPC slot, the
    // Daiya sheet and the shift-check trigger all derive from it — they used to hard-code 07:35/19:35 while
    // ShiftKey (check status, health, uptime, shift email) used the config's 07:30/19:30 (audit M1, 2026-09-07).
    private string DpcShift(DateTime t) => _shifts.DpcName(t);

    /// <summary>Start of the DPC shift/day slot a board belongs to: the shift instance start, or 00:00 for a night
    /// shift's post-midnight tail (a new calendar day = its own row, matching WindowFor's day cap).</summary>
    private DateTime DpcSlot(DateTime t) => _shifts.SlotStart(t);

    private void SaveDpcState()
    {
        long total; lock (_gate) total = _m4PanelsTotal;
        var d = new DpcStateData(total,
            _dpc.Snapshot().Select(b => new DpcBucketRow(b.Key.Lot, b.Key.Model, b.Key.Side, b.Panels, b.FirstAt)).ToList());
        try { System.IO.File.WriteAllText(DpcStatePath, System.Text.Json.JsonSerializer.Serialize(d)); }
        catch (Exception ex) { _log.LogDebug(ex, "DPC state save failed."); }
    }

    private void LoadDpcState()
    {
        try
        {
            if (!System.IO.File.Exists(DpcStatePath)) return;
            var d = System.Text.Json.JsonSerializer.Deserialize<DpcStateData>(
                System.IO.File.ReadAllText(DpcStatePath), new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (d is null) return;
            _m4PanelsTotal = d.M4PanelsTotal;
            // Unwritten panels survive a restart so the next flush neither re-writes nor loses them. With the
            // writer OFF the operator app owns the rows, so anything left over is dropped rather than flushed
            // later as one impossible lump; the ledger also drops buckets left from an earlier shift.
            long dropped = _config.WriteProductionCount
                ? _dpc.Restore((d.Pending ?? new())                    // older state files (no Pending) restore empty
                        .Select(r => new Pvs.Core.Runtime.ProductionBucket(
                            new Pvs.Core.Runtime.ProductionKey(r.Lot, r.Model, r.Side), r.Panels, r.FirstAt)),
                    DateTime.Now)
                : _dpc.Clear();
            if (dropped > 0)
                _log.LogWarning("DPC: discarded {Panels}p of stale unwritten backlog at startup (not written — that shift's rows were not PVS's to write).", dropped);
        }
        catch (Exception ex) { _log.LogDebug(ex, "DPC state load failed."); }
    }

    private DateTime? _lastProdWriteAt;
    private DateTime? _lastProdWriteError;
    /// <summary>When the last DailyProductionCount row was committed (null = none since start).</summary>
    public DateTime? LastProductionWriteAt => _lastProdWriteAt;
    /// <summary>When a DailyProductionCount write last THREW (null = none). This is the write-path failure the
    /// SELECT-1 DB heartbeat cannot see — a line can show "DB connected" while production writes silently fail.</summary>
    public DateTime? LastProductionWriteError => _lastProdWriteError;
    /// <summary>Unwritten production still queued: total panels and the oldest bucket's first-board time.</summary>
    public (long Panels, DateTime? Oldest) PendingProduction()
    {
        long p = 0; DateTime? oldest = null;
        foreach (var b in _dpc.Snapshot())
        {
            if (b.Panels <= 0) continue;
            p += b.Panels;
            if (oldest is null || b.FirstAt < oldest) oldest = b.FirstAt;
        }
        return (p, oldest);
    }

    /// <summary>
    /// Appends DailyProductionCount rows for the boards produced since the last write (config-gated by
    /// WriteProductionCount) — one row per (lot,model,side) bucket, Quantity = panels × per-panel boards,
    /// the window running from that bucket's first unwritten board to now, attributed to the shift at write
    /// time. The quantity is an INCREMENT off the ledger, never a lot total, and the window is clamped so a
    /// row can never start after it ends. A bucket is cleared only after its row is confirmed written, so a DB
    /// failure just retries next window (no loss, no double-write). Nothing to flush -> no-op.
    /// </summary>
    public async Task FlushProductionCountAsync(CancellationToken ct = default)
    {
        if (!_config.WriteProductionCount || NonCanon) return;   // non-Canon: no Canon lot/model to attribute a count to
        DateTime winEnd = DateTime.Now;
        var windows = _dpc.Due(winEnd);

        foreach (var w in windows)
        {
            var key = w.Key;
            int pp = _config.PanelBoardsFor(key.Model);
            long panelsToWrite = w.Panels;

            // SAFETY NET: a lot's DPC total must never exceed what PVS actually produced for it. The ledger
            // itself can no longer over-count (one panel per real board, forward-only), so this is now purely a
            // guard against another writer holding rows for the same lot. For the CURRENT lot, cap the write to
            // the room left under produced (produced − already-recorded); the excess stays in the bucket and
            // only ever writes if real production grows into it.
            string curLot; long curPanels;
            lock (_gate) { curLot = _currentLotNo; curPanels = (_lotCountFor == _currentLotNo) ? Math.Max(0, _m4PanelsTotal - _lotAnchorTotal) : 0; }
            if (string.Equals(key.Lot, curLot, StringComparison.OrdinalIgnoreCase) && curPanels > 0)
            {
                int recordedBoards = await _repo.GetProducedBoardsForLotAsync(key.Lot, key.Side, _config.LineId, ct);
                long roomPanels = Math.Max(0, curPanels - (recordedBoards / pp));
                if (panelsToWrite > roomPanels) panelsToWrite = roomPanels;
            }
            int boards = (int)(panelsToWrite * pp);
            if (boards <= 0) continue;   // nothing writable within the cap this window (excess held back)
            // Date the row to the bucket's OWN day/shift (w.Start), not the flush time — so production held on the
            // line through a DB outage is written back under the day it was actually made, never the day it flushed.
            var entry = new ProductionCountEntry(
                w.Start.ToString("yyyy-MM-dd"), w.Start.ToString("HH:mm:ss"), w.End.ToString("HH:mm:ss"),
                key.Model, key.Side, boards, _config.LineId.ToString(), key.Lot, DpcShift(w.Start), "PVS (auto)", "PVS", Guid.NewGuid().ToString());
            try
            {
                int rows = await _repo.InsertProductionCountAsync(entry, ct);
                if (rows > 0)
                {
                    // Subtract exactly what we wrote; boards produced during the write stay for the next flush.
                    _dpc.Commit(w, panelsToWrite, winEnd);   // the exact bucket this row came from (audit H9)
                    SaveDpcState();
                    _lastProdWriteAt = DateTime.Now;
                    _log.LogInformation("DPC row: line {Line} {Shift} {Lot} {Model}/{Side} +{Boards} boards ({Start}-{End}).",
                        _config.LineId, DpcShift(w.Start), key.Lot, key.Model, key.Side, boards,
                        w.Start.ToString("HH:mm:ss"), w.End.ToString("HH:mm:ss"));
                }
            }
            catch (Exception ex) { _lastProdWriteError = DateTime.Now; _log.LogWarning(ex, "DailyProductionCount write failed (kept for retry)."); }
        }
    }

    /// <summary>
    /// One-shot at startup (when PVS is the DPC writer): REPORTS the gap between what the current lot has
    /// produced and what DailyProductionCount holds for it. It does not write, and deliberately does not feed
    /// the gap into the ledger.
    /// <para>
    /// It used to seed the difference into the pending bucket, which is what produced the 2026-08-05 Line 5
    /// row: with nothing yet recorded for the lot, the "difference" IS the whole lot-to-date (30p/120 boards),
    /// and it was stamped with the seed instant, so the first flush 5 minutes later reported 120 boards in a
    /// 5-minute window. The gap is real, but its time window is not knowable — those boards were produced
    /// before PVS was the count source, over hours nobody recorded — and any lot the operator app already
    /// keyed in under a different lot string would be double-counted. So it is LOGGED and AUDITED for a
    /// supervisor to correct by hand, never invented into a window. The writer is forward-only.
    /// </para>
    /// </summary>
    public async Task SeedDpcForCurrentLotAsync(CancellationToken ct = default)
    {
        if (!_config.WriteProductionCount) return;
        string lot, model, side; int perPanel; long lotPanels;
        lock (_gate)
        {
            lot = _currentLotNo; model = Model?.Name ?? ""; side = Side ?? "";
            if (string.IsNullOrWhiteSpace(lot) || string.IsNullOrWhiteSpace(model)) return;
            perPanel = _config.PanelBoardsFor(model);
            lotPanels = (_lotCountFor == _currentLotNo) ? Math.Max(0, _m4PanelsTotal - _lotAnchorTotal) : 0;
        }
        if (lotPanels <= 0 || perPanel <= 0) return;
        int recordedBoards = await _repo.GetProducedBoardsForLotAsync(lot, side, _config.LineId, ct);
        long recordedPanels = recordedBoards / perPanel;             // floor: partial panel stays owed
        // Panels already in the ledger are owed and WILL be written — only the rest is a genuine gap.
        long gapPanels = Math.Max(0, lotPanels - recordedPanels - _dpc.PanelsFor(new Pvs.Core.Runtime.ProductionKey(lot, model, side)));
        if (gapPanels <= 0) return;
        _log.LogWarning("DPC gap: lot {Lot} {Model}/{Side} produced {Prod}p, recorded {Rec}p — {Gap}p ({Boards} boards) predate PVS as the count source and will NOT be written.",
            lot, model, side, lotPanels, recordedPanels, gapPanels, gapPanels * perPanel);
        Audit(new VerificationRecord(DateTime.Now, _config.LineName, "DpcGap", 0, 0,
            $"{recordedPanels}p recorded, {gapPanels}p unrecorded (not written)", Supervisor: "auto",
            Quantity: (int)(gapPanels * perPanel), Note: $"{side} startup gap", LotNo: lot));
    }

    private void SaveLotProgress()
    {
        LotProgressData d;
        lock (_gate) d = new LotProgressData(_lotCountFor, LotPanels(), _lotExtra, _lotTarget, _lotAnchorTotal, _lotSideFor);
        try { System.IO.File.WriteAllText(LotProgressPath, System.Text.Json.JsonSerializer.Serialize(d)); }
        catch (Exception ex) { _log.LogDebug(ex, "Lot progress save failed."); }
    }

    private void LoadLotProgress()
    {
        try
        {
            if (!System.IO.File.Exists(LotProgressPath)) return;
            var d = System.Text.Json.JsonSerializer.Deserialize<LotProgressData>(
                System.IO.File.ReadAllText(LotProgressPath), new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (d is null) return;
            _lotCountFor = d.LotNo ?? ""; _lotExtra = d.Extra; _lotTarget = d.Target; _lotSideFor = d.Side ?? "";
            // Prefer the persisted anchor; migrate an OLD file (no anchor) by back-computing it from its saved Panels.
            bool migrated = d.AnchorTotal is null;
            _lotAnchorTotal = d.AnchorTotal ?? (_m4PanelsTotal - d.Panels);
            // Persist the anchor NOW so a migrated old file is upgraded to the new format immediately — otherwise its
            // stale Panels could mis-migrate if a board completes (advancing the M4 total) before the next lot change.
            if (migrated) SaveLotProgress();
        }
        catch (Exception ex) { _log.LogDebug(ex, "Lot progress load failed."); }
    }

    // ---- Manual lot selection (operator picks the running lot from a dropdown; persisted, overrides auto) ----
    private sealed record ManualLotData(string? LotNo, string? Model = null);   // Model = "model|side" the lot is for
    private static string ManualLotPath => System.IO.Path.Combine(AppContext.BaseDirectory, "selected-lot.json");

    /// <summary>True when the supervisor's manual lot should drive THIS model+side. It is tagged with the model it
    /// was chosen for and stays dormant (not deleted) for others, so a model flip never loses the choice. An
    /// untagged legacy selection applies to any model (re-tagged on the next selection). Caller holds _gate.</summary>
    private bool ManualLotApplies(string? model, string? side) =>
        !string.IsNullOrWhiteSpace(_manualLotNo) &&
        (string.IsNullOrWhiteSpace(_manualLotModel) ||
         string.Equals(_manualLotModel, (model ?? "") + "|" + (side ?? ""), StringComparison.OrdinalIgnoreCase));

    /// <summary>Drop a manual lot that belongs to a DIFFERENT model than <paramref name="newModel"/> — a lot is
    /// model-specific, so on a real model change (e.g. L261→L313) the old lot must not carry over. A same-model
    /// side change keeps it (dormant, can reactivate). Clears the lot counter too. Caller holds _gate. Returns
    /// true if it dropped anything (so the caller persists).</summary>
    private bool DropManualLotIfOtherModel(string? newModel)
    {
        if (_manualLotModel is null) return false;
        if (_manualLotModel.StartsWith((newModel ?? "") + "|", StringComparison.OrdinalIgnoreCase)) return false;
        _manualLotNo = null; _manualLotModel = null;
        _currentLotNo = ""; _lotCountFor = ""; _lotSideFor = ""; _lotAnchorTotal = _m4PanelsTotal; _lotExtra = 0; _lotTarget = null;
        return true;
    }

    private void SaveManualLot()
    {
        string? lot, tag; lock (_gate) { lot = _manualLotNo; tag = _manualLotModel; }
        try { System.IO.File.WriteAllText(ManualLotPath, System.Text.Json.JsonSerializer.Serialize(new ManualLotData(lot, tag))); }
        catch (Exception ex) { _log.LogDebug(ex, "Manual lot save failed."); }
    }

    private void LoadManualLot()
    {
        try
        {
            if (!System.IO.File.Exists(ManualLotPath)) return;
            var d = System.Text.Json.JsonSerializer.Deserialize<ManualLotData>(
                System.IO.File.ReadAllText(ManualLotPath), new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (d is not null && !string.IsNullOrWhiteSpace(d.LotNo)) { _manualLotNo = d.LotNo; _currentLotNo = d.LotNo!; _manualLotModel = d.Model; }
        }
        catch (Exception ex) { _log.LogDebug(ex, "Manual lot load failed."); }
    }

    // ---- scan-progress persistence: a model-change / shift / lot-end check survives a restart, crash or
    //      accidental cancel so the operator continues from the EXACT feeder they were on (no re-scan). ----
    private static string ScanProgressPath => System.IO.Path.Combine(AppContext.BaseDirectory, "scan-progress.json");
    private sealed record ScanProgressData(string Kind, ModelChangeSnapshot? ModelChange, FullScanSnapshot? FullScan);

    /// <summary>Write the active check to disk after every scan-affecting action; delete the file when no check
    /// is running (or it just completed). Called OUTSIDE _gate — snapshots under the lock, writes without it.</summary>
    private void SaveScanProgress()
    {
        ScanProgressData? data = null;
        lock (_gate)
        {
            if (_modelChange is { State: not ModelChangeState.Complete })
                data = new ScanProgressData("ModelChange", _modelChange.Export(), null);
            else if (_scan is { State: not FullScanState.Complete })
                data = new ScanProgressData("FullScan", null, _scan.Export());
        }
        try
        {
            if (data is null)
            {
                if (System.IO.File.Exists(ScanProgressPath)) System.IO.File.Delete(ScanProgressPath);
            }
            else System.IO.File.WriteAllText(ScanProgressPath, System.Text.Json.JsonSerializer.Serialize(data));
        }
        catch (Exception ex) { _log.LogDebug(ex, "Scan progress save failed."); }
    }

    /// <summary>Restore an in-progress check on startup so a restart never loses scan progress.</summary>
    private void LoadScanProgress()
    {
        try
        {
            if (!System.IO.File.Exists(ScanProgressPath)) return;
            var d = System.Text.Json.JsonSerializer.Deserialize<ScanProgressData>(
                System.IO.File.ReadAllText(ScanProgressPath),
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (d is null) return;
            if (d.Kind == "ModelChange" && d.ModelChange is not null)
            {
                _modelChange = ModelChangeSession.Restore(d.ModelChange);
                int done = _modelChange.MatchedCount + _modelChange.ReleasedCount, total = _modelChange.Items.Count;
                _lastMessage = ResumeMessage("Model-change", done, total, _modelChange.State == ModelChangeState.AwaitingBadge,
                    _modelChange.Current?.Machine, _modelChange.Current?.Feeder);
                _log.LogInformation("Restored an in-progress model-change check ({Done}/{Total} feeders done).", done, total);
            }
            else if (d.Kind == "FullScan" && d.FullScan is not null)
            {
                _scan = FullScanSession.Restore(d.FullScan);
                int done = _scan.MatchedCount + _scan.ReleasedCount, total = _scan.Items.Count;
                _lastMessage = ResumeMessage(_scan.Purpose.ToString(), done, total, _scan.State == FullScanState.AwaitingBadge,
                    _scan.Current?.Machine, _scan.Current?.Feeder);
                _log.LogInformation("Restored an in-progress {Purpose} check ({Done}/{Total} feeders done).",
                    _scan.Purpose, done, total);
            }
        }
        catch (Exception ex) { _log.LogDebug(ex, "Scan progress load failed."); }
    }

    private static string ResumeMessage(string label, int done, int total, bool awaitingBadge, int? machine, int? feeder) =>
        awaitingBadge
            ? $"{label} check resumed after restart — badge in to continue ({done}/{total} feeders done)."
            : machine is int m && feeder is int f
                ? $"{label} check resumed after restart — {done}/{total} done. Continue: scan feeder {f} on machine {m}."
                : $"{label} check resumed after restart — {done}/{total} feeders done.";

    /// <summary>Supervisor changes the running lot from the dropdown (REQUIRES an L2+ badge, so it can't be changed
    /// accidentally). Overrides auto, persists the choice and resets the board counter. Pass "" to return to
    /// automatic. Optional <paramref name="producedBoards"/> (>= 0) sets the already-produced board count for the
    /// lot (a correction, e.g. after re-pinning a lot that was already part-way done) instead of resetting to 0.</summary>
    public async Task<string> SelectLotAsync(string lotNo, string badgeUid, int producedBoards = -1, CancellationToken ct = default)
    {
        var badge = await _repo.FindBadgeAsync(badgeUid ?? "", ct);
        if (badge is null || !badge.CanReleaseInterlock) return "Scan a SUPERVISOR badge (L2+) to change the lot.";
        lotNo = (lotNo ?? "").Trim();
        lock (_gate)
        {
            _manualLotNo = string.IsNullOrWhiteSpace(lotNo) ? null : lotNo;
            // Tag the choice with the model it is for, so a later model flip makes it dormant rather than deleting it.
            _manualLotModel = string.IsNullOrWhiteSpace(lotNo) ? null : (Model?.Name ?? "") + "|" + Side;
            if (!string.IsNullOrWhiteSpace(lotNo)) _currentLotNo = lotNo;
        }
        SaveManualLot();
        await RefreshLotAsync(ct);   // resets the board counter to 0 on a lot change
        if (producedBoards >= 0)
        {
            int perPanel = Model is not null ? _config.PanelBoardsFor(Model.Name) : 1;
            // Set the anchor so the derived count STARTS at producedBoards, then keeps counting live off M4.
            int seedPanels;
            lock (_gate)
            {
                _lotAnchorTotal = _m4PanelsTotal - (perPanel > 0 ? (int)Math.Round((double)producedBoards / perPanel) : producedBoards);
                seedPanels = LotPanels();
            }
            SaveLotProgress();
            // The feeders ALREADY reflect those produced boards (they decremented all along), so each machine's tally
            // must start there too — RefreshLotAsync just seeded 0, which made the lot-end recalc / an HMI sync draw
            // those boards' pieces a second time. (Audit finding H7, 2026-09-07.)
            foreach (var ch in _channels.Values) ch.Inventory.SeedBoardsApplied(seedPanels);
        }
        await _records.WriteAsync(new VerificationRecord(DateTime.Now, _config.LineName, "LotChange", 0, 0,
            string.IsNullOrWhiteSpace(lotNo) ? "(auto)" : lotNo, Supervisor: badge.Name, Overridden: true,
            Note: producedBoards >= 0 ? $"lot set via dropdown; produced set to {producedBoards} boards" : "lot set via dropdown",
            LotNo: string.IsNullOrWhiteSpace(lotNo) ? _currentLotNo : lotNo), ct);
        _log.LogInformation("Lot set to {Lot} by {Sup} (produced={P}).", string.IsNullOrWhiteSpace(lotNo) ? "(auto)" : lotNo, badge.Name, producedBoards);

        // PROGRAM-vs-LOT CHECK — the one command PVS deliberately transmits on its own (besides realtime-enable):
        // on a lot set/change, ask each machine which program is loaded (C3P) and verify it matches the model this
        // lot belongs to. Fire the C3P now; a moment later run the detect/verify against the fresh program name.
        // (Minimal-serial: no rotation, no periodic C3P, no auto C1M — PVS is otherwise receive-only.)
        foreach (var ch in _channels.Values) if (!IsMachineSkipped(ch.Machine)) ch.RequestProgram();
        _ = Task.Run(async () => { try { await Task.Delay(2500); await AutoDetectModelAsync(); } catch { } });

        return string.IsNullOrWhiteSpace(lotNo)
            ? $"Lot tracking set to automatic by {badge.Name}."
            : $"Now tracking lot {lotNo} by {badge.Name}" + (producedBoards >= 0 ? $"; produced set to {producedBoards}." : ".");
    }

    /// <summary>
    /// OPEN A LOT FROM A MAGAZINE-SLIP QR (Danial, 2026-09-07: "the QR replaces the badge; the side comes from the
    /// machine program"). The slip (printed by MCS from the DB) carries the PO; PVS takes the lot's model and target
    /// from DeliveryDocuments, the SIDE from the program the machines report, and refuses when the slip's model is
    /// not what the machines are running. A running lot that has NOT reached its target is never switched away by a
    /// stray slip — the slip is refused with the lot's progress (finish it, or a supervisor force-ends it). The same
    /// PO scanned again is a no-op (the caller then just counts the magazine). Audited as LotChange by "slip QR".
    /// </summary>
    public async Task<(bool Ok, bool Started, string Message)> StartLotFromSlipAsync(Pvs.Core.Boards.MagazineSlip slip, CancellationToken ct = default)
    {
        string po = (slip.Po ?? "").Trim();
        if (po.Length == 0) return (false, false, "Slip has no PO.");
        string cur; string? modelName; string side;
        lock (_gate) { cur = _currentLotNo; modelName = Model?.Name; side = Side ?? "A"; }
        if (string.Equals(cur, po, StringComparison.OrdinalIgnoreCase)) return (true, false, $"Lot {po} is already open.");
        if (string.IsNullOrWhiteSpace(modelName))
            return (false, false, "No model detected from the machines yet — wait for the program to be read, then scan the slip again.");
        if (!string.IsNullOrWhiteSpace(cur))
        {
            var (remaining, effTarget) = LotBoards();
            bool complete = remaining is int r && r <= 0;
            if (!complete)
            {
                int produced = (effTarget ?? 0) - (remaining ?? 0);
                return (false, false, $"⚠ Lot {cur} is still running ({produced}/{effTarget?.ToString() ?? "?"} boards) — finish it, or a supervisor force-ends it, before opening {po}.");
            }
        }
        string? lotModel = null;
        try { lotModel = await _repo.GetLotModelAsync(po, ct); } catch (Exception ex) { _log.LogDebug(ex, "lot model lookup failed for {Po}.", po); }
        if (lotModel is null) return (false, false, $"PO {po} not found in DeliveryDocuments — cannot open the lot from this slip.");
        if (!string.Equals(lotModel, modelName, StringComparison.OrdinalIgnoreCase))
            return (false, false, $"⚠ Slip PO {po} is for model {lotModel} but the machines are running {modelName} — wrong slip or wrong program.");

        lock (_gate)
        {
            _manualLotNo = po;
            _manualLotModel = modelName + "|" + side;
            _currentLotNo = po;
        }
        SaveManualLot();
        await RefreshLotAsync(ct);   // finalizes the previous (complete) lot, resets the counter, fetches the target
        int? target; lock (_gate) target = _lotTarget;
        string warn = target is int t && slip.QtyTotal > 0 && t != slip.QtyTotal ? $" (slip qty {slip.QtyTotal} ≠ PO target {t})" : "";
        await _records.WriteAsync(new VerificationRecord(DateTime.Now, _config.LineName, "LotChange", 0, 0, po,
            Supervisor: "slip QR", Overridden: true,
            Note: $"lot opened by magazine-slip scan — model {modelName} side {side}; slip qty {slip.QtyTotal}, mag {slip.MagNo}/{slip.MagTotal}; target {target?.ToString() ?? "?"}",
            LotNo: po), ct);
        _log.LogInformation("Lot {Lot} opened from a magazine slip (model {Model} side {Side}, target {Target}, slip qty {Qty}).", po, modelName, side, target, slip.QtyTotal);
        // Same program-vs-lot check the dropdown path does: ask each machine its program, verify a moment later.
        foreach (var ch in _channels.Values) if (!IsMachineSkipped(ch.Machine)) ch.RequestProgram();
        _ = Task.Run(async () => { try { await Task.Delay(2500); await AutoDetectModelAsync(); } catch { } });
        return (true, true, $"Lot {po} opened from slip — {modelName} {side} side, target {target?.ToString() ?? "?"} boards{warn}.");
    }

    /// <summary>Candidate lots for the dropdown (Planned delivery orders for the current model) + the selected lot.</summary>
    public async Task<object> LotOptionsAsync(CancellationToken ct = default)
    {
        string? model; string selected; bool manual;
        lock (_gate) { model = Model?.Name; selected = _currentLotNo; manual = _manualLotNo is not null; }
        var list = new List<object>();
        if (!string.IsNullOrWhiteSpace(model))
        {
            try
            {
                foreach (var o in await _repo.GetLotOptionsAsync(model!, _config.ReopenLots, ct))
                    list.Add(new { lotNo = o.PoNumber, target = o.Target, deliveryDate = o.DeliveryDate?.ToString("yyyy-MM-dd") });
            }
            catch { /* DB down — empty list */ }
        }
        return new { selected, manual, options = list };
    }

    /// <summary>When the lot OR side changes, reset the board counter and fetch the new lot's PO target. Called each
    /// auto-detect / pinned-model track. A 2-sided lot shares one lot number, so a B->A side change is a NEW run.</summary>
    public async Task RefreshLotAsync(CancellationToken ct = default)
    {
        string lot, side; lock (_gate) { lot = _currentLotNo; side = Side ?? ""; }
        if (string.IsNullOrWhiteSpace(lot)) return;
        string outgoing; bool changed;
        lock (_gate)
        {
            changed = lot != _lotCountFor || !string.Equals(side, _lotSideFor, StringComparison.OrdinalIgnoreCase);
            outgoing = _lotCountFor;   // the lot whose count is about to reset
        }
        if (!changed) return;   // same lot AND side — keep the running count
        // Before the operator resets the machines for the new lot, preserve the FINISHING lot's per-feeder
        // machine data (C1Z pickup/error counts). The reset destroys it otherwise (same reason the C1M count is
        // read on a heartbeat). Fire-and-forget so the lot change is never delayed.
        if (!string.IsNullOrWhiteSpace(outgoing) && outgoing != lot) { RecordLotUsageAtFinalize(outgoing); LotFinalizing?.Invoke(outgoing); }
        int? target = null;
        try { target = await _repo.GetLotTargetAsync(lot, ct); } catch { /* DB down — leave target null */ }
        lock (_gate) { _lotCountFor = lot; _lotSideFor = side; _lotAnchorTotal = _m4PanelsTotal; _lotExtra = 0; _lotTarget = target; }
        // New run: every machine's per-lot board tally starts from zero. The lot-end recalc above may have just snapped
        // the tally to the FINISHED lot's size; leaving it there inflates the next lot's usage check and would make a
        // later HMI sync / lot-end recalc hand pieces BACK. Seeding never touches a feeder's remaining.
        foreach (var ch in _channels.Values) ch.Inventory.SeedBoardsApplied(0);
        lock (_gate) _tallyLast.Clear();   // machine-tally results belong to the finished lot
        ClearTallyOffsets();               // corrections belonged to the finished lot too (audit H6)
        SaveLotProgress();
        _log.LogInformation("Lot changed to {Lot}; target {Target} boards; board counter reset.", lot, target);
    }

    /// <summary>Supervisor adds extra boards to the running lot (local only). Returns a status message.</summary>
    public async Task<string> AddLotQtyAsync(int extraBoards, string badgeUid, CancellationToken ct = default)
    {
        var badge = await _repo.FindBadgeAsync(badgeUid ?? "", ct);
        if (badge is null || !badge.CanReleaseInterlock) return "Scan a SUPERVISOR badge (L2+) to add lot quantity.";
        if (extraBoards <= 0) return "Enter a quantity greater than 0.";
        string lot; lock (_gate) { _lotExtra += extraBoards; lot = _lotCountFor; }
        SaveLotProgress();
        await _records.WriteAsync(new VerificationRecord(DateTime.Now, _config.LineName, "LotQtyAdd", 0, 0,
            $"+{extraBoards} boards", Supervisor: badge.Name, Quantity: extraBoards, Overridden: true,
            Note: "extra qty added to lot", LotNo: lot), ct);
        _log.LogInformation("Lot {Lot}: +{N} boards by {Sup}.", lot, extraBoards, badge.Name);
        return $"Added {extraBoards} boards to lot {lot} by {badge.Name}.";
    }

    /// <summary>Live lot progress: boards produced (panels × per-panel, off the last machine) vs target + extra.</summary>
    public object LotProgress()
    {
        lock (_gate)
        {
            // No active lot (e.g. right after a model change), or the counter belongs to a different lot than the
            // current one -> report nothing, so a stale count can't raise a false end-of-lot alert.
            if (string.IsNullOrWhiteSpace(_currentLotNo) || _lotCountFor != _currentLotNo)
                return new { lotNo = "" };
            int perPanel = Model is not null ? _config.PanelBoardsFor(Model.Name) : 1;
            int lotPanels = LotPanels();
            int produced = lotPanels * perPanel;
            int? effTarget = _lotTarget is int t ? t + _lotExtra : (int?)null;
            int? remaining = effTarget is int e ? Math.Max(0, e - produced) : (int?)null;
            return new
            {
                lotNo = _lotCountFor,
                target = _lotTarget,
                extra = _lotExtra,
                effectiveTarget = effTarget,
                producedBoards = produced,
                panels = lotPanels,
                perPanel,
                lastMachine = _lastMachine,
                remaining,
                endingSoon = remaining is int r && r <= LotEndThreshold,
                // INTEGRITY alarm: produced has passed the effective target. Surfaces an inflated count (e.g. a
                // bad machine adoption) immediately on the floor instead of it landing silently in the DB.
                overTarget = effTarget is int et && produced > et,
                overBy = effTarget is int et2 ? Math.Max(0, produced - et2) : 0
            };
        }
    }

    /// <summary>Re-query the per-part ISSUED totals (StockOuts) for the current lot/side. Cheap query on a timer /
    /// on lot change; picks up reels issued mid-lot. Read-only — never writes anything.</summary>
    public async Task RefreshLotCoverageAsync(CancellationToken ct = default)
    {
        string lot, side; int line = _config.LineId;
        lock (_gate) { lot = _currentLotNo; side = Side ?? ""; }
        if (string.IsNullOrWhiteSpace(lot)) { lock (_gate) { _lotIssued.Clear(); _lotIssuedReels.Clear(); } return; }
        try
        {
            var reels = await _repo.GetIssuedReelsForLotAsync(lot, side, line, ct);   // one query -> reel list + per-part sum
            lock (_gate)
            {
                _lotIssuedReels.Clear(); _lotIssuedReels.AddRange(reels);
                _lotIssued.Clear();
                foreach (var g in reels.GroupBy(r => r.PartNumber, StringComparer.OrdinalIgnoreCase))
                    _lotIssued[g.Key] = g.Sum(x => x.Qty);
            }
        }
        catch (Exception ex) { _log.LogDebug(ex, "Lot coverage refresh failed (kept last)."); }

        // Separately, EVERY reel at this line regardless of lot. A reel issued under a previous lot is still
        // physically on the rack, and the operator needs to see it — scoping this by lot hid two full reels
        // from a feeder an hour off empty (Line 1 F120, 2026-08-07).
        try
        {
            var atLine = await _repo.GetReelsAtLineAsync(line, ReelsAtLineDays, ct);
            lock (_gate) { _lineReels.Clear(); _lineReels.AddRange(atLine); }
        }
        catch (Exception ex) { _log.LogDebug(ex, "Reels-at-line refresh failed (kept last)."); }
    }

    /// <summary>How far back to look for reels issued to this line. StockOuts never deletes, so without a
    /// bound long-consumed reels would be reported as available.</summary>
    private const int ReelsAtLineDays = 30;

    /// <summary>Individual reels issued for the current lot/side (cached) — for the "issued but not loaded" view.</summary>
    public IReadOnlyList<Pvs.Core.Data.IssuedReel> LotIssuedReels()
    {
        lock (_gate) return _lotIssuedReels.ToList();
    }

    /// <summary>Every reel issued to this line in the last <see cref="ReelsAtLineDays"/> days that still holds
    /// stock — any lot, any side. This is what is physically on the rack.</summary>
    public IReadOnlyList<Pvs.Core.Data.IssuedReel> ReelsAtLine()
    {
        lock (_gate) return _lineReels.ToList();
    }

    /// <summary>Per-part pieces issued (StockOuts) for the current lot/side — feeds the material-coverage warning.</summary>
    public IReadOnlyDictionary<string, int> LotIssued()
    {
        lock (_gate) return new Dictionary<string, int>(_lotIssued, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Child boards per panel for the current model (1 if unknown).</summary>
    public int PerPanel { get { lock (_gate) return Model is not null ? _config.PanelBoardsFor(Model.Name) : 1; } }

    /// <summary>(boards remaining in the lot, effective target boards) — nulls when no lot is being tracked.</summary>
    public (int? RemainingBoards, int? EffectiveTarget) LotBoards()
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(_currentLotNo) || _lotCountFor != _currentLotNo) return (null, null);
            int perPanel = Model is not null ? _config.PanelBoardsFor(Model.Name) : 1;
            int produced = LotPanels() * perPanel;
            int? eff = _lotTarget is int t ? t + _lotExtra : (int?)null;
            int? rem = eff is int e ? Math.Max(0, e - produced) : (int?)null;
            return (rem, eff);
        }
    }

    /// <summary>True if this machine is configured on this line. Feeders on BOM machines this line does NOT run
    /// (a bypassed cell, or a cell handled off-serial like the JUKI) are excluded from the feeder list — their
    /// parts are covered on a running cell per the BOM, so the used-machine total still reconciles to the Canon
    /// BOM. Keeps the shift-scan checklist from showing phantom feeders for an unused cell.</summary>
    private bool IsConfiguredMachine(int machine) => _config.Machines.Any(m => m.Machine == machine);

    /// <summary>True if this line should expect feeders on this machine: it is on the line (config) OR the
    /// supervisor loaded a pen-drive list for it, AND the supervisor has not skipped it for this run. Config is
    /// the default, but the supervisor on the floor has the last word both ways — see LoadManualFeeders and
    /// SetMachineSkippedAsync.</summary>
    private bool IsUsableMachine(int machine) =>
        !_skippedMachines.Contains(machine) && (IsConfiguredMachine(machine) || _manualFeeders.ContainsKey(machine));

    /// <summary>Is this machine currently skipped (supervisor took it out of the run)? A skipped machine is not
    /// program-verified and never raises a program-mismatch alarm.</summary>
    public bool IsMachineSkipped(int machine) { lock (_gate) return _skippedMachines.Contains(machine); }

    private string? ExpectedFor(int machine, int feeder) =>
        _expected.TryGetValue((machine, feeder), out var p) ? p : null;

    /// <summary>The part expected at a feeder (from the loaded model's feeder map), or null. Used by the
    /// downtime tracker to tag a parts-out recovery with the part that ran out.</summary>
    public string? ExpectedPartAt(int machine, int feeder) { lock (_gate) return ExpectedFor(machine, feeder); }

    /// <summary>The live feeder state (remaining + IsTracked) for a machine, read from the coordinator's channels
    /// — which INCLUDE manual/non-serial machines (e.g. the JUKI). /api/inventory + the exhaust use this so a
    /// manual machine's feeders show a live count and decrement like the serial machines, even with no listener.</summary>
    public Pvs.Core.Inventory.FeederState? FeederStateOf(int machine, int feeder) =>
        _channels.TryGetValue(machine, out var ch) ? ch.Inventory.Get(feeder) : null;

    /// <summary>The tracked model's name, or null (for health: is its boards-per-panel factor configured?).</summary>
    public string? ModelName { get { lock (_gate) return Model?.Name; } }

    /// <summary>Every machine number the coordinator tracks (serial + manual).</summary>
    public IReadOnlyList<int> TrackedMachines() => _channels.Keys.OrderBy(m => m).ToList();

    /// <summary>Every tracked feeder state across ALL machines (serial + manual/JUKI) — for the exhaust forecast,
    /// so a manual machine's loaded feeders show a time-to-run-out too (using the line's board rate).</summary>
    public IReadOnlyList<(int Machine, Pvs.Core.Inventory.FeederState State)> AllFeederStates()
    {
        var list = new List<(int, Pvs.Core.Inventory.FeederState)>();
        foreach (var kv in _channels)
            foreach (var fs in kv.Value.Inventory.Feeders)
                list.Add((kv.Key, fs));
        return list;
    }

    /// <summary>
    /// Checks a feeder's live remaining against a parts-out: a genuine exhaust means the reel is empty, so
    /// if the machine-inventory estimate still shows well above one cycle, the parts-out is probably a FALSE
    /// call (jam / pickup error). Returns the remaining and whether it looks false. Threshold: &gt; 3 cycles
    /// or &gt; 50 pieces. Null remaining when the feeder isn't tracked.
    /// </summary>
    private (int? Remaining, bool ProbableFalse) FeederRemainingCheck(int machine, int feeder)
    {
        var fs = _channels.TryGetValue(machine, out var ch) ? ch.Inventory.Get(feeder) : null;
        if (fs is not { IsTracked: true }) return (null, false);
        int rem = fs.Remaining;
        return (rem, rem > Math.Max(fs.MountedPerBoard * 3, 50));
    }

    /// <summary>The current model's feeders (machine, feeder, expected part) — for the manual part-change picker.</summary>
    public IReadOnlyList<(int Machine, int Feeder, string Part)> FeederList()
    {
        lock (_gate)
            return _expected.Select(kv => (kv.Key.machine, kv.Key.feeder, kv.Value))
                .OrderBy(x => x.machine).ThenBy(x => x.feeder).ToList();
    }

    // ---- manual feeder lists (pen-drive CSV override, for a breakdown reshuffle) ----

    private static string ManualFeedersPath => System.IO.Path.Combine(AppContext.BaseDirectory, "manual-feeders.json");

    /// <summary>Rebuild _expected from the loaded manual feeder maps (skipped machines excluded). Caller holds the
    /// lock. A machine WITH a pen-drive list uses that list (no per-board qty → 1 placement × panels). A usable
    /// machine WITHOUT one keeps the DB map for the current model/side (from the per-model cache) — a pen-drive
    /// load is one file per machine, so between files the list must not shrink to the machines loaded so far:
    /// that starved the live inventory of every other machine and, with off-list auto-unload, would have taken
    /// their reels off the feeders. (Audit finding H3, 2026-09-07.) A machine the supervisor has SKIPPED
    /// contributes nothing either way.</summary>
    private void RebuildExpectedFromManual()
    {
        _expected.Clear();
        _expectedQty.Clear();
        List<ModelCacheFeeder>? dbMap = null;
        if (Model is not null) _feederMaps.TryGetValue(MapKey(Model.ProductId, Side ?? "A"), out dbMap);
        if (dbMap is not null)
            foreach (var f in dbMap)
            {
                if (!IsUsableMachine(f.Machine) || _manualFeeders.ContainsKey(f.Machine)) continue;
                _expected[(f.Machine, f.Feeder)] = f.Part;
                if (f.QtyPerUnit > 0) _expectedQty[(f.Machine, f.Feeder)] = f.QtyPerUnit;
            }
        // Each pen-drive row's PLACEMENT COUNT comes from the file (Sony Mount Step / JUKI QTY). A file without it
        // is rejected at load unless a supervisor forced it; a forced row falls back to the DB feeder map for this
        // model/side (same machine+feeder when the part agrees, else the same part anywhere — a reshuffle moved
        // it), and only then to 1 per board — logged. Rows at 1 per board are what drained L1 on 2026-09-07 (12 of
        // 27 feeders really placed 8–76 per panel; reels ran out while PVS showed thousands; ~55k pcs false attrition).
        var unmatched = new List<string>();
        foreach (var (machine, entries) in _manualFeeders)
        {
            if (!IsUsableMachine(machine)) continue;
            foreach (var e in entries)
            {
                _expected[(machine, e.Feeder)] = e.Part;
                int qty = e.Qty;
                if (qty <= 0 && dbMap is not null)
                {
                    var same = dbMap.FirstOrDefault(f => f.Machine == machine && f.Feeder == e.Feeder && Pvs.Core.Verification.PartNumber.Matches(f.Part, e.Part));
                    var hit = same ?? dbMap.FirstOrDefault(f => Pvs.Core.Verification.PartNumber.Matches(f.Part, e.Part));
                    qty = hit?.QtyPerUnit ?? 0;
                }
                if (qty > 0) _expectedQty[(machine, e.Feeder)] = qty;
                else unmatched.Add($"M{machine} F{e.Feeder} {e.Part}");
            }
        }
        if (unmatched.Count > 0)
            _log.LogWarning("Manual feeder list: NO placement count for {N} feeder(s) (not in the file, not in the DB feeder map) — decrementing at 1 per board: {List}",
                unmatched.Count, string.Join(", ", unmatched));
    }

    /// <summary>Load one machine's feeder list from a Sony CSV (pen drive). Supervisor only. The SUPERVISOR picks
    /// the target machine — a breakdown reshuffle moves feeders onto whatever cell is running, so any machine 1-8
    /// is allowed, config or not. A file whose declared Cell differs from the chosen machine is refused with
    /// CELL-MISMATCH unless <paramref name="force"/> says the supervisor meant it (that choice is audited).
    /// Overrides the DB for that machine until Reload-from-DB.</summary>
    public async Task<string> LoadManualFeedersAsync(int machine, string csv, Badge badge, bool force = false, CancellationToken ct = default)
    {
        if (!badge.CanReleaseInterlock) return "Scan a SUPERVISOR badge (L2+) to load a feeder list.";
        if (machine < 1 || machine > MaxMachine) return $"Machine {machine} is out of range (1-{MaxMachine}).";
        var entries = Pvs.Core.Feeders.SonyFeederCsv.Parse(csv, machine);
        if (entries.Count == 0) return "No feeders found in that file.";
        int? declared = Pvs.Core.Feeders.SonyFeederCsv.DeclaredCell(csv);
        if (declared is int fm && fm != machine && !force)
            return $"CELL-MISMATCH: that file is for Cell {fm}, not Machine {machine}.";
        // REJECT a file in a format that carries no placement count (Danial 2026-09-07: "reject the different format
        // from the feeder list in the Parts Control PC"). The Sony export has it as Mount Step, the JUKI/Canon export
        // as QTY; anything else would track every feeder at 1 per board. A supervisor may force it (audited), and the
        // counts then come from the DB feeder map.
        int noQty = entries.Count(e => e.Qty <= 0);
        if (noQty > 0 && !force)
            return $"REJECTED: {noQty} of {entries.Count} rows have no placement count (Mount Step / QTY column) — that is not the feeder-list format from the Parts Control PC. Load the correct feeder list; a supervisor may force this one (counts then come from the DB feeder map).";
        var label = Pvs.Core.Feeders.SonyFeederCsv.Comment(csv) ?? $"file ({entries.Count})";
        bool wasSkipped;
        lock (_gate)
        {
            _manualFeeders[machine] = entries.ToList();
            _manualFeederLabels[machine] = label;
            wasSkipped = _skippedMachines.Remove(machine);   // loading a list puts the machine back in the run
            RebuildExpectedFromManual();
        }
        SaveManualFeeders();
        // The list changed → the live inventory must follow it NOW (it is only ever built in RefreshInventoryAsync);
        // otherwise the old feeders keep decrementing / forecasting / syncing until the next baseline. (Audit H3)
        try { await RefreshInventoryAsync(ct); } catch (Exception ex) { _log.LogDebug(ex, "Inventory refresh after manual feeder load failed."); }
        string note = (declared is int d && d != machine ? $"pen-drive CSV (Cell {d} -> M{machine})" : "pen-drive CSV")
                    + (noQty > 0 ? $"; FORCED with {noQty} row(s) lacking a placement count (DB feeder map used)" : "");
        Audit(new VerificationRecord(DateTime.Now, _config.LineName, "ManualFeederLoad", entries.Count, 0,
            $"M{machine}: {label}", Supervisor: badge.Name, Overridden: true, Note: note, LotNo: _currentLotNo));
        return $"Machine {machine}: loaded {entries.Count} feeders ({label})."
             + (declared is int d2 && d2 != machine ? $" NOTE: file is Cell {d2}." : "")
             + (wasSkipped ? " Machine is back in the line." : "");
    }

    /// <summary>Highest machine number a supervisor may load or skip. Sony lines run 4 cells; the extra headroom
    /// lets a line with more mounters than the config lists still be driven from the floor.</summary>
    private const int MaxMachine = 8;

    /// <summary>Supervisor takes a machine OUT of this run (bypassed cell / mounter down), or puts it back.
    /// A skipped machine contributes no expected feeders, so the shift checklist and the parts interlock stop
    /// asking for parts nobody is loading. Supervisor only, and audited both ways.</summary>
    public async Task<string> SetMachineSkippedAsync(int machine, bool skip, Badge badge, CancellationToken ct = default)
    {
        if (!badge.CanReleaseInterlock) return "Scan a SUPERVISOR badge (L2+) to skip a machine.";
        if (machine < 1 || machine > MaxMachine) return $"Machine {machine} is out of range (1-{MaxMachine}).";
        bool manual, changed; Product? model; string side;
        lock (_gate)
        {
            changed = skip ? _skippedMachines.Add(machine) : _skippedMachines.Remove(machine);
            manual = _manualFeeders.Count > 0;
            model = Model; side = Side ?? "A";
            if (manual) RebuildExpectedFromManual();
        }
        if (!changed) return skip ? $"Machine {machine} is already skipped." : $"Machine {machine} is already in the line.";
        SaveManualFeeders();
        // DB mode: re-select the model so _expected is rebuilt without (or with) the machine. Manual mode already
        // rebuilt from the pen-drive lists above.
        if (!manual && model is not null) await SelectModelAsync(model.ProductId, side, ct);
        // Live inventory follows the new list immediately (a skipped machine's feeders stop decrementing and leave
        // the exhaust card now, not at the next baseline). (Audit H3)
        try { await RefreshInventoryAsync(ct); } catch (Exception ex) { _log.LogDebug(ex, "Inventory refresh after machine skip failed."); }
        Audit(new VerificationRecord(DateTime.Now, _config.LineName, skip ? "MachineSkip" : "MachineUnskip", 0, 0,
            $"M{machine}", Supervisor: badge.Name, Overridden: true,
            Note: skip ? "machine not running this lot" : "machine back in the line", LotNo: _currentLotNo));
        return skip
            ? $"Machine {machine} SKIPPED — its feeders are out of the check until you put it back."
            : $"Machine {machine} is back in the line — its feeders are checked again.";
    }

    /// <summary>Clear ALL manual feeder overrides and go back to the DB ProductBOM. Supervisor only.</summary>
    public async Task<string> ClearManualFeedersAsync(Badge badge, CancellationToken ct = default)
    {
        if (!badge.CanReleaseInterlock) return "Scan a SUPERVISOR badge (L2+) to reload from the DB.";
        int had, skipped; Product? model; string side;
        lock (_gate)
        {
            had = _manualFeeders.Count; _manualFeeders.Clear(); _manualFeederLabels.Clear();
            skipped = _skippedMachines.Count; _skippedMachines.Clear();   // a full reload puts every machine back
            model = Model; side = Side ?? "A";
        }
        SaveManualFeeders();
        if (model is not null) await SelectModelAsync(model.ProductId, side, ct);   // rebuild _expected from DB
        await RefreshInventoryAsync(ct);
        Audit(new VerificationRecord(DateTime.Now, _config.LineName, "ManualFeederClear", 0, 0,
            $"cleared {had} override(s), {skipped} skip(s)", Supervisor: badge.Name, Overridden: true, Note: "reload from DB", LotNo: _currentLotNo));
        return "Feeder lists reloaded from the DB ProductBOM"
             + (skipped > 0 ? $"; {skipped} skipped machine(s) back in the line." : ".");
    }

    /// <summary>Per-machine manual-feeder status for the ⚙ page (which machines are file-loaded, counts, total).
    /// Lists the line's ACTUAL machines (line.config.json) so the floor sees only its real cells — every line is 4
    /// machines. Any machine a supervisor has already loaded or skipped is included too (even if outside the config),
    /// so a breakdown reshuffle onto an unlisted cell still shows up. `onLine` flags the ones the config lists.</summary>
    public object ManualFeederState()
    {
        lock (_gate)
        {
            var relevant = _config.Machines.Select(m => m.Machine)
                .Concat(_manualFeeders.Keys).Concat(_skippedMachines)
                .Where(n => n is >= 1 and <= MaxMachine)
                .Distinct().OrderBy(n => n).ToList();
            var machines = relevant.Select(n =>
            {
                var cfg = _config.Machines.FirstOrDefault(m => m.Machine == n);
                return new
                {
                    machine = n,
                    onLine = cfg is not null,
                    manualCell = cfg?.Manual ?? false,
                    skipped = _skippedMachines.Contains(n),
                    loaded = _manualFeeders.ContainsKey(n),
                    label = _manualFeederLabels.TryGetValue(n, out var l) ? l : null,
                    feeders = _manualFeeders.TryGetValue(n, out var e) ? e.Count : 0
                };
            }).ToList();
            int loadedTotal = _manualFeeders.Where(kv => IsUsableMachine(kv.Key)).Sum(kv => kv.Value.Count);
            return new
            {
                active = _manualFeeders.Count > 0,
                machines,
                loadedTotal,
                skipped = _skippedMachines.OrderBy(m => m).ToArray()
            };
        }
    }

    private void SaveManualFeeders()
    {
        try
        {
            object data;
            lock (_gate) data = new { Feeders = _manualFeeders.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
                                      Labels = _manualFeederLabels.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
                                      Skipped = _skippedMachines.OrderBy(m => m).ToArray() };
            System.IO.File.WriteAllText(ManualFeedersPath, System.Text.Json.JsonSerializer.Serialize(data));
        }
        catch (Exception ex) { _log.LogDebug(ex, "Manual-feeders save failed."); }
    }

    private void LoadManualFeeders()
    {
        try
        {
            if (!System.IO.File.Exists(ManualFeedersPath)) return;
            var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(ManualFeedersPath));
            if (doc.RootElement.TryGetProperty("Feeders", out var fe))
                foreach (var m in fe.EnumerateObject())
                {
                    if (!int.TryParse(m.Name, out int machine)) continue;
                    var list = new List<Pvs.Core.Feeders.SonyFeederCsv.Entry>();
                    foreach (var row in m.Value.EnumerateArray())
                        list.Add(new Pvs.Core.Feeders.SonyFeederCsv.Entry(
                            row.GetProperty("Machine").GetInt32(), row.GetProperty("Feeder").GetInt32(), row.GetProperty("Part").GetString() ?? "",
                            row.TryGetProperty("Qty", out var qv) && qv.TryGetInt32(out int qq) ? qq : 0));
                    if (list.Count > 0) _manualFeeders[machine] = list;
                }
            if (doc.RootElement.TryGetProperty("Labels", out var la))
                foreach (var m in la.EnumerateObject())
                    if (int.TryParse(m.Name, out int machine)) _manualFeederLabels[machine] = m.Value.GetString() ?? "";
            if (doc.RootElement.TryGetProperty("Skipped", out var sk) && sk.ValueKind == System.Text.Json.JsonValueKind.Array)
                foreach (var m in sk.EnumerateArray())
                    if (m.TryGetInt32(out int machine)) _skippedMachines.Add(machine);
            if (_manualFeeders.Count > 0) { RebuildExpectedFromManual(); _log.LogInformation("Restored {N} manual feeder override(s).", _manualFeeders.Count); }
            else if (_skippedMachines.Count > 0)
            {
                // DB mode: the model cache was restored BEFORE the skips were read — drop the skipped machines'
                // feeders now so a restart doesn't resurrect a checklist the supervisor took out.
                foreach (var key in _expected.Keys.Where(k => !IsUsableMachine(k.machine)).ToList())
                { _expected.Remove(key); _expectedQty.Remove(key); }
            }
            if (_skippedMachines.Count > 0) _log.LogInformation("Restored {N} skipped machine(s): {M}.", _skippedMachines.Count, string.Join(",", _skippedMachines.OrderBy(m => m)));
        }
        catch (Exception ex) { _log.LogDebug(ex, "Manual-feeders load failed."); }
    }

    /// <summary>
    /// Manually starts a Mode B parts-change for a chosen machine + feeder — no parts-out event needed
    /// (for a planned reel change, or when a machine's serial link is down). It then follows the normal
    /// exhaust scan flow: operator badge → old reel → new reel → quantity. Expected part comes from the
    /// current model's feeder map (flagged if the feeder isn't in the model).
    /// </summary>
    public string StartManualPartsChange(int machine, int feeder)
    {
        lock (_gate)
        {
            _lastActivity = DateTime.Now;
            if (HasActiveSession) return "Finish or cancel the current task first.";
            if (Model is null) return "Select the running model first.";
            string expected = ExpectedFor(machine, feeder) ?? "(feeder not in model — flag)";
            _change = new PartsChangeSession(machine, feeder, expected);
            _lastMessage = $"Part change — machine {machine}, feeder {feeder} ({expected}). Scan operator badge.";
            return _lastMessage;
        }
    }

    /// <summary>
    /// (Re)baselines the live per-feeder inventory for the current model, so the exhaust forecast has data:
    /// for each assigned feeder it sets part + placements-per-board (ProductBOM.Quantity — confirmed equal
    /// to the machine feeder-list Mount Step for L307), and loads the reel's remaining qty from StockOuts
    /// (by the UID last scanned onto that feeder). From here each board-complete decrements it live.
    /// Called on a model change and after a verification check re-confirms the reels. Feeders with no known
    /// reel are configured but left untracked (excluded from the forecast until a reel is scanned).
    /// </summary>
    /// <summary>Reads the feeder map with a HARD timeout so a stalled parts-control link fails fast to cache
    /// instead of hanging ~40s per call. Returns null on timeout/error.</summary>
    private async Task<IReadOnlyList<Pvs.Core.Data.FeederAssignment>?> TryReadFeederMapAsync(int productId, string side, int seconds, CancellationToken ct)
    {
        // A stalled parts-control link can hang the socket ~40s and ignore token cancellation, so RACE the read
        // against a timer and abandon it on timeout (observe its exception later) rather than waiting it out.
        var dbTask = _repo.GetFeederMapAsync(productId, side, _config.LineId, ct);
        var winner = await Task.WhenAny(dbTask, Task.Delay(TimeSpan.FromSeconds(seconds), ct));
        if (winner == dbTask)
        {
            try { return await dbTask; }
            catch (Exception ex) { _log.LogWarning(ex, "Feeder-map read failed — using cache."); return null; }
        }
        _ = dbTask.ContinueWith(t => { _ = t.Exception; }, TaskScheduler.Default);   // don't leave it unobserved
        _log.LogWarning("Feeder-map read exceeded {S}s (parts-control link stalling) — using cached map.", seconds);
        return null;
    }

    /// <summary>One reel taken off a feeder by an unload (the return manifest row).</summary>
    public sealed record UnloadedReel(int Machine, int Feeder, string Part, string Uid, int Remaining);

    /// <summary>
    /// Whole-line UNLOAD — used at month-end when the lots are finished and all parts return to the store to be
    /// counted in, or when a completely new model is loaded. Takes EVERY reel off the feeders: clears the
    /// feeder→reel mapping (reversible) and stops all feeder tracking immediately. It NEVER writes StockOut — each
    /// reel keeps its remaining count by UID, so the store's StockIn count stays accurate ("removed from the
    /// machine, remainder in the UID maintained"). Returns the return manifest (machine, feeder, part, UID,
    /// remaining at unload), captured from the live inventory before clearing.
    /// </summary>
    public IReadOnlyList<UnloadedReel> UnloadAll(string supervisor)
    {
        // Manifest from the LIVE inventory (the maintained remainder), captured before anything is cleared.
        var manifest = new List<UnloadedReel>();
        var seen = new HashSet<(int, int)>();
        foreach (var ch in _channels.Values)
            foreach (var f in ch.Inventory.Feeders)
                if (!string.IsNullOrWhiteSpace(f.ReelUid))
                {
                    manifest.Add(new UnloadedReel(ch.Machine, f.Feeder, f.PartNumber, f.ReelUid!, Math.Max(0, f.Remaining)));
                    seen.Add((ch.Machine, f.Feeder));
                }
        // Fold in any mapped reel not currently tracked in-memory (e.g. before the first grounding after a restart),
        // so the manifest is complete even when live inventory hasn't been baselined yet.
        foreach (var r in _reels.All())
            if (!seen.Contains((r.Machine, r.Feeder)) && !string.IsNullOrWhiteSpace(r.Uid))
                manifest.Add(new UnloadedReel(r.Machine, r.Feeder, r.Part, r.Uid, 0));

        // 1) Clear the persisted feeder→reel mapping (writes an undo backup). 2) Stop live tracking now, so nothing
        // decrements and the sync pass has no tracked feeders to write — StockOut is never touched.
        _reels.ClearAll();
        foreach (var ch in _channels.Values) ch.Inventory.Clear();

        _log.LogInformation("UNLOAD ALL by {Sup}: {N} reels taken off feeders (feeder mapping cleared; StockOut untouched).",
            supervisor, manifest.Count);
        return manifest.OrderBy(m => m.Machine).ThenBy(m => m.Feeder).ToList();
    }

    /// <summary>Reverse the last unload: re-load the feeder→reel mapping from the saved backup and re-baseline the
    /// inventory (remaining is pulled fresh from StockOut by UID, which the unload never changed). Supervisor use.
    /// Returns the reels re-loaded (empty when there is nothing to restore).</summary>
    public async Task<IReadOnlyList<UnloadedReel>> RestoreLastUnloadAsync(string supervisor, CancellationToken ct = default)
    {
        var restored = _reels.RestoreLastUnload();
        if (restored.Count == 0) return Array.Empty<UnloadedReel>();
        try { await RefreshInventoryAsync(ct); }
        catch (Exception ex) { _log.LogDebug(ex, "Inventory refresh after unload-restore failed."); }
        _log.LogInformation("UNLOAD RESTORE by {Sup}: {N} reels re-loaded onto feeders.", supervisor, restored.Count);
        return restored.Select(r => new UnloadedReel(r.Machine, r.Feeder, r.Part, r.Uid, 0))
            .OrderBy(m => m.Machine).ThenBy(m => m.Feeder).ToList();
    }

    public async Task RefreshInventoryAsync(CancellationToken ct = default)
    {
        Product? model; string side;
        lock (_gate) { model = Model; side = Side ?? "A"; }
        if (model is null) return;

        // Seed from the EFFECTIVE feeder list (_expected) — the SAME source the checklist, the scan interlock,
        // /api/feeders and machine-inventory all use — so the exhaust forecast always matches the feeder list, and
        // a supervisor's manual pen-drive feeder load (which OVERRIDES the DB map) is honoured here too. _expected
        // is built at model-select from DB-or-manual; _expectedQty holds each feeder's per-board Mount Step.
        var feeders = new List<(int Machine, int Feeder, string Part, int Qty)>();
        lock (_gate)
        {
            if (Model?.ProductId == model.ProductId && string.Equals(Side, side, StringComparison.OrdinalIgnoreCase) && _expected.Count > 0)
                feeders = _expected.Select(kv => (kv.Key.machine, kv.Key.feeder, kv.Value,
                    _expectedQty.TryGetValue(kv.Key, out var q) ? q : 0)).ToList();
        }
        if (feeders.Count == 0) { _log.LogWarning("Inventory baseline skipped — feeder list (_expected) empty for {Model} ({Side}).", model.Name, side); return; }

        int panels = _config.PanelBoardsFor(model.Name);   // child boards per panel (a cycle mounts a full panel)
        if (!_config.HasPanelBoards(model.Name))
            _log.LogWarning("Model {Model} has NO panelBoards entry — boards-per-panel defaulted to 1. Decrement, lot progress and DPC will be off by the true factor until line.config.json gets an entry. (Audit H8)", model.Name);
        var wrongPart = new List<(int Machine, int Feeder, string Part, string Uid)>();
        foreach (var ch in _channels.Values) ch.Inventory.Clear();
        foreach (var f in feeders)
        {
            if (!_channels.TryGetValue(f.Machine, out var ch)) continue;

            int perBoard = (f.Qty > 0 ? f.Qty : 1) * panels;   // per-cycle consumption = per-board × panel
            ch.Inventory.Configure(f.Feeder, f.Part, perBoard);

            var reel = _reels.Get(f.Machine, f.Feeder);
            if (reel is null || string.IsNullOrWhiteSpace(reel.Uid)) continue;
            // The reel remembered on this feeder must be the PART the feeder list puts there. On a side/model change
            // the same feeder number can carry a different part; the old reel's mapping survives, and loading it here
            // tracked the OLD reel's UID/qty as the NEW part (decremented, StockOut-synced by UID) until the
            // model-change check overwrote it. Treat a part mismatch exactly like an off-list feeder: unload it
            // (mapping cleared, remainder kept by UID) and leave the feeder un-loaded until a reel is scanned on.
            // Audit finding H2, 2026-09-07.
            if (!string.IsNullOrWhiteSpace(reel.Part) && !Pvs.Core.Verification.PartNumber.Matches(reel.Part, f.Part))
            {
                wrongPart.Add((f.Machine, f.Feeder, reel.Part, reel.Uid));
                continue;
            }
            // Remaining is UID-tracked in the DB (StockOuts.Quantity, kept current — verified == live remaining, not
            // the issued full qty), so pull it from the DB by UID as the AUTHORITATIVE source: a restart or a wiped
            // local file still restores the correct balance. The local record is the fallback if the DB has no row /
            // the link is down. (Wrapped so one failed lookup doesn't abort the whole baseline.)
            int? qty = null;
            try { qty = await _repo.FindStockOutQtyAsync(reel.Uid, f.Part, ct); } catch { qty = null; }
            if (qty is null)
            {
                var loc = _remaining.Get(f.Machine, f.Feeder);
                if (loc is not null && string.Equals(loc.Uid?.Trim(), reel.Uid.Trim(), StringComparison.OrdinalIgnoreCase))
                    qty = loc.Remaining;
            }
            if (qty is int q && q > 0) ch.Inventory.LoadReel(f.Feeder, reel.Uid, q);
        }

        // OFF-LIST REELS ARE UNLOADED AUTOMATICALLY (Danial, 2026-09-07: "if the parts are not on the feeder list for
        // each machine they should not be decremented; all feeders which are not on the current selected model should
        // unload automatically, and should not be in the exhaust card"). A reel still mapped to a feeder that the
        // selected model/side does not use is a leftover from an earlier run: the machine is not picking from it, so
        // grounding it at 1 × panels per cycle drained its count for nothing and forecast phantom run-outs (L1: 24 of
        // 56 exhaust rows / 16 parts on 2026-09-07). Take it off the mapping (reversible backup; remainder kept by
        // UID; StockOut untouched) and never configure it into the live inventory — so it is not decremented, not on
        // the exhaust card, not in the usage check. (This REPLACES the earlier "every loaded feeder must decrement"
        // grounding for feeders outside the list.) Manual pen-drive feeder lists are honoured: _expected already is one.
        var covered = new HashSet<(int, int)>(feeders.Select(f => (f.Machine, f.Feeder)));
        var offList = _reels.All().Where(r => !covered.Contains((r.Machine, r.Feeder)) && !string.IsNullOrWhiteSpace(r.Uid))
                                  .Select(r => (r.Machine, r.Feeder)).ToList();
        var wrongKeys = new HashSet<(int, int)>(wrongPart.Select(w => (w.Machine, w.Feeder)));
        offList.AddRange(wrongKeys);   // wrong-part reels on listed feeders are unloaded the same way
        int extra = 0;
        if (offList.Count > 0)
        {
            var removed = _reels.RemoveOffList(offList);
            extra = removed.Count;
            foreach (var r in removed)
            {
                var loc = _remaining.Get(r.Machine, r.Feeder);
                int rem = (loc is not null && string.Equals(loc.Uid?.Trim(), r.Uid.Trim(), StringComparison.OrdinalIgnoreCase)) ? loc.Remaining : -1;
                bool wrong = wrongKeys.Contains((r.Machine, r.Feeder));
                string listed = wrong ? (feeders.FirstOrDefault(x => x.Machine == r.Machine && x.Feeder == r.Feeder).Part ?? "?") : "";
                string why = wrong ? $"feeder list expects {listed} here" : $"not on the {model.Name} ({side}) feeder list";
                _log.LogWarning("AUTO-UNLOAD M{M} F{F} {Part} ({Uid}): {Why} — taken off the feeder (remainder {Rem} kept by UID; StockOut untouched).",
                    r.Machine, r.Feeder, r.Part, r.Uid, why, rem < 0 ? "?" : rem.ToString());
                Audit(new VerificationRecord(DateTime.Now, _config.LineName, "AutoUnload", r.Machine, r.Feeder,
                    wrong ? "wrong-part reel unloaded" : "off-list reel unloaded",
                    Note: $"{r.Part} {r.Uid}: {why}; remaining {(rem < 0 ? "?" : rem.ToString())} kept by UID",
                    LotNo: _currentLotNo));
            }
        }

        // Re-anchor each machine's board tally to the lot's board count so a restart / re-baseline doesn't zero it
        // under the just-restored feeder balances (which ALREADY reflect those boards) — otherwise the operator's
        // HMI board-count sync would subtract them a second time. On a genuine model/lot change the anchor equals
        // the total, so seed==0 and every machine correctly starts fresh.
        int seed; Dictionary<int, int> offsets;
        lock (_gate) { seed = Math.Max(0, (int)(_m4PanelsTotal - _lotAnchorTotal)); offsets = new Dictionary<int, int>(_tallyOffset); }
        // ...PLUS each machine's own persisted correction (HMI key-in / C1Z apply), so a re-baseline never throws a
        // correction away and the next sync never re-applies it (audit H6).
        foreach (var ch in _channels.Values)
            ch.Inventory.SeedBoardsApplied(Math.Max(0, seed + (offsets.TryGetValue(ch.Machine, out var off) ? off : 0)));

        _log.LogInformation("Inventory baselined for {Model} ({Side}), {N} feeder-list feeders; {X} off-list reel(s) auto-unloaded; board tally seeded at {Seed}{Offsets}.", model.Name, side, feeders.Count, extra, seed,
            offsets.Count == 0 ? "" : " with offsets " + string.Join(", ", offsets.Select(kv => $"M{kv.Key}:{kv.Value:+#;-#;0}")));
        await RefreshLotCoverageAsync(ct);   // pull issued-per-part for this lot/side (material coverage warning)
    }

    /// <summary>
    /// Records each tracked feeder's LIVE remaining (machine inventory, decremented per board) to a LOCAL
    /// file on the line PC — <c>remaining.json</c> next to the app — as the running record. Writing the
    /// remaining back to the parts-control StockOuts is DISABLED for now (per operator request), so this
    /// keeps the record on the line PC only and never touches the parts-control DB. Runs every 5 min and
    /// on demand; returns the number of feeders recorded.
    /// </summary>
    public Task<int> RecordRemainingAsync(CancellationToken ct = default)
    {
        var items = new List<Pvs.LineApp.Inventory.RemainingEntry>();
        foreach (var ch in _channels.Values)
            foreach (var f in ch.Inventory.Feeders)
                if (f.IsTracked && !string.IsNullOrWhiteSpace(f.ReelUid))
                    items.Add(new Pvs.LineApp.Inventory.RemainingEntry(
                        ch.Machine, f.Feeder, f.PartNumber, f.ReelUid!, f.Remaining, DateTime.Now));
        // Guard: never overwrite the saved balances with an EMPTY set. Right after a restart (before the
        // feeders are grounded by a shift/model check) nothing is tracked yet; saving empty here would wipe
        // the last-known balances that remaining.json holds as the source of truth. Skip until we have data.
        if (items.Count > 0) _remaining.Save(items);
        return Task.FromResult(items.Count);
    }

    /// <summary>Timer body: always record local remaining; also mirror to StockOuts when SyncStockOuts is on.</summary>
    private async Task RecordAndSyncAsync(CancellationToken ct = default)
    {
        if (NonCanon) return;   // no Canon BOM/feeders to record or mirror to StockOuts
        try { await RecordRemainingAsync(ct); } catch (Exception ex) { _log.LogDebug(ex, "Local remaining record failed."); }
        try { await RefreshLotCoverageAsync(ct); } catch (Exception ex) { _log.LogDebug(ex, "Lot coverage refresh failed."); }
        if (_config.SyncStockOuts)
        {
            try { await SyncRemainingToStockOutsAsync(ct); } catch (Exception ex) { _log.LogWarning(ex, "StockOuts sync pass failed."); }
        }
    }

    private readonly Dictionary<string, int> _lastSynced = new();  // reel UID -> last remaining written to StockOuts (skip unchanged)

    /// <summary>
    /// Writes each tracked feeder's live remaining balance back to StockOuts.Quantity, keyed by reel UID
    /// (via <see cref="IReelPartRepository.UpdateReelQtyAsync"/> — the Quantity column only). Skips reels whose
    /// balance hasn't changed since the last sync. Enabled by config.SyncStockOuts; the local remaining.json
    /// stays the source of truth, StockOuts is a mirror for the parts-control system. Returns rows written.
    /// </summary>
    public async Task<int> SyncRemainingToStockOutsAsync(CancellationToken ct = default)
    {
        if (!_config.SyncStockOuts) return 0;
        var items = new List<(string Uid, string Part, int Rem)>();
        foreach (var ch in _channels.Values)
            foreach (var f in ch.Inventory.Feeders)
                if (f.IsTracked && !string.IsNullOrWhiteSpace(f.ReelUid))
                    items.Add((f.ReelUid!, f.PartNumber, Math.Max(0, f.Remaining)));
        int written = 0;
        foreach (var it in items)
        {
            bool skip; lock (_gate) { skip = _lastSynced.TryGetValue(it.Uid, out var prev) && prev == it.Rem; }
            if (skip) continue;
            try
            {
                int rows = await _repo.UpdateReelQtyAsync(it.Uid, it.Part, it.Rem, ct);
                if (rows > 0) { lock (_gate) { _lastSynced[it.Uid] = it.Rem; } written++; }
            }
            catch (Exception ex) { _log.LogWarning(ex, "StockOuts sync failed for reel {Uid}.", it.Uid); }
        }
        if (written > 0) _log.LogInformation("Synced {N} reel balances to StockOuts.", written);
        return written;
    }

    /// <summary>
    /// Sets a feeder's remaining in the live inventory AND the local record (a supervisor correction /
    /// machine-inventory adjust). Does NOT write StockOuts. Returns false if the feeder isn't configured.
    /// </summary>
    public bool SetFeederRemaining(int machine, int feeder, int qty)
    {
        if (!_channels.TryGetValue(machine, out var ch)) return false;
        var fs = ch.Inventory.Get(feeder);
        if (fs is null) return false;
        ch.Inventory.SetRemaining(feeder, qty);
        _remaining.Set(new Pvs.LineApp.Inventory.RemainingEntry(machine, feeder, fs.PartNumber, fs.ReelUid ?? "", Math.Max(0, qty), DateTime.Now));
        return true;
    }

    // ---- parts-out: add to pending list (never holds the lock) ----

    private void OnPartsOut(PartsOutEvent e)
    {
        lock (_gate) { _pending[(e.Machine, e.Feeder)] = e; } // dedup by feeder, keep latest
        RecordExhaustCalibration(e);                          // shadow-learn from the genuine parts-out (read-only)
    }

    /// <summary>
    /// SHADOW learning: at a genuine parts-out the reel is empty, so PVS's tracked remaining is the accumulated
    /// count error over the boards that reel ran — the tell-tale that the per-board decrement is drifting. Feed
    /// it to the per-part calibration so future exhaust predictions improve. ONE sample per reel (dedup by UID).
    /// READ-ONLY — never rewrites a reel balance (never lose a reel's count); it only learns a correction factor.
    /// </summary>
    private void RecordExhaustCalibration(PartsOutEvent e)
    {
        if (!_channels.TryGetValue(e.Machine, out var ch)) return;
        if (ch.Inventory.ReelUsage(e.Feeder) is not { } u || string.IsNullOrWhiteSpace(u.ReelUid)) return;
        string key = $"{e.Machine}|{e.Feeder}|{u.ReelUid}";
        lock (_gate) { if (!_calibratedReels.Add(key)) return; }   // already sampled this reel's exhaust
        var cal = _calibration.Record(u.Part, u.BoardsThisReel, u.Remaining, u.MountedPerBoard, DateTime.Now);
        if (cal is null) return;   // too few boards to be meaningful
        _log.LogInformation("Exhaust-calib M{M} F{F} {Part}: reel ran {B} boards, PVS still showed {R} pcs at parts-out → " +
            "err {E:0.00}/board (drift {D:+0.0;-0.0}%, {N} samples).",
            e.Machine, e.Feeder, u.Part, u.BoardsThisReel, u.Remaining, cal.LastErrorPerBoard,
            cal.DriftPercent(u.MountedPerBoard), cal.Samples);
        SaveCalibration();
    }

    private Pvs.Core.Inventory.ExhaustCalibration LoadCalibration()
    {
        try
        {
            if (System.IO.File.Exists(CalibrationPath))
            {
                var seed = System.Text.Json.JsonSerializer.Deserialize<List<Pvs.Core.Inventory.PartCalibration>>(
                    System.IO.File.ReadAllText(CalibrationPath));
                if (seed is not null) return new Pvs.Core.Inventory.ExhaustCalibration(seed: seed);
            }
        }
        catch (Exception ex) { _log.LogDebug(ex, "Calibration load failed — starting fresh."); }
        return new Pvs.Core.Inventory.ExhaustCalibration();
    }

    private void SaveCalibration()
    {
        // Throttle: this fires on parts-out; one write per ~30s is plenty (a lost sample re-learns next reel).
        lock (_gate) { if ((DateTime.Now - _calibrationSavedAt) < TimeSpan.FromSeconds(30)) return; _calibrationSavedAt = DateTime.Now; }
        try { System.IO.File.WriteAllText(CalibrationPath, System.Text.Json.JsonSerializer.Serialize(_calibration.All())); }
        catch (Exception ex) { _log.LogDebug(ex, "Calibration save failed."); }
    }

    /// <summary>The learned per-part exhaust drift (shadow), worst first — for the accuracy dashboard/endpoint.</summary>
    public IReadOnlyList<Pvs.Core.Inventory.PartCalibration> Calibration() => _calibration.All();

    private void PrunePending(DateTime now)
    {
        var stale = _pending.Where(kv => now - kv.Value.At > PartsOutTtl).Select(kv => kv.Key).ToList();
        foreach (var k in stale) _pending.Remove(k);
    }

    private void Housekeep()
    {
        lock (_gate)
        {
            PrunePending(DateTime.Now);
            // Only the parts-out change (Mode B) and recount auto-clear when idle — they must never block the
            // line. Operator-initiated full scans / model changes PERSIST so the operator can step away and
            // re-enter to finish the remaining feeders; they end only on completion or an explicit cancel.
            if ((_change is not null || _recount is not null) && DateTime.Now - _lastActivity > IdleTimeout)
            {
                _change = null; _recount = null;
                _lastMessage = "Idle parts-change / recount was cleared.";
                _log.LogInformation("Parts-change/recount auto-cleared after {Min} min idle.", IdleTimeout.TotalMinutes);
            }
        }
    }

    // ---- shift schedule ----
    // Shift windows come from line.config.json (Day 07:30–19:30, Night 19:30–07:30). The line mainly runs
    // the day shift; a shift-change check is auto-triggered 5 min after each shift start (07:35 / 19:35).
    private string ShiftKey(DateTime dt) => _shifts.ShiftKey(dt);

    public string CurrentShift => _shifts.ShiftAt(DateTime.Now).Name;
    public string CurrentShiftKey => _shifts.ShiftKey(DateTime.Now);

    /// <summary>The current production lot number (PONumber) — from the latest DailyProductionCount for the line.</summary>
    public string CurrentLotNo { get { lock (_gate) { return _currentLotNo; } } }

    /// <summary>PVS's own panel count for the CURRENT lot run, or 0 when no lot is being tracked (e.g.
    /// just after a model change). This is the figure the machine's own C1M counter is reconciled against.</summary>
    public int CurrentLotPanels
    {
        get
        {
            lock (_gate)
            {
                if (string.IsNullOrWhiteSpace(_currentLotNo) || _lotCountFor != _currentLotNo) return 0;
                return LotPanels();
            }
        }
    }

    /// <summary>Best-effort audit write — never throws into the caller (the sink is a local append log).</summary>
    private void Audit(VerificationRecord r) => _ = SafeWriteAsync(r);
    private async Task SafeWriteAsync(VerificationRecord r)
    {
        try { await _records.WriteAsync(r); } catch (Exception ex) { _log.LogDebug(ex, "Audit write failed."); }
    }

    /// <summary>
    /// Adopt the machine's count as the current lot's count — re-anchor so <see cref="LotPanels"/> == the given
    /// panels. This TRUSTS the last machine's (M4/Cell4 = PCB-out) own counter over PVS's live R0 count, which
    /// misses boards whenever PVS is off/restarting. Persisted so it survives a restart.
    /// <para>
    /// INTEGRITY GUARD (<paramref name="enforceCap"/>): the machine's serial report counter is NOT reset per lot
    /// by operators, so once it runs cumulatively it reads far above the lot's real output (the L307 incident:
    /// 825 panels adopted against a 300-panel target → a 3300-board lot and a phantom DB row). An over-target
    /// value is therefore treated as an un-reset counter and REFUSED, unless a supervisor explicitly forces it.
    /// </para>
    /// Returns the panels it set, <c>-1</c> if no lot is running, or <c>-2</c> if refused by the target cap.
    /// </summary>
    public int AdoptLotCount(int machinePanels, string reason, string? supervisor = null, bool enforceCap = true)
    {
        machinePanels = Math.Max(0, machinePanels);
        int oldPanels, pp; long capPanels; string lot; bool refused, noTarget, wentBackwards = false, adopted = false;
        // Everything that reads AND mutates the anchor happens under ONE lock, so the lot identity can't change
        // underneath the decision (no check-then-act race). Logging/audit/persist run after, outside the lock.
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(_currentLotNo)) return -1;
            lot = _currentLotNo;
            oldPanels = (_lotCountFor == _currentLotNo) ? LotPanels() : 0;
            pp = Model is not null ? _config.PanelBoardsFor(Model.Name) : 1;
            int? effTarget = _lotTarget is int t ? t + _lotExtra : (int?)null;
            noTarget = effTarget is null;
            // Target ceiling in panels, +10% slack for genuine overproduction.
            capPanels = (effTarget is int e && pp > 0) ? (long)Math.Ceiling(e / (double)pp * 1.10) : long.MaxValue;
            // A machine value BELOW PVS's own lot count means its counter was reset under us — a tech reset the
            // program/count mid-lot (the machine offers no way to preset it back; per SI-F it can only be read,
            // read-and-cleared, or deleted). PVS's R0 tally is the fuller record, so an auto-adopt must never
            // ratchet the lot DOWN — that would discard boards already produced. Keep counting to lot end from
            // PVS's figure; only a supervisor force (enforceCap=false) may set the count down.
            wentBackwards = machinePanels < oldPanels;
            // REFUSE (never fall open) when the cap is on and either the target is unknown — the DB-down-at-lot-start
            // window that caused the incident — the machine value is over the ceiling (an un-reset report counter),
            // or it went backwards (a mid-lot machine reset). Refusing keeps PVS's own count: the safe direction.
            refused = enforceCap && (noTarget || machinePanels > capPanels || wentBackwards);
            if (!refused)
            {
                _lotCountFor = _currentLotNo;
                _lotAnchorTotal = _m4PanelsTotal - machinePanels;
                adopted = true;
            }
        }
        int auditBoards = (int)Math.Min(int.MaxValue, (long)machinePanels * pp);
        if (refused)
        {
            string why = noTarget ? "no lot target known" : wentBackwards ? $"below current {oldPanels}p (machine reset — count held)" : $"over cap {capPanels}p";
            _log.LogWarning("Lot count adopt REFUSED ({Why}): lot {Lot} machine {MP}p [{Reason}].", why, lot, machinePanels, reason);
            Audit(new VerificationRecord(DateTime.Now, _config.LineName, "AdoptRejected", 0, 0,
                $"{oldPanels}->{machinePanels}p ({why})", Supervisor: supervisor ?? "auto",
                Quantity: auditBoards, Overridden: false, Note: reason, LotNo: lot));
            return -2;
        }
        _log.LogInformation("Lot count ADOPTED from machine: lot {Lot} {Old}p -> {New}p [{Reason}].",
            lot, oldPanels, machinePanels, reason);
        Audit(new VerificationRecord(DateTime.Now, _config.LineName, "AdoptCount", 0, 0,
            $"{oldPanels}->{machinePanels}p", Supervisor: supervisor ?? "auto",
            Quantity: auditBoards, Overridden: !enforceCap, Note: reason, LotNo: lot));
        if (adopted) SaveLotProgress();
        return machinePanels;
    }

    /// <summary>If the lot target is unknown (DB was unreachable at lot start), try to fetch it now so the adopt
    /// cap can engage. Best-effort and idempotent — safe to call every reconcile pass; on failure the target stays
    /// null and auto-adoption stays refused (the safe direction).</summary>
    public async Task EnsureLotTargetAsync(CancellationToken ct = default)
    {
        string lot;
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(_currentLotNo) || _lotCountFor != _currentLotNo || _lotTarget is not null) return;
            lot = _currentLotNo;
        }
        try
        {
            var target = await _repo.GetLotTargetAsync(lot, ct);
            if (target is int) lock (_gate) { if (_currentLotNo == lot && _lotTarget is null) _lotTarget = target; }
        }
        catch { /* DB still down — leave null; adoption stays refused until it recovers */ }
    }

    /// <summary>Supervisor manually sets the lot count from a machine reading (badge-gated, L2+). Honours the
    /// target cap unless <paramref name="force"/> is set (deliberate override for genuine overproduction).</summary>
    public async Task<string> AdoptLotCountBadgedAsync(string badgeUid, int panels, bool force, CancellationToken ct = default)
    {
        var badge = await _repo.FindBadgeAsync(badgeUid ?? "", ct);
        if (badge is null || !badge.CanReleaseInterlock) return "Scan a SUPERVISOR badge (L2+) to set the lot count.";
        int applied = AdoptLotCount(panels, force ? "manual override (force)" : "manual set", badge.Name, enforceCap: !force);
        if (applied == -1) return "No lot is running.";
        if (applied == -2) return $"Refused: {panels} panels is over the lot target — check the machine's report counter was reset for this lot, or use force to override.";
        return $"Lot count set to {applied} panels ({applied * PerPanel} boards) by {badge.Name}.";
    }

    /// <summary>
    /// OPERATOR/supervisor manually sets ONE machine's board count from its Sony HMI (in PANELS = completed PWBs)
    /// and corrects that machine's feeder draw-down to match. PVS can't read the machine counter over serial while
    /// the line is producing (Appendix F: C1M/C1Z answer A4E00 during AUTO production), so the count is kept
    /// honest by hand: the difference from PVS's own per-machine tally is applied to that machine's feeders as a
    /// one-off decrement (or give-back). Badge-gated (L2+). Per machine — each machine carries its OWN board-out
    /// count, so they are corrected independently (never M4-for-all).
    /// </summary>
    public async Task<string> SyncMachineBoardCountAsync(int machine, int hmiPanels, string badgeUid, CancellationToken ct = default)
    {
        if (!_channels.TryGetValue(machine, out var ch)) return $"Machine {machine} is not configured.";
        if (hmiPanels < 0) return "Enter the HMI panel count (0 or more).";
        // The PCB-out (last) machine's count is the lot count's basis, so its override stays SUPERVISOR-gated.
        // The mid-line machines only correct their OWN feeder draw-down, so the operator may key those with no
        // badge — key-in and Set. An optional badge is still recorded on the mid-line ones if one is scanned.
        string actor = "operator";
        if (machine == _lastMachine)
        {
            var badge = await _repo.FindBadgeAsync(badgeUid ?? "", ct);
            if (badge is null || !badge.CanReleaseInterlock) return "Scan a SUPERVISOR badge (L2+) to set the PCB‑out machine board count.";
            actor = badge.Name;
        }
        else if (!string.IsNullOrWhiteSpace(badgeUid))
        {
            var b = await _repo.FindBadgeAsync(badgeUid, ct);
            if (b is not null) actor = b.Name;
        }
        int before = ch.Inventory.BoardsApplied;
        int delta = ch.Inventory.SyncToBoardCount(hmiPanels);
        NoteTallyOffset(machine, hmiPanels);   // keep this correction across re-baselines (audit H6)
        // Persist every tracked feeder's corrected remaining so the correction survives a restart.
        var tracked = ch.Inventory.Feeders.Where(f => f.IsTracked).ToList();
        foreach (var f in tracked)
            _remaining.Set(new Pvs.LineApp.Inventory.RemainingEntry(machine, f.Feeder, f.PartNumber, f.ReelUid ?? "", f.Remaining, DateTime.Now));
        Audit(new VerificationRecord(DateTime.Now, _config.LineName, "MachineCountSync", tracked.Count, 0,
            $"M{machine} boards {before}->{hmiPanels} (delta {delta})", Supervisor: actor, Quantity: hmiPanels,
            Overridden: true, Note: "operator HMI board-count override; feeder draw-down corrected", LotNo: _currentLotNo));
        _log.LogInformation("M{Machine} board count synced from HMI {Before}->{After} (delta {Delta}); {N} feeders corrected by {Sup}.",
            machine, before, hmiPanels, delta, tracked.Count, actor);
        return delta == 0
            ? $"M{machine} already at {hmiPanels} boards — no feeder change."
            : $"M{machine} set to {hmiPanels} boards ({(delta > 0 ? "drew down" : "gave back")} {Math.Abs(delta)} boards on {tracked.Count} feeders) by {actor}.";
    }

    /// <summary>
    /// Lot component-usage verification — READ-ONLY (never rewrites a reel count; never lose a reel's balance).
    /// The SIMULATED usage for the lot is MountedPerBoard × the lot's completed board count, per tracked feeder.
    /// Each machine's own board tally is the basis it drew its feeders down on, so comparing that tally to the
    /// lot board count tells whether the actual usage matches the simulation: a gap means that machine consumed
    /// its components off a wrong board count, off by MountedPerBoard × the gap. Reporting only — a human acts.
    /// </summary>
    private object BuildLotUsage(string lotNo, int lotBoards)   // caller holds _gate
    {
        int perPanel = Model is not null ? _config.PanelBoardsFor(Model.Name) : 1;
        double tol = _config.UsageToleranceOverPct > 0 ? _config.UsageToleranceOverPct : 0.2;
        double target = _config.UsageTargetPct > 0 ? _config.UsageTargetPct : 0.05;
        var machines = _channels.OrderBy(kv => kv.Key).Select(kv =>
        {
            var ch = kv.Value; int mb = ch.Inventory.BoardsApplied; int gap = lotBoards - mb;
            // The machine's OWN panel count from its last lot-aligned C1Z (median successful ÷ mount), if one landed.
            int? c1zPanels = _tallyLast.TryGetValue(kv.Key, out var tl) && tl.LotNo == lotNo && tl.Verdict is "aligned" or "would-apply" or "applied" ? tl.MachinePanels : (int?)null;
            DateTime? c1zAt = c1zPanels is null ? null : tl!.At;
            var feeders = ch.Inventory.Feeders.Where(f => f.IsTracked).OrderBy(f => f.Feeder).Select(f => new
            {
                feeder = f.Feeder, part = f.PartNumber, perBoard = f.MountedPerBoard,
                simulatedUsage = (long)f.MountedPerBoard * lotBoards,   // the BIBLE: expected pieces for the whole lot
                usageGap = (long)f.MountedPerBoard * gap,               // pieces the actual usage is off by
                remaining = f.Remaining, reelUid = f.ReelUid
            }).ToList();
            // Deviation of this machine's decrement from the bible, as a % of the lot's board count. A machine
            // with no tracked feeders has nothing to verify. accuracy = 100 − |deviation|.
            double devPct = (feeders.Count == 0 || lotBoards == 0) ? 0.0 : Math.Abs(gap) / (double)lotBoards * 100.0;
            string verdict = feeders.Count == 0 ? "n/a" : devPct <= target ? "ok" : devPct <= tol ? "acceptable" : "OUT";
            return new
            {
                machine = kv.Key, machineBoards = mb, boardGap = gap,
                deviationPct = Math.Round(devPct, 3), accuracyPct = Math.Round(100.0 - devPct, 3),
                verdict, matches = verdict is "ok" or "n/a", c1zPanels, c1zAt, feeders
            };
        }).ToList();
        var outMachines = machines.Where(m => m.verdict == "OUT").Select(m => m.machine).ToList();
        return new
        {
            lotNo, lotBoards, perPanel, targetPct = target, tolerancePct = tol,
            worstDeviationPct = machines.Count == 0 ? 0.0 : machines.Max(m => m.deviationPct),
            allWithinTarget = machines.All(m => m.verdict is "ok" or "n/a"),
            comprehensiveCheckRequired = outMachines.Count > 0, outMachines,
            machines
        };
    }

    /// <summary>Live lot component-usage check for the RUNNING lot (read-only). Empty when no lot is tracked.</summary>
    public object LotUsageCheck()
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(_currentLotNo) || _lotCountFor != _currentLotNo) return new { lotNo = "" };
            return BuildLotUsage(_lotCountFor, Math.Max(0, (int)(_m4PanelsTotal - _lotAnchorTotal)));
        }
    }

    /// <summary>At lot completion, record the finishing lot's component-usage verification (read-only) BEFORE its
    /// board counter resets: write the full report to a local file and log/audit a one-line pass/mismatch. This
    /// is the "did each machine's per-board usage match the simulation" gate. Never touches a reel count.</summary>
    private void RecordLotUsageAtFinalize(string outgoingLot)
    {
        object report; int lotBoards;
        var recalced = new List<(int M, int Before, int After, int Delta, int Feeders)>();
        var toPersist = new List<Pvs.LineApp.Inventory.RemainingEntry>();
        double tol = _config.UsageToleranceOverPct > 0 ? _config.UsageToleranceOverPct : 0.2;
        lock (_gate)
        {
            // Calibrate against the LOT SIZE (PO target), NOT PVS's own count — the running count is the thing that
            // drifts (missed R0 board-completes), so it can't calibrate itself. The lot size is the independent truth
            // for a completed lot. UNITS: the lot size is in BOARDS, but the counter/decrement work in PANELS/cycles
            // (one R0 = one panel = boards-per-panel child boards), so convert boards -> panels before calibrating.
            // Fall back to the counted panels only when no lot size is known (DB down).
            int perPanel = Model is not null ? _config.PanelBoardsFor(Model.Name) : 1; if (perPanel < 1) perPanel = 1;
            bool perPanelKnown = Model is not null && _config.HasPanelBoards(Model.Name);
            int countedPanels = Math.Max(0, (int)(_m4PanelsTotal - _lotAnchorTotal));
            // Only convert the lot size with a KNOWN panel factor. An unknown model defaults to 1 board/panel, which
            // would turn a 1200-board lot into 1200 "panels" and draw 3× the real usage off every feeder. (Audit H8)
            if (!perPanelKnown && _lotTarget is int)
                _log.LogWarning("Lot {Lot}: boards-per-panel unknown for model {Model} — lot-end calibration falls back to the counted {Panels} panels, NOT the lot size.", outgoingLot, Model?.Name, countedPanels);
            lotBoards = (perPanelKnown && _lotTarget is int lt && lt > 0)
                ? (int)Math.Round((double)(lt + _lotExtra) / perPanel)   // lot size (boards) -> panels/cycles
                : countedPanels;
            report = BuildLotUsage(outgoingLot, lotBoards);   // AS-FOUND: deviations vs the lot size BEFORE any recalc
            // Lot-end check, machine by machine: the machine's OWN count (last lot-aligned C1Z) vs the lot size vs
            // PVS's tally. Logged for the calibration-source decision; the recalc below still uses the lot size.
            foreach (var kv in _tallyLast.OrderBy(k => k.Key))
                if (kv.Value.LotNo == outgoingLot)
                    _log.LogInformation("Lot {Lot} lot-end check M{M}: machine pickups {MP}p (C1Z {At:HH:mm}, {Verdict}), lot size {LS}p, PVS tally {T}p.",
                        outgoingLot, kv.Key, kv.Value.MachinePanels, kv.Value.At, kv.Value.Verdict, lotBoards,
                        _channels.TryGetValue(kv.Key, out var tch) ? tch.Inventory.BoardsApplied : -1);
            // Comprehensive check: a machine whose decrement deviates from the bible (perBoard × lotBoards) by more
            // than the tolerance is RECALCULATED against the bible — snap its board count to the lot count and
            // correct its feeders. Audited + reversible; grounded in the governing per-lot total. Machines within
            // contract are LEFT ALONE (never lose an accurate reel's count).
            foreach (var kv in _channels.OrderBy(k => k.Key))
            {
                var ch = kv.Value;
                int tracked = ch.Inventory.Feeders.Count(f => f.IsTracked);
                if (tracked == 0 || lotBoards == 0) continue;
                int before = ch.Inventory.BoardsApplied;
                double devPct = Math.Abs(lotBoards - before) / (double)lotBoards * 100.0;
                if (devPct <= tol) continue;
                int delta = ch.Inventory.SyncToBoardCount(lotBoards);   // recalc decrement to the bible
                recalced.Add((kv.Key, before, lotBoards, delta, tracked));
                foreach (var f in ch.Inventory.Feeders.Where(f => f.IsTracked))
                    toPersist.Add(new Pvs.LineApp.Inventory.RemainingEntry(kv.Key, f.Feeder, f.PartNumber, f.ReelUid ?? "", f.Remaining, DateTime.Now));
            }
        }
        foreach (var e in toPersist) _remaining.Set(e);   // persist recalced balances so they survive a restart
        try
        {
            var dir = System.IO.Path.Combine(AppContext.BaseDirectory, "lot-usage");
            System.IO.Directory.CreateDirectory(dir);
            var safe = string.Concat((outgoingLot ?? "lot").Select(c => char.IsLetterOrDigit(c) ? c : '_'));
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, safe + ".json"),
                System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { _log.LogDebug(ex, "Lot-usage report write failed for {Lot}.", outgoingLot); }
        if (recalced.Count == 0)
        {
            _log.LogInformation("Lot {Lot} usage within contract (≤{Tol}% of the bible over {Boards} boards) — no recalc needed.", outgoingLot, tol, lotBoards);
            Audit(new VerificationRecord(DateTime.Now, _config.LineName, "LotUsageCheck", lotBoards, 0,
                "usage within contract", Note: "lot-completion component-usage verification", LotNo: outgoingLot));
        }
        else
        {
            foreach (var r in recalced)
                _log.LogWarning("Lot {Lot} COMPREHENSIVE CHECK: M{M} board count {Before} vs lot {After} — decrement RECALCULATED against the bible ({Feeders} feeders, delta {Delta}).",
                    outgoingLot, r.M, r.Before, r.After, r.Feeders, r.Delta);
            Audit(new VerificationRecord(DateTime.Now, _config.LineName, "LotUsageRecalc", lotBoards, 0,
                $"comprehensive check + recalc on {string.Join(",", recalced.Select(r => "M" + r.M))} (>{tol}% off bible)",
                Overridden: true, Note: "lot-end usage exceeded tolerance; decrement recalculated against lot size × per-board mount", LotNo: outgoingLot));
        }
    }

    // ---- Machine-tally sync: the machine's own board count (from its C1Z pickups) vs PVS's R0 tally -------------
    /// <summary>One evaluation of a landed C1Z report against a machine's board tally.</summary>
    public sealed record TallySyncResult(DateTime At, int Machine, string LotNo, string Mode, string Verdict,
        int MachinePanels, int FeedersUsed, int MinPanels, int MaxPanels, int LotPanels, int TallyBefore, int Delta,
        bool WouldApply, bool Applied);

    private const int TallyMinFeeders = 3;
    /// <summary>Below this the difference is a panel in flight (feeders on an earlier head already placed for the
    /// panel being built), not drift — never "correct" it back and forth.</summary>
    public const int TallyMinDeltaPanels = 2;
    private readonly Dictionary<int, TallySyncResult> _tallyLast = new();   // guarded by _gate
    private readonly List<TallySyncResult> _tallyRecent = new();             // guarded by _gate

    /// <summary>
    /// Evaluate a machine's landed per-feeder pickup report against its board tally. The machine's panel count is
    /// the MEDIAN of successful ÷ mount over its tracked feeders (<see cref="Pvs.Core.Inventory.MachineTally"/>).
    /// A report from BEFORE the operator's per-lot reset (carrying the finished lot's count) is rejected as stale.
    /// <c>shadow</c> records what it would correct and touches nothing; <c>apply</c> corrects the tally — and so
    /// the feeders, by the difference only — through the SAME path as the operator's HMI sync (audited, persisted,
    /// reversible). Accurate machines (|delta| &lt; <see cref="TallyMinDeltaPanels"/>) are left alone.
    /// </summary>
    public TallySyncResult? TallySyncFromMachine(int machine, IReadOnlyList<(int Feeder, long Successful)> pickups, string mode)
    {
        if (!_channels.TryGetValue(machine, out var ch)) return null;
        var rows = new List<(int Feeder, long Successful, int MountPerPanel)>();
        foreach (var p in pickups)
        {
            var fs = ch.Inventory.Get(p.Feeder);
            if (fs is null || !fs.IsTracked) continue;
            rows.Add((p.Feeder, p.Successful, fs.MountedPerBoard));
        }
        var est = Pvs.Core.Inventory.MachineTally.FromPickups(rows, TallyMinFeeders);
        string lot; int lotPanels; bool lotTracked;
        lock (_gate)
        {
            lot = _currentLotNo;
            lotTracked = !string.IsNullOrWhiteSpace(lot) && _lotCountFor == lot;
            lotPanels = lotTracked ? LotPanels() : 0;
        }
        int before = ch.Inventory.BoardsApplied;
        int panels = est?.Panels ?? 0, delta = 0; bool would = false, applied = false; string verdict;
        if (est is null) verdict = "too-few-feeders";
        else if (!lotTracked) verdict = "no-lot";
        else if (!Pvs.Core.Inventory.MachineTally.IsLotAligned(panels, lotPanels)) verdict = "stale";
        else
        {
            delta = panels - before;
            would = Math.Abs(delta) >= TallyMinDeltaPanels;
            verdict = !would ? "aligned" : mode == "apply" ? "applied" : "would-apply";
            if (would && mode == "apply")
            {
                int d = ch.Inventory.SyncToBoardCount(panels);
                NoteTallyOffset(machine, panels);   // keep this correction across re-baselines (audit H6)
                foreach (var f in ch.Inventory.Feeders.Where(f => f.IsTracked))
                    _remaining.Set(new Pvs.LineApp.Inventory.RemainingEntry(machine, f.Feeder, f.PartNumber, f.ReelUid ?? "", f.Remaining, DateTime.Now));
                applied = true;
                Audit(new VerificationRecord(DateTime.Now, _config.LineName, "MachineTallySync", machine, 0,
                    $"M{machine} boards {before}->{panels} (delta {d}) from machine pickups", Supervisor: "machine (C1Z)",
                    Quantity: panels, Overridden: true,
                    Note: $"median of {est.FeedersUsed} feeders (successful ÷ mount), spread {est.MinPanels}-{est.MaxPanels}p", LotNo: lot));
            }
        }
        var r = new TallySyncResult(DateTime.Now, machine, lot, mode, verdict, panels, est?.FeedersUsed ?? 0,
            est?.MinPanels ?? 0, est?.MaxPanels ?? 0, lotPanels, before, delta, would, applied);
        lock (_gate) { _tallyLast[machine] = r; _tallyRecent.Add(r); if (_tallyRecent.Count > 200) _tallyRecent.RemoveAt(0); }
        if (verdict == "would-apply")
            _log.LogWarning("C1Z tally SHADOW M{M}: machine {MP}p (median of {N} feeders, {Min}-{Max}p) vs PVS tally {T}p, lot {LP}p — would correct {D} panels (×mount per feeder). NOT applied.",
                machine, panels, est!.FeedersUsed, est.MinPanels, est.MaxPanels, before, lotPanels, delta);
        else if (verdict == "applied")
            _log.LogWarning("C1Z tally APPLIED M{M}: machine {MP}p (median of {N} feeders) vs PVS tally {T}p — corrected {D} panels on the feeders.",
                machine, panels, est!.FeedersUsed, before, delta);
        else
            _log.LogInformation("C1Z tally M{M}: {Verdict} — machine {MP}p, PVS tally {T}p, lot {LP}p, {N} feeders.",
                machine, verdict, panels, before, lotPanels, est?.FeedersUsed ?? 0);
        try
        {
            var dir = System.IO.Path.Combine(AppContext.BaseDirectory, "tally-sync");
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(System.IO.Path.Combine(dir, $"tally-{DateTime.Now:yyyy-MM-dd}.log"),
                $"{r.At:HH:mm:ss} M{machine} lot={lot} mode={mode} verdict={verdict} machine={panels}p feeders={r.FeedersUsed} spread={r.MinPanels}-{r.MaxPanels} pvs={before}p lotPanels={lotPanels} delta={delta}{Environment.NewLine}");
        }
        catch (Exception ex) { _log.LogDebug(ex, "tally-sync log write failed."); }
        return r;
    }

    /// <summary>Machine-tally sync state for /api/tallysync: last result per machine + recent history.</summary>
    public object TallySyncState(string mode)
    {
        lock (_gate)
            return new
            {
                mode, minDeltaPanels = TallyMinDeltaPanels, minFeeders = TallyMinFeeders, lotNo = _currentLotNo,
                lotPanels = (!string.IsNullOrWhiteSpace(_currentLotNo) && _lotCountFor == _currentLotNo) ? LotPanels() : 0,
                machines = _channels.OrderBy(k => k.Key).Select(k => new
                {
                    machine = k.Key, pvsTally = k.Value.Inventory.BoardsApplied,
                    last = _tallyLast.TryGetValue(k.Key, out var t) ? t : null
                }).ToList(),
                recent = _tallyRecent.AsEnumerable().Reverse().Take(50).ToList()
            };
    }

    /// <summary>Per-machine board-out tally (PVS's own R0 count) + tracked-feeder count + whether a supervisor
    /// badge is required (only the PCB-out/last machine), for the monitor sync UI.</summary>
    public IReadOnlyList<(int Machine, int BoardsApplied, int TrackedFeeders, bool NeedsBadge)> MachineBoardCounts()
    {
        lock (_gate)
            return _channels.OrderBy(kv => kv.Key)
                .Select(kv => (kv.Key, kv.Value.Inventory.BoardsApplied, kv.Value.Inventory.Feeders.Count(f => f.IsTracked), kv.Key == _lastMachine))
                .ToList();
    }

    /// <summary>
    /// Supervisor force-ends the running lot: finalises its production count (a last DPC flush) and clears the
    /// lot so the next one starts fresh from a new anchor. Badge-gated (L2+). Returns a message for the operator.
    /// </summary>
    public async Task<string> ForceEndLotAsync(string badgeUid, CancellationToken ct = default)
    {
        var badge = await _repo.FindBadgeAsync(badgeUid ?? "");
        if (badge is null || !badge.CanReleaseInterlock) return "Scan a SUPERVISOR badge (L2+) to force-end the lot.";
        string endedLot; int panels, pp;
        lock (_gate)
        {
            endedLot = _currentLotNo;
            pp = Model is not null ? _config.PanelBoardsFor(Model.Name) : 1;
            panels = (_lotCountFor == _currentLotNo) ? LotPanels() : 0;
        }
        if (string.IsNullOrWhiteSpace(endedLot)) return "No lot is running.";
        // Verify component usage against the simulation, then preserve this lot's final per-feeder machine data,
        // both BEFORE the operator resets the machines (which destroys the counts).
        RecordLotUsageAtFinalize(endedLot);
        LotFinalizing?.Invoke(endedLot);
        // Finalise the production count for this lot before clearing it.
        if (_config.WriteProductionCount) { try { await FlushProductionCountAsync(ct); } catch (Exception ex) { _log.LogDebug(ex, "Flush on force-end failed."); } }
        int boards = panels * pp;
        bool droppedManual;
        lock (_gate)
        {
            _currentLotNo = ""; _lotCountFor = ""; _lotSideFor = "";
            _lotAnchorTotal = _m4PanelsTotal; _lotExtra = 0; _lotTarget = null;
            // The supervisor's dropdown pick must go too (as SetNonCanonAsync does). Left armed, the next auto-detect
            // tick re-adopted the ENDED lot at 0 panels, and when the real next lot was picked the lot-end recalc ran
            // on the old lot a second time (tally ≈ a few panels vs the full lot size) — drawing the whole lot's
            // usage off every feeder again. Audit finding H1, 2026-09-07.
            droppedManual = _manualLotNo is not null;
            _manualLotNo = null; _manualLotModel = null;
        }
        foreach (var ch in _channels.Values) ch.Inventory.SeedBoardsApplied(0);   // next lot starts its tally fresh (feeders untouched)
        lock (_gate) _tallyLast.Clear();
        ClearTallyOffsets();
        SaveLotProgress();
        if (droppedManual) SaveManualLot();
        _log.LogInformation("Lot {Lot} FORCE-ENDED by {Sup} at {Boards} boards ({Panels} panels); manual lot pick cleared.", endedLot, badge.Name, boards, panels);
        await _records.WriteAsync(new VerificationRecord(DateTime.Now, _config.LineName, "LotEnd", 0, 0,
            "ForceEnded", Supervisor: badge.Name, Quantity: boards, Overridden: true,
            Note: $"supervisor force-end at {panels}p", LotNo: endedLot), ct);
        return $"Lot {endedLot} ended by {badge.Name} — {boards} boards. Ready for the next lot.";
    }

    // A shift-change check is due from 5 min after each shift start (07:35 / 19:35), for a grace window,
    // so it can still run once an in-progress changeover finishes instead of being lost at one instant.
    private static readonly TimeSpan ShiftTriggerGrace = TimeSpan.FromMinutes(60);

    /// <summary>"Day"/"Night" if <paramref name="now"/> is inside a shift-change trigger window, else null.</summary>
    private string? ShiftTriggerSlot(DateTime now)
    {
        // Within the grace window after the CURRENT shift's start (from the configured schedule — one clock).
        try
        {
            var start = _shifts.ShiftStart(now);
            return now >= start && now < start + ShiftTriggerGrace ? _shifts.ShiftAt(now).Name : null;
        }
        catch { return null; }
    }

    /// <summary>True if a ShiftChange, ModelChange or LotEnd full check already completed this shift.</summary>
    private bool CompletedThisShift(string shiftKey) =>
        _lastShiftScan?.ShiftKey == shiftKey ||
        _lastModelChange?.ShiftKey == shiftKey ||
        _lastLotEnd?.ShiftKey == shiftKey;

    /// <summary>
    /// Ensures a shift-change check runs near each shift start (from 07:35 / 19:35 for a 60-min grace
    /// window). It fires once per shift and is SMART about the morning changeover:
    ///  - if a ModelChange / LotEnd / ShiftChange already COMPLETED this shift, that fuller check counts —
    ///    no redundant shift scan is forced;
    ///  - if a check is currently in progress, it waits and retries on a later tick instead of interrupting;
    ///  - once the line is idle with a model loaded (and nothing has covered the shift), it starts the
    ///    ShiftChange scan, which waits at AwaitingBadge for the operator.
    /// </summary>
    private void CheckShiftTrigger()
    {
        try
        {
            var now = DateTime.Now;
            string? slot = ShiftTriggerSlot(now);
            if (slot is null) return;

            string shiftKey = _shifts.ShiftKey(now);
            lock (_gate)
            {
                if (_shiftTriggerDone.Contains(shiftKey)) return;   // already fired or satisfied this shift

                if (CompletedThisShift(shiftKey))
                {
                    _shiftTriggerDone.Add(shiftKey);
                    _log.LogInformation("Shift-change trigger ({Slot}) satisfied — a full check already completed this shift.", slot);
                    return;
                }
                // Don't interrupt an in-progress check (e.g. a lot-start model change); retry on a later tick.
                if (HasActiveSession) { _log.LogDebug("Shift-change trigger ({Slot}) waiting — a check is in progress.", slot); return; }
                if (Model is null)    { _log.LogDebug("Shift-change trigger ({Slot}) waiting — no model loaded yet.", slot); return; }

                _shiftTriggerDone.Add(shiftKey);
            }
            string msg = StartFullScan(ScanPurpose.ShiftChange);
            _log.LogInformation("Auto-triggered {Slot} shift-change check: {Msg}", slot, msg);
        }
        catch (Exception ex) { _log.LogDebug(ex, "Shift-change trigger check failed."); }
    }

    // ---- operator actions ----

    public async Task<SessionSnapshot> ScanBadgeAsync(string uid, CancellationToken ct = default)
    {
        var badge = await _repo.FindBadgeAsync(uid, ct) ?? new Badge("?", uid, "");
        Daiya.AddOperator(badge.UserId, badge.Name, badge.Level, DateTime.Now);   // shift roster for the Daiya Graph names
        lock (_gate)
        {
            _lastActivity = DateTime.Now;

            // No task running + a parts-out is pending -> start Mode B for the OLDEST one.
            if (!HasActiveSession)
            {
                PrunePending(DateTime.Now);
                if (_pending.Count > 0)
                {
                    var e = _pending.Values.OrderBy(x => x.At).First();
                    _pending.Remove((e.Machine, e.Feeder));
                    string expected = ExpectedFor(e.Machine, e.Feeder) ?? "(feeder not in model — flag)";
                    _change = new PartsChangeSession(e.Machine, e.Feeder, expected, fromPartsOut: true);
                }
            }

            if (_change is not null) _lastMessage = _change.ScanBadge(badge).Message;
            else if (_scan is not null) _lastMessage = _scan.ScanBadge(badge).Message;
            else if (_recount is not null) _lastMessage = _recount.ScanBadge(badge).Message;
            else if (_modelChange is not null) _lastMessage = _modelChange.ScanBadge(badge).Message;
            else _lastMessage = "No task in progress. Start a check, or a parts-out will appear here.";
        }
        await MaybeFinalizeAsync(ct);
        SaveScanProgress();
        return Snapshot();
    }

    public async Task<SessionSnapshot> ScanReelAsync(string partNumber, string uid, CancellationToken ct = default)
    {
        // A mis-scan (UID or badge landing in the part-number slot) must NOT be treated as a wrong part —
        // that would interlock and drag in a supervisor. The two barcodes sit side by side on the reel, so
        // this is a common slip. UIDs and badges end with '%' (or match the badge prefix); part numbers
        // don't. Reject and re-prompt instead of interlocking.
        bool partIsCode = _config.LooksLikeBadge(partNumber) || (partNumber ?? "").TrimEnd().EndsWith("%");
        if (partIsCode || _config.LooksLikeBadge(uid))
        {
            lock (_gate) { _lastActivity = DateTime.Now; _lastMessage = "That looks like a UID/badge, not a part number — scan the reel's PART barcode, then its UID."; }
            return Snapshot();
        }

        bool needQty = false; string? qUid = null, qPart = null; int qFeeder = 0, qMachine = 0;
        bool needChangeQty = false; string? cUid = null, cPart = null; int cFeeder = 0;
        int? rM = null, rF = null; bool accepted = false;
        lock (_gate)
        {
            _lastActivity = DateTime.Now;
            // capture the feeder this reel is going onto (before the scan advances the cursor) — CHECK scans only.
            // A parts-change commits its mapping (and retires the outgoing reel) on COMPLETION, in MaybeFinalizeAsync:
            // doing it here mapped the new reel and zeroed the old reel's StockOut on ANY scan in AwaitingNewReel,
            // even one the session then interlocked and the operator cancelled. (Audit finding H5, 2026-09-07.)
            if (_scan is { State: FullScanState.Scanning } sc) { rM = sc.Current?.Machine; rF = sc.Current?.Feeder; }
            else if (_modelChange is { State: ModelChangeState.Scanning } mc0) { rM = mc0.Current?.Machine; rF = mc0.Current?.Feeder; }

            if (_change is not null)
            {
                _lastMessage = _change.ScanReel(partNumber, uid).Message;
                // New reel verified -> pull its qty from parts control so the operator just confirms (FIRM).
                if (_change.State == ChangeState.AwaitingQuantity)
                { needChangeQty = true; cUid = _change.NewReelUid; cPart = _change.NewReelPart; cFeeder = _change.Feeder; }
            }
            else if (_scan is not null)
            {
                var st = _scan.ScanReel(partNumber, uid); _lastMessage = st.Message;
                accepted = st.Outcome is StepOutcome.Ok or StepOutcome.Completed;
            }
            else if (_recount is not null) _lastMessage = _recount.ScanReel(partNumber, uid).Message;
            else if (_modelChange is not null)
            {
                var st = _modelChange.ScanReel(partNumber, uid); _lastMessage = st.Message;
                accepted = st.Outcome is StepOutcome.Ok or StepOutcome.Completed;
                if (_modelChange.State == ModelChangeState.ConfirmingQty && _modelChange.Current is { } cur)
                { needQty = true; qUid = cur.ReelUid; qPart = cur.ScannedPart; qFeeder = cur.Feeder; qMachine = cur.Machine; }
            }
            else _lastMessage = "No task in progress.";
        }

        // Remember which reel is now on that feeder — ONLY when the check ACCEPTED the scan (a wrong-part interlock
        // used to map the wrong reel anyway), and a UID lives on ONE feeder: the same reel remembered elsewhere is a
        // stale mapping (moved reel / earlier mis-scan) that would otherwise be decremented and StockOut-synced
        // twice. (Audit finding H4, 2026-09-07.)
        if (accepted && rM is int rm && rF is int rf) CommitReelMapping(rm, rf, partNumber, uid);

        // Part matched on a model change — resolve the reel's current remaining qty to show the operator.
        // Prefer the LOCAL record (tracked remaining); StockOuts issued qty only for a new/unrecorded reel.
        if (needQty)
        {
            int? q = null;
            var loc = _remaining.Get(qMachine, qFeeder);
            if (loc is not null && string.Equals(loc.Uid?.Trim(), qUid?.Trim(), StringComparison.OrdinalIgnoreCase)) q = loc.Remaining;
            if (q is null) { try { q = await _repo.FindStockOutQtyAsync(qUid ?? "", qPart ?? "", ct); } catch { /* show as unknown */ } }
            q ??= QtyFromUid(qUid);   // fallback: some reel UID barcodes embed the quantity
            lock (_gate)
            {
                _modelChange?.SetScannedQty(q);
                _lastMessage = q is int v
                    ? $"Feeder {qFeeder} part verified. Qty on record: {v}. Confirm, or flag if wrong."
                    : $"Feeder {qFeeder} part verified. No qty found — confirm or flag.";
            }
        }

        // Parts-change new reel verified -> resolve its qty from StockOuts and show it for FIRM confirmation.
        if (needChangeQty)
        {
            int? q = null;
            try { q = await _repo.FindStockOutQtyAsync(cUid ?? "", cPart ?? "", ct); } catch { /* show as unknown */ }
            q ??= QtyFromUid(cUid);
            lock (_gate)
            {
                _change?.SetPrefillQuantity(q);
                _lastMessage = q is int v
                    ? $"New reel verified. Qty on record: {v}. Scan FIRM to confirm (or key the qty)."
                    : $"New reel verified — no qty on record. Key the quantity, then scan FIRM.";
            }
        }
        await MaybeFinalizeAsync(ct);
        SaveScanProgress();
        return Snapshot();
    }

    /// <summary>Map a reel to a feeder, displacing the same UID from any other feeder and stopping its live tracking
    /// there (the reel is physically HERE). Logged + audited when something was displaced.</summary>
    private void CommitReelMapping(int machine, int feeder, string part, string uid)
    {
        var displaced = _reels.SetUnique(machine, feeder, part, uid);
        foreach (var d in displaced)
        {
            if (_channels.TryGetValue(d.Machine, out var dch)) dch.Inventory.UnloadReel(d.Feeder);
            _log.LogWarning("Reel {Uid} ({Part}) scanned onto M{M} F{F} — it was still remembered on M{DM} F{DF}; that stale mapping is dropped (a reel is on one feeder).",
                uid, part, machine, feeder, d.Machine, d.Feeder);
            Audit(new VerificationRecord(DateTime.Now, _config.LineName, "ReelMoved", d.Machine, d.Feeder,
                "stale mapping dropped", NewReelUid: uid, NewReelPart: part,
                Note: $"UID now on M{machine} F{feeder}; was remembered on M{d.Machine} F{d.Feeder}", LotNo: _currentLotNo));
        }
    }

    /// <summary>A reel swapped OFF a feeder during a parts-change is CONSUMED — a parts-change happens because the
    /// machine ran that feeder OUT, so the outgoing reel is empty. Per the stock model (StockOut = reels in feeders
    /// + standby reels at the line; a consumed reel leaves StockOut for ConsumedReels), retire it: record it in
    /// ConsumedReels then zero its StockOut quantity. UNCONDITIONAL — rank/remaining no longer gate it, because a
    /// parts-change means it ran out (the tracked remaining is drift-prone and physically the reel is empty).
    /// REVERSIBLE via <see cref="RestoreConsumedReelAsync"/> if a swap was a mistake. Only runs where StockOuts
    /// write-back is enabled (SyncStockOuts). Model-changes go through a different path and are NOT retired —
    /// those reels come off still-full and return to standby.</summary>
    private async Task RetireOutgoingReelAsync(Pvs.LineApp.Inventory.FeederReel outgoing, int machine, int feeder, CancellationToken ct)
    {
        if (!_config.SyncStockOuts) return;
        string lot; lock (_gate) lot = _currentLotNo;
        if (string.IsNullOrWhiteSpace(lot)) return;                        // only "while mid lot running"
        var uid = outgoing.Uid?.Trim(); var part = outgoing.Part?.Trim();
        if (string.IsNullOrWhiteSpace(uid) || string.IsNullOrWhiteSpace(part)) return;
        // Remaining: prefer the live tracked balance for this feeder+UID, else the DB StockOut qty.
        int? remaining = null;
        var loc = _remaining.Get(machine, feeder);
        if (loc is not null && string.Equals(loc.Uid?.Trim(), uid, StringComparison.OrdinalIgnoreCase)) remaining = loc.Remaining;
        if (remaining is null) { try { remaining = await _repo.FindStockOutQtyAsync(uid, part, ct); } catch { } }
        if (remaining is not int rem) return;                             // unknown remaining -> don't touch it
        // No rank/remaining gate: a parts-change swap means this reel ran OUT (consumed). Whatever the drift-prone
        // tracked remaining says, the physical reel is empty, so it leaves StockOut for ConsumedReels. The rank is
        // recorded for the restore record only — it never decides whether to retire.
        string? rank; try { rank = await _repo.GetPartRankAsync(part, ct); } catch { rank = null; }
        try
        {
            // Record FIRST: if ConsumedReels doesn't exist yet (table not created), this throws and we never zero
            // the StockOut — so the feature is inert (safe) until the table is in place, and never zeroes without
            // a restore record behind it.
            await _repo.RecordConsumedReelAsync(new Pvs.Core.Data.ConsumedReel(uid, part, rank ?? "", rem, _config.LineName, lot), ct);
            await _repo.UpdateReelQtyAsync(uid, part, 0, ct);             // then zero the StockOut (same write as the qty sync)
            await _repo.AddPartAttritionAsync(part, rem, 1, ct);         // the zeroed remainder is written-off material -> accumulate as attrition for this part
            lock (_gate) _lastSynced[uid] = 0;
            Audit(new VerificationRecord(DateTime.Now, _config.LineName, "ReelRetired", rem, 0,
                $"M{machine} F{feeder} {part} [{rank}]", NewReelUid: uid, Overridden: true,
                Note: $"consumed reel retired at {rem} pcs (parts-change swap, rank {rank}); StockOut zeroed; {rem} pcs to attrition", LotNo: lot));
            _log.LogInformation("Retired reel {Uid} ({Part} rank {Rank}) at {Rem} pcs off M{M}F{F}.", uid, part, rank, rem, machine, feeder);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Reel retire failed for {Uid}.", uid); }
    }

    /// <summary>Reverses a retire: restores the reel's StockOut quantity to what it had when retired and closes
    /// the ConsumedReels record. Supervisor (L2+) only.</summary>
    public async Task<string> RestoreConsumedReelAsync(string uid, string badgeUid, CancellationToken ct = default)
    {
        var badge = await _repo.FindBadgeAsync(badgeUid ?? "", ct);
        if (badge is null || !badge.CanReleaseInterlock) return "Scan a SUPERVISOR badge (L2+) to restore a reel.";
        var rec = await _repo.GetActiveConsumedReelAsync((uid ?? "").Trim(), ct);
        if (rec is null) return $"No retired reel found for UID '{uid}'.";
        await _repo.UpdateReelQtyAsync(rec.Uid, rec.PartNumber, rec.RemainingAtRetire, ct);
        await _repo.MarkConsumedRestoredAsync(rec.Uid, ct);
        await _repo.AddPartAttritionAsync(rec.PartNumber, -rec.RemainingAtRetire, -1, ct);   // reverse the attrition we accumulated
        lock (_gate) _lastSynced[rec.Uid] = rec.RemainingAtRetire;
        Audit(new VerificationRecord(DateTime.Now, _config.LineName, "ReelRestored", rec.RemainingAtRetire, 0,
            $"{rec.PartNumber} [{rec.Rank}]", NewReelUid: rec.Uid, Supervisor: badge.Name, Overridden: true,
            Note: $"retired reel restored to {rec.RemainingAtRetire} pcs", LotNo: rec.LotNo));
        return $"Reel {rec.Uid} restored — {rec.RemainingAtRetire} pcs put back by {badge.Name}.";
    }

    public async Task<SessionSnapshot> EnterQuantityAsync(int qty, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _lastActivity = DateTime.Now;
            if (_change is not null) _lastMessage = _change.EnterQuantity(qty).Message;
            else if (_recount is not null) _lastMessage = _recount.EnterCount(qty).Message;
            else _lastMessage = "No task in progress.";
        }
        await MaybeFinalizeAsync(ct);
        return Snapshot();
    }

    public async Task<SessionSnapshot> SkipAsync(CancellationToken ct = default)
    {
        lock (_gate) { _lastActivity = DateTime.Now; if (_change is not null) _lastMessage = _change.Skip().Message; }
        await MaybeFinalizeAsync(ct);
        return Snapshot();
    }

    /// <summary>Jump the scan to a chosen machine (operator picks scan order).</summary>
    public SessionSnapshot GoToMachine(int machine)
    {
        lock (_gate)
        {
            _lastActivity = DateTime.Now;
            if (_scan is not null) _lastMessage = _scan.GoToMachine(machine).Message;
            else if (_modelChange is not null) _lastMessage = _modelChange.GoToMachine(machine).Message;
            else _lastMessage = "No full-scan / model-change task in progress.";
        }
        SaveScanProgress();
        return Snapshot();
    }

    /// <summary>Operator confirms a completed machine — saves a per-machine record before moving on.</summary>
    public async Task<SessionSnapshot> ConfirmMachineAsync(int machine, CancellationToken ct = default)
    {
        VerificationRecord? rec = null;
        lock (_gate)
        {
            _lastActivity = DateTime.Now;
            if (_scan is not null)
            {
                var step = _scan.ConfirmMachine(machine);
                _lastMessage = step.Message;
                if (step.Outcome == StepOutcome.Ok)
                {
                    var items = _scan.Items.Where(i => i.Machine == machine).ToList();
                    int matched = items.Count(i => i.Status == FeederCheckStatus.Matched);
                    int released = items.Count(i => i.Status == FeederCheckStatus.Released);
                    rec = new VerificationRecord(DateTime.Now, _config.LineName, _scan.Purpose + "-Machine", machine, 0,
                        $"M{machine}: {matched} matched, {released} released ({items.Count} feeders)",
                        Operator: _scan.Operator?.Name, ExpectedPart: Model?.Name, LotNo: _currentLotNo);
                }
            }
            else if (_modelChange is not null)
            {
                var step = _modelChange.ConfirmMachine(machine);
                _lastMessage = step.Message;
                if (step.Outcome == StepOutcome.Ok)
                {
                    var items = _modelChange.Items.Where(i => i.Machine == machine).ToList();
                    int done = items.Count(i => i.PartStatus is FeederCheckStatus.Matched or FeederCheckStatus.Released);
                    int corrected = items.Count(i => i.QtyOutcome == QtyOutcome.Corrected);
                    rec = new VerificationRecord(DateTime.Now, _config.LineName, "ModelChange-Machine", machine, 0,
                        $"M{machine}: {done}/{items.Count} verified, {corrected} qty corrected",
                        Operator: _modelChange.Operator?.Name, ExpectedPart: Model?.Name, LotNo: _currentLotNo);
                }
            }
            else _lastMessage = "No full-scan / model-change task in progress.";
        }
        if (rec is not null) await _records.WriteAsync(rec, ct);
        SaveScanProgress();
        return Snapshot();
    }

    /// <summary>Reset one feeder in the active full-scan / model-change so it can be rescanned.</summary>
    public async Task<SessionSnapshot> ClearFeederAsync(int machine, int feeder, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _lastActivity = DateTime.Now;
            if (_scan is not null) _lastMessage = _scan.ClearFeeder(machine, feeder).Message;
            else if (_modelChange is not null) _lastMessage = _modelChange.ClearFeeder(machine, feeder).Message;
            else _lastMessage = "No full-scan / model-change task in progress.";
        }
        await MaybeFinalizeAsync(ct);
        SaveScanProgress();
        return Snapshot();
    }

    public SessionSnapshot Cancel()
    {
        lock (_gate)
        {
            _lastActivity = DateTime.Now;
            _change = null; _scan = null; _recount = null; _modelChange = null;
            _lastMessage = "Task cancelled.";
        }
        SaveScanProgress();   // no active check -> clears the saved progress file
        return Snapshot();
    }

    public async Task<int?> PrefillQuantityAsync(string partNumber, string uid, CancellationToken ct = default)
    {
        // Prefill is a convenience — never let a DB outage block it (the operator keys the qty by hand). Fall back
        // to the local record, then the qty embedded in the UID barcode, so a parts-exchange completes offline.
        try
        {
            var reel = await _repo.FindReelAsync(uid, partNumber, ct);
            if (reel?.RemainingQty is int q) return q;
        }
        catch { /* DB unreachable — fall through to local/offline sources */ }
        return QtyFromUid(uid);
    }

    // ---- model-change quantity step ----

    public async Task<SessionSnapshot> ConfirmQtyAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            _lastActivity = DateTime.Now;
            _lastMessage = _modelChange is not null ? _modelChange.ConfirmQty().Message : "No task in progress.";
        }
        await MaybeFinalizeAsync(ct);
        SaveScanProgress();
        return Snapshot();
    }

    public async Task<SessionSnapshot> RejectQtyAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            _lastActivity = DateTime.Now;
            _lastMessage = _modelChange is not null ? _modelChange.RejectQty().Message : "No task in progress.";
        }
        await MaybeFinalizeAsync(ct);
        SaveScanProgress();
        return Snapshot();
    }

    /// <summary>Supervisor keys the corrected quantity; writes it back to inventory and records the change.</summary>
    public async Task<SessionSnapshot> EnterCorrectedQtyAsync(int qty, CancellationToken ct = default)
    {
        ModelChangeItem? corrected = null; int prevQty = 0; string? op = null;
        lock (_gate)
        {
            _lastActivity = DateTime.Now;
            if (_modelChange is { State: ModelChangeState.AwaitingCorrectedQty } mc)
            {
                var it = mc.Current;
                prevQty = it?.CurrentQty ?? 0;
                op = mc.Operator?.Name;
                var step = mc.EnterCorrectedQty(qty);
                _lastMessage = step.Message;
                if (step.Outcome is StepOutcome.Ok or StepOutcome.Completed) corrected = it;
            }
            else _lastMessage = "No corrected quantity is expected right now.";
        }

        if (corrected is not null)
        {
            // Apply the correction to the LIVE inventory + the local record (no StockOuts write).
            lock (_gate)
            {
                if (_channels.TryGetValue(corrected.Machine, out var ch) && ch.Inventory.Get(corrected.Feeder) is not null)
                    ch.Inventory.SetRemaining(corrected.Feeder, qty);
            }
            _remaining.Set(new Pvs.LineApp.Inventory.RemainingEntry(corrected.Machine, corrected.Feeder,
                corrected.ScannedPart ?? "", corrected.ReelUid ?? "", Math.Max(0, qty), DateTime.Now));

            await _records.WriteAsync(new VerificationRecord(
                DateTime.Now, _config.LineName, "ModelChange", corrected.Machine, corrected.Feeder,
                "QtyCorrected", Operator: op, Supervisor: corrected.QtyCorrectedBy?.Name,
                ExpectedPart: corrected.ExpectedPart, NewReelUid: corrected.ReelUid, NewReelPart: corrected.ScannedPart,
                Quantity: qty, Overridden: true, Note: $"qty {prevQty}->{qty} (recorded on line PC)", LotNo: _currentLotNo), ct);
        }
        await MaybeFinalizeAsync(ct);
        SaveScanProgress();
        return Snapshot();
    }

    public string StartModelChange()
    {
        string msg;
        lock (_gate)
        {
            _lastActivity = DateTime.Now;
            if (HasActiveSession) return "Finish or cancel the current task first.";
            if (Model is null) return "Select the running model first.";
            var feeders = _expected
                .OrderBy(kv => kv.Key.machine).ThenBy(kv => kv.Key.feeder)
                .Select(kv => new ModelChangeItem { Machine = kv.Key.machine, Feeder = kv.Key.feeder, ExpectedPart = kv.Value })
                .ToList();
            if (feeders.Count == 0) return "No feeders mapped for this model.";
            _modelChange = new ModelChangeSession(feeders);
            msg = $"Model-change check started — {feeders.Count} feeders. Badge in to begin.";
        }
        SaveScanProgress();
        return msg;
    }

    public string StartFullScan(ScanPurpose purpose)
    {
        string msg;
        lock (_gate)
        {
            _lastActivity = DateTime.Now;
            if (HasActiveSession) return "Finish or cancel the current task first.";
            if (Model is null) return "Select the running model first.";
            var feeders = _expected
                .OrderBy(kv => kv.Key.machine).ThenBy(kv => kv.Key.feeder)
                .Select(kv => new FeederCheck { Machine = kv.Key.machine, Feeder = kv.Key.feeder, ExpectedPart = kv.Value })
                .ToList();
            if (feeders.Count == 0) return "No feeders mapped for this model.";
            _scan = new FullScanSession(purpose, feeders);
            msg = $"{purpose} check started — {feeders.Count} feeders.";
        }
        SaveScanProgress();
        return msg;
    }

    // ---- finalize ----

    private async Task MaybeFinalizeAsync(CancellationToken ct)
    {
        VerificationRecord? rec = null;
        bool rebaseline = false;
        bool checkDone = false;
        Pvs.LineApp.Inventory.FeederReel? retiring = null;
        lock (_gate)
        {
            if (_change is { State: ChangeState.Complete or ChangeState.Skipped } c)
            {
                bool completed = c.State == ChangeState.Complete;
                // COMMIT the swap only now that it is complete: capture the OUTGOING reel (for auto-retire), then
                // remember the new reel on the feeder. A skipped/cancelled change ("false alarm") commits nothing —
                // the old reel stays mapped and is NOT retired. (Audit finding H5.)
                if (completed && !string.IsNullOrWhiteSpace(c.NewReelUid))
                {
                    var prev = _reels.Get(c.Machine, c.Feeder);
                    if (prev is not null && !string.IsNullOrWhiteSpace(prev.Uid)
                        && !string.Equals(prev.Uid.Trim(), c.NewReelUid.Trim(), StringComparison.OrdinalIgnoreCase))
                        retiring = prev;
                    CommitReelMapping(c.Machine, c.Feeder, c.NewReelPart ?? c.ExpectedPart ?? "", c.NewReelUid);
                }
                rec = new VerificationRecord(
                    DateTime.Now, _config.LineName, "PartsChange", c.Machine, c.Feeder,
                    completed ? (c.WasOverridden ? "Released" : "Completed") : "Skipped",
                    Operator: c.Operator?.Name, Supervisor: c.ReleasedBy?.Name,
                    ExpectedPart: c.ExpectedPart, OldReelUid: c.OldReelUid,
                    NewReelUid: c.NewReelUid, NewReelPart: c.NewReelPart,
                    Quantity: c.Quantity, Overridden: c.WasOverridden,
                    Note: completed ? null : "false alarm", LotNo: _currentLotNo);
                // Load the NEW reel + its keyed qty into the LIVE inventory so the forecast tracks the fresh
                // reel (previously the feeder kept decrementing the old, exhausted reel → wrong ~0 remaining).
                if (completed && !string.IsNullOrWhiteSpace(c.NewReelUid) && c.Quantity is int newQty
                    && _channels.TryGetValue(c.Machine, out var chNew) && chNew.Inventory.Get(c.Feeder) is not null)
                {
                    chNew.Inventory.LoadReel(c.Feeder, c.NewReelUid!, newQty);
                    _remaining.Set(new Pvs.LineApp.Inventory.RemainingEntry(
                        c.Machine, c.Feeder, c.NewReelPart ?? c.ExpectedPart, c.NewReelUid!, Math.Max(0, newQty), DateTime.Now));
                }
                _change = null;
            }
            else if (_scan is { State: FullScanState.Complete } s)
            {
                rec = new VerificationRecord(
                    DateTime.Now, _config.LineName, s.Purpose.ToString(), 0, 0,
                    $"{s.MatchedCount} matched, {s.ReleasedCount} released",
                    Operator: s.Operator?.Name, LotNo: _currentLotNo);
                var vc = new VerifyCompletion(s.Purpose.ToString(), DateTime.Now, Model?.Name ?? "",
                    $"{s.MatchedCount} matched, {s.ReleasedCount} released", ShiftKey(DateTime.Now));
                if (s.Purpose == ScanPurpose.LotEnd) _lastLotEnd = vc; else _lastShiftScan = vc;
                _scan = null;
                rebaseline = true; checkDone = true;
            }
            else if (_recount is { State: RecountState.Complete } r)
            {
                rec = new VerificationRecord(
                    DateTime.Now, _config.LineName, "Recount", 0, r.Feeder ?? 0,
                    "Completed", Operator: r.Operator?.Name, Supervisor: r.Approver?.Name,
                    NewReelUid: r.ReelUid, Quantity: r.CountedQty, Overridden: r.NeededApproval, LotNo: _currentLotNo);
                _recount = null;
            }
            else if (_modelChange is { State: ModelChangeState.Complete } m)
            {
                rec = new VerificationRecord(
                    DateTime.Now, _config.LineName, "ModelChange", 0, 0,
                    $"{m.MatchedCount} matched, {m.ReleasedCount} released, {m.QtyConfirmedCount} qty ok, {m.QtyCorrectedCount} qty corrected",
                    Operator: m.Operator?.Name, LotNo: _currentLotNo);
                _lastModelChange = new VerifyCompletion("ModelChange", DateTime.Now, Model?.Name ?? "",
                    $"{m.MatchedCount + m.ReleasedCount}/{m.Items.Count} verified, {m.QtyCorrectedCount} qty corrected", ShiftKey(DateTime.Now));
                _modelChange = null;
                rebaseline = true; checkDone = true;
            }
        }
        if (checkDone) SaveCheckStatus();   // persist the completion so the monitor's GREEN survives an app restart
        if (rec is not null) await _records.WriteAsync(rec, ct);
        if (retiring is not null) await RetireOutgoingReelAsync(retiring, retiring.Machine, retiring.Feeder, ct);
        if (rebaseline) { try { await RefreshInventoryAsync(ct); } catch (Exception ex) { _log.LogDebug(ex, "Inventory rebaseline after check failed."); } }
    }

    // ---- last-check completion status (the monitor's shift/lot/model-change GREEN) — persisted so it survives a restart ----
    private sealed record CheckStatusData(VerifyCompletion? ShiftScan, VerifyCompletion? LotEnd, VerifyCompletion? ModelChange);
    private static string CheckStatusPath => System.IO.Path.Combine(AppContext.BaseDirectory, "check-status.json");

    private void SaveCheckStatus()
    {
        CheckStatusData d; lock (_gate) d = new CheckStatusData(_lastShiftScan, _lastLotEnd, _lastModelChange);
        try { System.IO.File.WriteAllText(CheckStatusPath, System.Text.Json.JsonSerializer.Serialize(d)); }
        catch (Exception ex) { _log.LogDebug(ex, "Check status save failed."); }
    }

    private void LoadCheckStatus()
    {
        try
        {
            if (!System.IO.File.Exists(CheckStatusPath)) return;
            var d = System.Text.Json.JsonSerializer.Deserialize<CheckStatusData>(
                System.IO.File.ReadAllText(CheckStatusPath), new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (d is null) return;
            _lastShiftScan = d.ShiftScan; _lastLotEnd = d.LotEnd; _lastModelChange = d.ModelChange;
        }
        catch (Exception ex) { _log.LogDebug(ex, "Check status load failed."); }
    }

    // ---- snapshot ----

    public SessionSnapshot Snapshot()
    {
        lock (_gate)
        {
            PrunePending(DateTime.Now);
            var pending = _pending.Values.OrderBy(e => e.At)
                .Select(e =>
                {
                    var (rem, fc) = FeederRemainingCheck(e.Machine, e.Feeder);
                    return (object)new { machine = e.Machine, feeder = e.Feeder, at = e.At, remaining = rem, probableFalse = fc };
                }).ToList();

            if (_change is { } c)
            {
                var (cRem, cFalse) = c.FromPartsOut ? FeederRemainingCheck(c.Machine, c.Feeder) : (null, false);
                return new SessionSnapshot("PartsChange", c.State.ToString(),
                    c.InterlockReason ?? _lastMessage, c.Machine, c.Feeder, c.ExpectedPart, c.Operator?.Name, Pending: pending,
                    PrefillQuantity: c.State == ChangeState.AwaitingQuantity ? c.PrefillQuantity : null,
                    OldReelPart: c.OldReelPart, OldReelUid: c.OldReelUid,
                    NewReelPart: c.NewReelPart, NewReelUid: c.NewReelUid, Quantity: c.Quantity,
                    ProbableFalse: cFalse, FeederRemaining: cRem);
            }

            if (_scan is { } s)
                return new SessionSnapshot(s.Purpose.ToString(), s.State.ToString(), _lastMessage,
                    s.Current?.Machine, s.Current?.Feeder, s.Current?.ExpectedPart, s.Operator?.Name,
                    Checklist: s.Items.Select(i => (object)new
                    {
                        machine = i.Machine, feeder = i.Feeder, expected = i.ExpectedPart,
                        scanned = i.ScannedPart, uid = i.ReelUid, status = i.Status.ToString()
                    }).ToList(), Pending: pending, Confirmed: s.ConfirmedMachines.ToList());

            if (_recount is { } r)
                return new SessionSnapshot("Recount", r.State.ToString(), _lastMessage, null, r.Feeder,
                    r.ExpectedPart, r.Operator?.Name, Pending: pending);

            if (_modelChange is { } m)
                return new SessionSnapshot("ModelChange", m.State.ToString(), _lastMessage,
                    m.Current?.Machine, m.Current?.Feeder, m.Current?.ExpectedPart, m.Operator?.Name,
                    Checklist: m.Items.Select(i => (object)new
                    {
                        machine = i.Machine, feeder = i.Feeder, expected = i.ExpectedPart,
                        scanned = i.ScannedPart, uid = i.ReelUid, status = i.PartStatus.ToString(),
                        currentQty = i.CurrentQty, confirmedQty = i.ConfirmedQty, qty = i.QtyOutcome.ToString()
                    }).ToList(),
                    PrefillQuantity: m.State == ModelChangeState.ConfirmingQty ? m.Current?.CurrentQty : null,
                    Pending: pending, Confirmed: m.ConfirmedMachines.ToList());

            string idle = pending.Count > 0
                ? $"{pending.Count} parts-out waiting — badge in to handle."
                : (_lastMessage.Length > 0 ? _lastMessage : "No active task.");
            return new SessionSnapshot("None", "Idle", idle, Pending: pending);
        }
    }

    // Fallback qty when a reel has no StockOuts row: some reel UID barcodes embed the quantity.
    // The exact encoding is still TBD, so this returns null for now (behaviour = StockOuts-only) and
    // will parse the qty out of the scanned UID once the barcode format is confirmed.
    private static int? QtyFromUid(string? uid) => null;

    public void Dispose() { _housekeeping.Dispose(); _autoModelTimer.Dispose(); _syncTimer.Dispose(); _dpcTimer.Dispose(); _startupTimer.Dispose(); }
}
