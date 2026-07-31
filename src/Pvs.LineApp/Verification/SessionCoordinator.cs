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

    private readonly Dictionary<(int machine, int feeder), string> _expected = new();
    private readonly Dictionary<(int machine, int feeder), int> _expectedQty = new();  // per-child-board Mount Step (QtyPerUnit); cached with _expected for the forecast rate when the DB is unreachable
    private readonly Dictionary<(int machine, int feeder), PartsOutEvent> _pending = new();
    private readonly object _gate = new();
    private readonly System.Threading.Timer _housekeeping;
    private readonly System.Threading.Timer _autoModelTimer;
    private readonly System.Threading.Timer _syncTimer;
    private readonly System.Threading.Timer _dpcTimer;
    private readonly System.Threading.Timer _startupTimer;   // one-shot: baseline the inventory ~8s after start
    private bool _autoModel;
    private string? _lastAutoLot;   // last production lot key we auto-switched to (so a manual override isn't clobbered)
    private string _currentLotNo = "";   // current production lot number (PONumber) — tags records (esp. consumed reels)
    private string? _manualLotNo;        // operator-picked lot (dropdown); overrides auto. null = follow the current running lot.
    private string? _manualLotModel;     // "model|side" the manual lot was chosen for. The lot APPLIES only for that model
                                         // (dormant, not deleted, for other models) so a model flip never destroys the choice.
    // Supervisor-selected model (dropdown). When set, auto-detect does NOT override it — it VERIFIES it against the
    // machine's C3P program once a machine is online. null = follow the machine (auto-detect).
    private (int ProductId, string Name, string Side)? _manualModel;
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
    // DailyProductionCount writer: PVS appends an incremental board-count row every 30 min (config-gated).
    private long _m4PanelsTotal;          // monotonic panels off the last machine (never reset) — DPC increment source
    private long _dpcWrittenPanels;       // panels already written to DailyProductionCount
    private DateTime _dpcWindowStart;     // start of the current unwritten window (default = no open window)
    private string _dpcLot = "", _dpcModel = "", _dpcSide = "";  // context captured when the window opened
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
        LoadDpcState();       // restore the monotonic M4 total FIRST (the lot count is derived from it) + DPC counters
        LoadLotProgress();    // restore the lot anchor so a restart mid-lot recomputes the count (needs _m4PanelsTotal)
        LoadManualLot();      // restore the operator's manual lot choice so it survives a restart
        LoadManualModel();    // restore a supervisor-pinned model so a restart doesn't drop back to auto-detect
        LoadFeederMapCache(); // per-model feeder maps (local copy of ProductBOM) for offline model selection
        LoadScanProgress();   // restore an in-progress check so a restart never loses scan progress
        LoadCheckStatus();    // restore the last shift/lot/model-change completion so the monitor GREEN survives a restart

        foreach (var ch in _channels.Values)
            ch.PartsOutDetected += OnPartsOut;

        // count completed panels off the LAST machine (line output) toward the current lot
        _lastMachine = _channels.Keys.DefaultIfEmpty(0).Max();
        if (_channels.TryGetValue(_lastMachine, out var lastCh))
            lastCh.BoardCompleted += _ => OnLotBoardComplete();

        _housekeeping = new System.Threading.Timer(_ => { Housekeep(); CheckShiftTrigger(); }, null,
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        _autoModelTimer = new System.Threading.Timer(_ => { _ = AutoDetectModelAsync(); }, null,
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));
        // Every 5 min (first run after 2 min): record live remaining to remaining.json AND, when enabled,
        // mirror those balances to StockOuts.Quantity in the DB.
        _syncTimer = new System.Threading.Timer(_ => { _ = RecordAndSyncAsync(); }, null,
            TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5));
        // Every 30 min: when enabled, append PVS's incremental board count to DailyProductionCount.
        _dpcTimer = new System.Threading.Timer(_ => { _ = FlushProductionCountAsync(); }, null,
            TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30));
        // One-shot ~8s after start (model restored from cache by then): baseline the live inventory so the exhaust
        // forecast is NEVER empty after a restart/changeover — it pulls each loaded reel's remaining from the DB by
        // UID (authoritative), no manual refresh or check needed. Fixes "exhaust not showing" after a restart.
        _startupTimer = new System.Threading.Timer(async _ =>
        {
            try { if (Model is not null) await RefreshInventoryAsync(); }
            catch (Exception ex) { _log.LogDebug(ex, "Startup inventory baseline failed."); }
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
        try
        {
            string? model = null, side = null, source = null;
            string? machineModel = null, machineSide = null;   // machine C3P program only (for verifying a pinned model)

            // 1) machine program via C3P (from any machine that answered a prior query)
            foreach (var ch in _channels.Values)
            {
                if (ParseProgram(ch.ProgramName) is (string m, string s))
                { machineModel = m; machineSide = s; model = m; side = s; source = $"machine {ch.Machine} program"; break; }
            }
            // 2) production system's current lot — used only as the MODEL fallback when no machine answered C3P.
            CurrentLot? lot = null;
            try { lot = await _repo.GetCurrentLotAsync(_config.LineId, ct); } catch { /* DB unreachable */ }
            if (model is null && lot is not null) { model = lot.Model; side = lot.Side?.Trim().ToUpperInvariant() == "B" ? "B" : "A"; source = "production lot"; }

            // refresh the machine program for the next cycle
            foreach (var ch in _channels.Values) ch.RequestProgram();

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
                if (machineModel is not null && _lastAutoLot is not null && _lastAutoLot != (model + "|" + side))
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
                if (po is null && lot is not null && !string.IsNullOrWhiteSpace(lot.LotNo)
                    && string.Equals(lot.Model?.Trim(), model, StringComparison.OrdinalIgnoreCase))
                    po = lot.LotNo;   // default to the production lot ONLY when it is for THIS model (not a stale cross-model lot)
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
            if (po is null && lot is not null && !string.IsNullOrWhiteSpace(lot.LotNo)
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
            foreach (var f in map.Where(f => f.Position.IsAssigned && f.Machine > 0 && f.Position.Number is int))
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

            _expected.Clear();
            foreach (var kv in fresh) _expected[kv.Key] = kv.Value;
            _expectedQty.Clear();
            foreach (var kv in freshQty) _expectedQty[kv.Key] = kv.Value;
            Model = product;
            Side = side;
            _autoModel = false;   // a manual (or auto) select; AutoDetect re-flags it afterwards
            // Cache this model+side's feeder map (from ProductBOM) so it can be re-populated offline later.
            _feederMaps[MapKey(productId, side)] = _expected
                .Select(kv => new ModelCacheFeeder(kv.Key.machine, kv.Key.feeder, kv.Value,
                    _expectedQty.TryGetValue(kv.Key, out var q) ? q : 0)).ToList();
            SaveModelCache();      // remember the model + feeder map so a restart isn't stuck with no model
            SaveFeederMapCache();  // persist the per-model feeder map for offline model selection
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
        lock (_gate) { if (HasActiveSession) return "Finish or cancel the current check before changing the model."; }

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
            $"{Model?.Name} {side}", Supervisor: badge.Name, Overridden: true, LotNo: _currentLotNo), ct);
        _ = AutoDetectModelAsync(ct);   // immediate verify + lot refresh against the machine
        return $"{Model?.Name} ({side}) pinned by {badge.Name}. {msg}";
    }

    /// <summary>Supervisor releases the manual pin and returns the line to auto-detect (REQUIRES an L2+ badge).</summary>
    public async Task<string> ClearManualModelAsync(string badgeUid, CancellationToken ct = default)
    {
        var badge = await _repo.FindBadgeAsync(badgeUid ?? "", ct);
        if (badge is null || !badge.CanReleaseInterlock) return "Scan a SUPERVISOR badge (L2+) to return to auto.";
        lock (_gate) { _manualModel = null; _modelVerify = "unknown"; _modelVerifyDetail = ""; _lastAutoLot = null; }
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

    /// <summary>Produced panels for the current lot run, DERIVED from the monotonic M4 total. Caller holds _gate.</summary>
    private int LotPanels() => (int)Math.Max(0, _m4PanelsTotal - _lotAnchorTotal);

    private void OnLotBoardComplete()
    {
        lock (_gate)
        {
            // Monotonic M4 total (the lot count is DERIVED from this: LotPanels = total - anchor).
            _m4PanelsTotal++;
            if (_dpcWindowStart == default)
            {
                _dpcWindowStart = DateTime.Now;
                _dpcLot = _currentLotNo; _dpcModel = Model?.Name ?? ""; _dpcSide = Side ?? "";
            }
        }
        // Persist the monotonic total every board so the derived lot count is restart-accurate to the last board.
        SaveDpcState();
    }

    // ---- DailyProductionCount writer (PVS as the line's production-count source; config-gated) ----
    private sealed record DpcStateData(long M4PanelsTotal, long WrittenPanels);
    private static string DpcStatePath => System.IO.Path.Combine(AppContext.BaseDirectory, "dpc-state.json");
    private static string DpcShift(DateTime t) =>
        t.TimeOfDay >= new TimeSpan(7, 35, 0) && t.TimeOfDay < new TimeSpan(19, 35, 0) ? "Morning" : "Night";

    private void SaveDpcState()
    {
        DpcStateData d; lock (_gate) d = new DpcStateData(_m4PanelsTotal, _dpcWrittenPanels);
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
            if (d is not null) { _m4PanelsTotal = d.M4PanelsTotal; _dpcWrittenPanels = d.WrittenPanels; }
        }
        catch (Exception ex) { _log.LogDebug(ex, "DPC state load failed."); }
    }

    /// <summary>
    /// Appends one incremental DailyProductionCount row for the boards produced since the last write (config-gated
    /// by WriteProductionCount). Quantity = new panels × per-panel (child boards). Attributed to the window's
    /// captured lot/model/side and the shift at write time. Skips when nothing was produced this window.
    /// </summary>
    public async Task FlushProductionCountAsync(CancellationToken ct = default)
    {
        if (!_config.WriteProductionCount) return;
        long pending; DateTime winStart, winEnd = DateTime.Now;
        string lot, model, side;
        lock (_gate)
        {
            pending = _m4PanelsTotal - _dpcWrittenPanels;
            winStart = _dpcWindowStart == default ? winEnd : _dpcWindowStart;
            lot = _dpcLot; model = _dpcModel; side = _dpcSide;
        }
        if (pending <= 0) return;                                   // nothing produced this window
        if (string.IsNullOrWhiteSpace(lot) || string.IsNullOrWhiteSpace(model)) return;
        int boards = (int)(pending * _config.PanelBoardsFor(model));
        var entry = new ProductionCountEntry(
            winEnd.ToString("yyyy-MM-dd"), winStart.ToString("HH:mm:ss"), winEnd.ToString("HH:mm:ss"),
            model, side, boards, _config.LineId.ToString(), lot, DpcShift(winEnd), "PVS (auto)", "PVS", Guid.NewGuid().ToString());
        try
        {
            int rows = await _repo.InsertProductionCountAsync(entry, ct);
            if (rows > 0)
            {
                lock (_gate) { _dpcWrittenPanels = _m4PanelsTotal; _dpcWindowStart = default; _dpcLot = ""; _dpcModel = ""; _dpcSide = ""; }
                SaveDpcState();
                _log.LogInformation("DPC row: line {Line} {Shift} {Lot} {Model}/{Side} +{Boards} boards.",
                    _config.LineId, DpcShift(winEnd), lot, model, side, boards);
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "DailyProductionCount write failed (pvs_ro INSERT grant?)."); }
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
            lock (_gate) { _lotAnchorTotal = _m4PanelsTotal - (perPanel > 0 ? (int)Math.Round((double)producedBoards / perPanel) : producedBoards); }
            SaveLotProgress();
        }
        await _records.WriteAsync(new VerificationRecord(DateTime.Now, _config.LineName, "LotChange", 0, 0,
            string.IsNullOrWhiteSpace(lotNo) ? "(auto)" : lotNo, Supervisor: badge.Name, Overridden: true,
            Note: producedBoards >= 0 ? $"lot set via dropdown; produced set to {producedBoards} boards" : "lot set via dropdown",
            LotNo: string.IsNullOrWhiteSpace(lotNo) ? _currentLotNo : lotNo), ct);
        _log.LogInformation("Lot set to {Lot} by {Sup} (produced={P}).", string.IsNullOrWhiteSpace(lotNo) ? "(auto)" : lotNo, badge.Name, producedBoards);
        return string.IsNullOrWhiteSpace(lotNo)
            ? $"Lot tracking set to automatic by {badge.Name}."
            : $"Now tracking lot {lotNo} by {badge.Name}" + (producedBoards >= 0 ? $"; produced set to {producedBoards}." : ".");
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
                foreach (var o in await _repo.GetLotOptionsAsync(model!, ct))
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
        bool changed; lock (_gate) changed = lot != _lotCountFor || !string.Equals(side, _lotSideFor, StringComparison.OrdinalIgnoreCase);
        if (!changed) return;   // same lot AND side — keep the running count
        int? target = null;
        try { target = await _repo.GetLotTargetAsync(lot, ct); } catch { /* DB down — leave target null */ }
        lock (_gate) { _lotCountFor = lot; _lotSideFor = side; _lotAnchorTotal = _m4PanelsTotal; _lotExtra = 0; _lotTarget = target; }
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
                endingSoon = remaining is int r && r <= LotEndThreshold
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
    }

    /// <summary>Individual reels issued for the current lot/side (cached) — for the "issued but not loaded" view.</summary>
    public IReadOnlyList<Pvs.Core.Data.IssuedReel> LotIssuedReels()
    {
        lock (_gate) return _lotIssuedReels.ToList();
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

    private string? ExpectedFor(int machine, int feeder) =>
        _expected.TryGetValue((machine, feeder), out var p) ? p : null;

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

    public async Task RefreshInventoryAsync(CancellationToken ct = default)
    {
        Product? model; string side;
        lock (_gate) { model = Model; side = Side ?? "A"; }
        if (model is null) return;

        // Build the feeder map: try the DB with a HARD timeout (the parts-control link drops bursts and can
        // hang ~40s), then FALL BACK to the cached map (_expected/_expectedQty) so a hiccup can't blank inventory.
        var feeders = new List<(int Machine, int Feeder, string Part, int Qty)>();
        var map = await TryReadFeederMapAsync(model.ProductId, side, 8, ct);
        bool fromDb = map is not null;
        if (map is not null)
            feeders = map.Where(f => f.Position.IsAssigned && f.Machine > 0 && f.Position.Number is int)
                         .Select(f => (f.Machine, f.Position.Number!.Value, f.PartNumber, f.QtyPerUnit)).ToList();
        if (!fromDb)
        {
            lock (_gate)
            {
                if (Model?.ProductId == model.ProductId && string.Equals(Side, side, StringComparison.OrdinalIgnoreCase) && _expected.Count > 0)
                    feeders = _expected.Select(kv => (kv.Key.machine, kv.Key.feeder, kv.Value,
                        _expectedQty.TryGetValue(kv.Key, out var q) ? q : 0)).ToList();
            }
            if (feeders.Count == 0) { _log.LogWarning("Inventory baseline skipped — DB unreachable and no cached feeder map for {Model} ({Side}).", model.Name, side); return; }
            _log.LogInformation("Inventory baselined from CACHED feeder map ({N} feeders) — DB unreachable.", feeders.Count);
        }

        int panels = _config.PanelBoardsFor(model.Name);   // child boards per panel (a cycle mounts a full panel)
        foreach (var ch in _channels.Values) ch.Inventory.Clear();
        foreach (var f in feeders)
        {
            if (!_channels.TryGetValue(f.Machine, out var ch)) continue;

            int perBoard = (f.Qty > 0 ? f.Qty : 1) * panels;   // per-cycle consumption = per-board × panel
            ch.Inventory.Configure(f.Feeder, f.Part, perBoard);

            var reel = _reels.Get(f.Machine, f.Feeder);
            if (reel is null || string.IsNullOrWhiteSpace(reel.Uid)) continue;
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
        _log.LogInformation("Inventory baselined for {Model} ({Side}), {N} feeders (source={Src}).", model.Name, side, feeders.Count, fromDb ? "DB" : "cache");
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
    }

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

    // A shift-change check is due from 5 min after each shift start (07:35 / 19:35), for a grace window,
    // so it can still run once an in-progress changeover finishes instead of being lost at one instant.
    private static readonly TimeSpan ShiftTriggerGrace = TimeSpan.FromMinutes(60);

    /// <summary>"Day"/"Night" if <paramref name="now"/> is inside a shift-change trigger window, else null.</summary>
    private static string? ShiftTriggerSlot(DateTime now)
    {
        var t = now.TimeOfDay;
        var day = new TimeSpan(7, 35, 0);
        var night = new TimeSpan(19, 35, 0);
        if (t >= day && t < day + ShiftTriggerGrace) return "Day";
        if (t >= night && t < night + ShiftTriggerGrace) return "Night";
        return null;
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
        int? rM = null, rF = null;
        lock (_gate)
        {
            _lastActivity = DateTime.Now;
            // capture the feeder this reel is going onto (before the scan advances the cursor)
            if (_change is { State: ChangeState.AwaitingNewReel } cc) { rM = cc.Machine; rF = cc.Feeder; }
            else if (_scan is { State: FullScanState.Scanning } sc) { rM = sc.Current?.Machine; rF = sc.Current?.Feeder; }
            else if (_modelChange is { State: ModelChangeState.Scanning } mc0) { rM = mc0.Current?.Machine; rF = mc0.Current?.Feeder; }

            if (_change is not null)
            {
                _lastMessage = _change.ScanReel(partNumber, uid).Message;
                // New reel verified -> pull its qty from parts control so the operator just confirms (FIRM).
                if (_change.State == ChangeState.AwaitingQuantity)
                { needChangeQty = true; cUid = _change.NewReelUid; cPart = _change.NewReelPart; cFeeder = _change.Feeder; }
            }
            else if (_scan is not null) _lastMessage = _scan.ScanReel(partNumber, uid).Message;
            else if (_recount is not null) _lastMessage = _recount.ScanReel(partNumber, uid).Message;
            else if (_modelChange is not null)
            {
                _lastMessage = _modelChange.ScanReel(partNumber, uid).Message;
                if (_modelChange.State == ModelChangeState.ConfirmingQty && _modelChange.Current is { } cur)
                { needQty = true; qUid = cur.ReelUid; qPart = cur.ScannedPart; qFeeder = cur.Feeder; qMachine = cur.Machine; }
            }
            else _lastMessage = "No task in progress.";
        }

        // Remember which reel is now on that feeder (for the machine-inventory view).
        if (rM is int rm && rF is int rf) _reels.Set(rm, rf, partNumber, uid);

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
        var reel = await _repo.FindReelAsync(uid, partNumber, ct);
        return reel?.RemainingQty;
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
        lock (_gate)
        {
            if (_change is { State: ChangeState.Complete or ChangeState.Skipped } c)
            {
                bool completed = c.State == ChangeState.Complete;
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
