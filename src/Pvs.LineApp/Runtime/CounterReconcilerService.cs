using System.Text.Json;
using Pvs.Core.Data;
using Pvs.Core.Runtime;
using Pvs.LineApp.Serial;

namespace Pvs.LineApp.Runtime;

/// <summary>One machine's line in a reconciliation run, as persisted and served to the UI.</summary>
public sealed record ReconcileRow(
    int Machine,
    string Status,
    int? MachineCount,
    int PvsCount,
    int? PreviousMachineCount,
    int Delta,
    string Message,
    string? Error,
    string? Pwb,
    // Cumulative real-time messages PVS is confident it missed on this machine (transaction-ID gaps) — a
    // serial blind spot. Non-zero is why the live count could be short and the machine's own C1M is the truth.
    int SuspectedMissed = 0);

/// <summary>
/// The four-way accuracy check for the run (lot size vs PVS vs machine vs operator), flattened for the JSONL
/// and the UI. Every figure is in BOARDS. <see cref="Findings"/> holds every discrepancy, not just the
/// headline <see cref="Status"/>, so nothing is lost to the summary.
/// </summary>
public sealed record LotAccuracyRow(
    string Scope,
    string LotNo,
    string Side,
    int Line,
    string Status,
    int PvsBoards,
    int? MachineBoards,
    int? OperatorBoards,
    int? LotSizeBoards,
    string LotSizeSource,
    bool NeedsSupervisor,
    bool Adopted,
    string? AdoptBlockedBecause,
    string Message,
    List<string> Findings,
    string? LastOperator);

/// <summary>A whole reconciliation pass across the line.</summary>
public sealed record ReconcileRun(
    DateTime At,
    string LotNo,
    string Trigger,
    List<ReconcileRow> Machines,
    int? SpreadMin,
    int? SpreadMax,
    int? Spread,
    LotAccuracyRow? Accuracy = null);

/// <summary>
/// Periodically reads every machine's OWN production counter (Sony <c>C1M</c>) and reconciles it against
/// PVS's live board count, because the two sources fail in opposite ways: PVS misses boards whenever it is
/// off or a serial link drops, while the machine's counter is RESET by the operator at the end of every lot
/// (all four machines, per written SOP).
/// <para>
/// The heartbeat exists mainly because <b>the reset destroys the evidence</b>: read only after the operator
/// resets and that lot's final count is gone for good. A periodic read bounds the worst case to the last
/// interval instead of the whole lot.
/// </para>
/// <para>
/// Each pass also runs the FOUR-way accuracy check (<see cref="LotAccuracyCheck"/>) for the running lot:
/// the order's size from DeliveryDocuments, PVS's own count, the last machine's counter, and the boards the
/// OPERATOR's application recorded in DailyProductionCount. Those four should agree; when they don't, the
/// disagreement is logged with enough context for a supervisor to act. The operator's figure is CREDIBLE and
/// is never rewritten — the one and only automatic correction is PVS adopting the last machine's count when
/// the machine read ahead of it, and even that is refused without a known lot size.
/// </para>
/// <para>
/// This service only OBSERVES and RECORDS — it never rewrites PVS's counters directly. Every run is appended
/// to a daily JSONL so the discrepancies themselves become data (they show where PVS goes blind and whether
/// the reset SOP is actually followed).
/// </para>
/// </summary>
public sealed class CounterReconcilerService : IDisposable
{
    // A C1M report takes ~30s to stream at 9600 baud, so give it room. Machines have separate COM
    // ports, so all of them are read at once rather than one after another.
    private static readonly TimeSpan ReadWindow = TimeSpan.FromSeconds(45);

    private readonly LineService _line;
    private readonly IReelPartRepository _repo;
    private readonly ILogger _log;
    private readonly string _dir;
    private readonly int _intervalMinutes;
    private readonly object _gate = new();
    // machine -> its counter at our last read. Keyed by MACHINE, not by lot: the counter is a whole-machine
    // total that spans lots, so keying it per lot would throw away the backwards step that reveals a reset.
    private readonly Dictionary<int, int> _previous = new();
    private readonly List<ReconcileRun> _recent = new();
    private Timer? _timer;
    private volatile bool _running;
    private Timer? _supplyTimer;       // automatic per-feeder (C1Z) capture rotation
    private int _supplyRotation;       // round-robin machine index
    private int _supplyBusy;           // 0/1 Interlocked guard so ticks never overlap

    /// <summary>Set while an OPERATOR manual read (C1M+C1Z from the inventory page) owns the serial pipeline — the
    /// background rotation stands aside so it never contends with the operator's read (highest-priority command).</summary>
    public bool ManualReadActive { get; set; }

    // Master on/off switches for the AUTOMATIC C1Z rotation and automatic C1M reads. When a switch is OFF, PVS
    // never fires that command on its own — it is read ONLY on demand from the inventory page. Runtime-togglable
    // from the setup page: the setter persists to auto-commands.json and takes effect on the next tick (no restart).
    private volatile bool _autoC1z = true, _autoC1m = true;
    public bool AutoC1z { get => _autoC1z; set { _autoC1z = value; SaveAutoFlags(); } }
    public bool AutoC1m { get => _autoC1m; set { _autoC1m = value; SaveAutoFlags(); } }
    // Machine-tally sync from C1Z (off | shadow | apply) — see LineConfig.C1zTallySync. Same persistence as the switches.
    private volatile string _tallySync = "off";
    public string TallySyncMode { get => _tallySync; set { _tallySync = NormalizeTally(value); SaveAutoFlags(); } }
    private static string NormalizeTally(string? v) => (v ?? "").Trim().ToLowerInvariant() switch { "apply" => "apply", "shadow" => "shadow", _ => "off" };
    private string AutoFlagsPath => Path.Combine(_dir, "auto-commands.json");
    private sealed record AutoFlags(bool c1z, bool c1m, string? tally = null);
    private void SaveAutoFlags()
    {
        try { Directory.CreateDirectory(_dir); File.WriteAllText(AutoFlagsPath, JsonSerializer.Serialize(new AutoFlags(_autoC1z, _autoC1m, _tallySync))); }
        catch (Exception ex) { _log.LogDebug(ex, "auto-commands save failed."); }
    }
    private void LoadAutoFlags()
    {
        _autoC1z = _line.Config.AutoC1z; _autoC1m = _line.Config.AutoC1m;   // config default...
        _tallySync = NormalizeTally(_line.Config.C1zTallySync);
        try
        {
            if (File.Exists(AutoFlagsPath) &&
                JsonSerializer.Deserialize<AutoFlags>(File.ReadAllText(AutoFlagsPath)) is { } d)
            { _autoC1z = d.c1z; _autoC1m = d.c1m; if (d.tally is not null) _tallySync = NormalizeTally(d.tally); }   // ...overridden by the operator's last runtime choice
        }
        catch (Exception ex) { _log.LogDebug(ex, "auto-commands load failed."); }
        _log.LogInformation("Auto-command switches: C1Z rotation={C1z}, C1M reads={C1m}, machine-tally sync={Tally}.", _autoC1z, _autoC1m, _tallySync);
    }
    // Debounce for gap-triggered passes (transaction-ID blind spots), so a burst of gaps can't stack up reads.
    private DateTime _lastGapRun;

    // Feeder pickup-rate alert: WhatsApp the manager when a feeder's C1Z pickup rate drops below the threshold.
    // ONCE per feeder per lot/reel (dedup key includes lot + reel UID, so it re-arms on a lot or reel change).
    private readonly HashSet<string> _pickupAlerted = new();
    private WhatsAppSender? _pickupAlertSender;

    // Board-complete-triggered C1M capture: read the machine report in the CLEAR WINDOW right after a board
    // completes (Appendix F — the head is between motions, so C1M is "Possible"; during active mounting it is
    // Head-Operation/Wait-Position → A4E00). Per-machine debounce + a busy guard so captures never overlap.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte> _capBusy = new();
    private readonly Dictionary<int, DateTime> _lastReportCap = new();

    // Per-machine C1M baseline for the CURRENT lot, so the report shows SUM-BY-LOT (this lot's numbers) instead of
    // the machine's lifetime accumulation (Danial: "tabulate by lot, ignore the accumulated"). Baseline = the C1M
    // field values captured when the current lot was first seen; per-lot value = latest − baseline. Persisted so a
    // restart mid-lot keeps the lot's own start point.
    public static readonly string[] LotDeltaFields =
        { "PC","VC","TC","MC","DC","RC","EP","MP","TP","PP","BP","AT","MT","NT","WT","DT","ET","GT","BT","ST","PT" };
    private sealed record C1mBaseline(string Lot, Dictionary<string, long> Fields, DateTime At);
    private readonly Dictionary<int, C1mBaseline> _c1mBase = new();
    private string C1mBasePath => Path.Combine(_dir, "c1m-baseline.json");

    /// <summary>Parse the C1M report's delta fields into a dictionary (only fields present).</summary>
    private static Dictionary<string, long> ParseC1m(string raw)
    {
        var d = new Dictionary<string, long>();
        foreach (var c in LotDeltaFields)
        {
            var v = Pvs.Core.Serial.SonyProductionReport.Field(raw, c);
            if (v is not null) d[c] = v.Value;
        }
        return d;
    }

    /// <summary>
    /// SUM-BY-LOT view of a machine's C1M report: current reading minus the lot-start baseline. Re-baselines on a
    /// new lot (or first sighting). Returns per-lot field values (attempted/successful/completed/errors/times for
    /// THIS lot only) — never the lifetime accumulation.
    /// </summary>
    public Dictionary<string, long> PerLotFields(int machine, string lot, string raw)
    {
        var cur = ParseC1m(raw);
        lock (_gate)
        {
            if (!_c1mBase.TryGetValue(machine, out var b) || !string.Equals(b.Lot, lot, StringComparison.Ordinal))
            {
                b = new C1mBaseline(lot ?? "", new Dictionary<string, long>(cur), DateTime.Now);
                _c1mBase[machine] = b;
                SaveC1mBase();
            }
            var delta = new Dictionary<string, long>();
            foreach (var kv in cur)
                delta[kv.Key] = Math.Max(0, kv.Value - (b.Fields.TryGetValue(kv.Key, out var bv) ? bv : 0));
            return delta;
        }
    }

    private void SaveC1mBase()
    {
        try { Directory.CreateDirectory(_dir); File.WriteAllText(C1mBasePath, JsonSerializer.Serialize(_c1mBase)); }
        catch (Exception ex) { _log.LogDebug(ex, "C1M baseline save failed."); }
    }
    private void LoadC1mBase()
    {
        try
        {
            if (!File.Exists(C1mBasePath)) return;
            var d = JsonSerializer.Deserialize<Dictionary<int, C1mBaseline>>(File.ReadAllText(C1mBasePath));
            if (d is not null) lock (_gate) foreach (var kv in d) _c1mBase[kv.Key] = kv.Value;
        }
        catch (Exception ex) { _log.LogDebug(ex, "C1M baseline load failed."); }
    }

    // C1Z shadow-mode baselines: the first (VC, TC, PVS-remaining) seen for a reel on a feeder, so each capture
    // can compare the MACHINE's pickup-based consumption against PVS's per-board decrement. Observe-only.
    private readonly record struct ShadowBaseline(string Uid, long VcAtLoad, long TcAtLoad, int PvsRemAtLoad, DateTime At);
    private readonly Dictionary<(int Machine, int Feeder), ShadowBaseline> _shadow = new();

    public CounterReconcilerService(LineService line, IReelPartRepository repo, ILogger log, string dir, int intervalMinutes)
    {
        _line = line;
        _repo = repo;
        _log = log;
        _dir = dir;
        _intervalMinutes = intervalMinutes < 1 ? 20 : intervalMinutes;
    }

    public void Start()
    {
        LoadPrevious();
        LoadC1mBase();   // restore the per-lot baseline so a restart mid-lot keeps this lot's start point
        LoadAutoFlags(); // restore the operator's C1Z/C1M auto-command switches (setup page)

        // DIAGNOSTIC: board-trigger capture disabled — testing whether a SINGLE clean C1M read works during
        // production (the hammering hypothesis). Re-enable once the read timing is settled.
        // foreach (var l in _line.Listeners) { int m = l.Channel.Machine; l.Channel.BoardCompleted += _ => OnBoardCaptureTrigger(m); }
        // DIAGNOSTIC: reconcile heartbeat disabled too, so the ONLY C1M on the wire is the one manual test read —
        // to confirm a single clean C1M works every time (no hammering, no contention). Re-enable after.
        // _timer = new Timer(_ => { _ = RunAsync("heartbeat"); }, null,
        //     TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(_intervalMinutes));

        // AUTOMATIC per-feeder (C1Z) capture: rotate ONE machine per tick (no contention — never all-at-once) and
        // send C1Z000P<program name> (the program name is MANDATORY; bare C1Z is refused A4E01). Poll until it
        // lands, retrying past the momentary busy windows, so machine-inventory (feeder pickups) stays fresh with
        // the machine's OWN per-feeder truth. Cycles every ~machines × 45s. Read-only; never writes stock.
        _supplyTimer = new Timer(_ => { _ = CaptureNextSupplyAsync(); }, null,
            TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(45));

        // Feeder pickup-rate alert — check EVERY C1Z landing (auto rotation, manual read, or endpoint) and WhatsApp
        // the manager once when a feeder's pickup rate drops below the threshold. Fire-and-forget; never blocks serial.
        if (_line.Config.Alerts.PickupAlert)
        {
            _pickupAlertSender = new WhatsAppSender(_line.Config.Alerts.WhatsAppBridgeUrl, _line.Config.Alerts.WhatsAppRecipient);
            foreach (var l in _line.Listeners)
            {
                int mno = l.Channel.Machine;
                l.Channel.SupplyReportRead += raw => { try { CheckPickupRates(mno, raw); } catch (Exception ex) { _log.LogDebug(ex, "pickup-rate check failed."); } };
            }
        }

        // Machine-tally sync (C1Z → board tally): evaluate EVERY landed per-feeder report (auto rotation, manual read,
        // endpoint) against that machine's own board tally. "shadow" records what it would correct and touches
        // nothing; "apply" corrects through the operator-HMI-sync path. "off" = nothing happens.
        foreach (var l in _line.Listeners)
        {
            int mno = l.Channel.Machine;
            l.Channel.SupplyReportRead += raw => { try { TallySyncFromReport(mno, raw); } catch (Exception ex) { _log.LogDebug(ex, "machine-tally sync failed."); } };
        }

        // A serial blind spot (transaction-ID gap) means PVS's live count may now be short on that machine, so
        // read the machine's OWN counter (C1M) right away instead of waiting for the next heartbeat. Debounced:
        // a drop can raise a burst of gaps, and a read takes ~45s, so at most one gap-triggered pass per minute.
        foreach (var l in _line.Listeners)
        {
            var ch = l.Channel;
            ch.CountGapSuspected += missed =>
            {
                if (!_autoC1m) return;   // auto C1M reads switched OFF (setup page) — read only on demand
                DateTime now = DateTime.Now;
                lock (_gate)
                {
                    if (_running || (now - _lastGapRun) < TimeSpan.FromMinutes(1)) return;
                    _lastGapRun = now;
                }
                _log.LogWarning("Reconcile: M{Machine} transaction-ID gap — PVS missed ~{Missed} real-time message(s) " +
                    "(serial blind spot). Reading the machine's own C1M counter to recover the truth.", ch.Machine, missed);
                _ = RunAsync("txn-gap");
            };
        }
    }

    /// <summary>The most recent runs, newest first.</summary>
    public IReadOnlyList<ReconcileRun> Recent { get { lock (_gate) return _recent.AsEnumerable().Reverse().ToList(); } }

    public int IntervalMinutes => _intervalMinutes;
    public bool IsRunning => _running;

    /// <summary>
    /// Read every machine's counter and compare it with PVS's count for the current lot.
    /// Safe to call on demand (lot start/end, shift change, after a restart or a machine coming back online).
    /// </summary>
    public async Task<ReconcileRun> RunAsync(string trigger)
    {
        // One pass at a time — overlapping C1M requests on the same port would corrupt each other.
        if (_running)
            return new ReconcileRun(DateTime.Now, _line.Coordinator?.CurrentLotNo ?? "", trigger + " (skipped: already running)",
                new List<ReconcileRow>(), null, null, null);
        _running = true;
        try
        {
            var now = DateTime.Now;
            var listeners = _line.Listeners.ToList();

            // Ask each machine for its ENTIRE-MACHINE production report: a BARE "C1M000", with no trailing P and
            // no PWB name. That is the form the machines on this floor actually answer; the per-PWB-file form
            // ("C1M000P<name>") gets refused. The loaded program name is still recorded on each row for context.
            // Consequence, and it matters: the entire-machine counter is NOT scoped to the lot, so it only means
            // anything relative to the operator's end-of-lot reset — which is exactly why the previous reading is
            // kept and why the lot-size cap guards every adoption.
            var pwbFor = new Dictionary<int, string?>();
            foreach (var l in listeners)
            {
                pwbFor[l.Channel.Machine] = StripExtension(l.Channel.ProgramName);
                l.Channel.RequestProductionCount(now);
            }

            await Task.Delay(ReadWindow);

            // --- retry-on-refusal: turn the intermittent C1M into a reliable read ---
            // A machine that answered A4E00/A5xx just couldn't execute the report AT THAT INSTANT (mid-cycle /
            // panel busy) — it is NOT a dead link. Re-ask only the machines that refused and still have no fresh
            // reading this pass, a few short-spaced attempts, until each executes or the retries run out. A
            // machine that already answered, is offline, or is supervisor-skipped is never retried. This is the
            // whole point of the change: every online machine's OWN counter gets read, not just the ones that
            // happened to be idle when the pass began. Read-only (C1M is a read), and time is only spent when
            // there is actually a laggard.
            int retries = Math.Max(0, _line.Config.ReconcileRetries);
            double retrySec = _line.Config.ReconcileRetrySeconds < 3 ? 12 : _line.Config.ReconcileRetrySeconds;
            for (int attempt = 0; attempt < retries; attempt++)
            {
                var laggards = listeners.Where(l =>
                    !(l.Channel.CompletedPwbs is not null && l.Channel.CompletedPwbsAt >= now)   // no fresh read yet
                    && l.Channel.IsOnline                                                        // dead link won't answer
                    && !(_line.Coordinator?.IsMachineSkipped(l.Channel.Machine) ?? false)        // supervisor-skipped: leave it
                    && l.Channel.LastReportError is string e
                    && (e.StartsWith("A4", StringComparison.Ordinal) || e.StartsWith("A5", StringComparison.Ordinal)))
                    .ToList();
                if (laggards.Count == 0) break;

                _log.LogInformation("Reconcile: C1M retry {Attempt}/{Retries} for machine(s) {Machines} (refused: {Errors}).",
                    attempt + 1, retries, string.Join(",", laggards.Select(l => l.Channel.Machine)),
                    string.Join(",", laggards.Select(l => $"M{l.Channel.Machine}={l.Channel.LastReportError}")));

                var at = DateTime.Now;
                foreach (var l in laggards) l.Channel.RequestProductionCount(at);
                await Task.Delay(TimeSpan.FromSeconds(retrySec));
            }

            int pvsPanels = _line.Coordinator?.CurrentLotPanels ?? 0;
            string lot = _line.Coordinator?.CurrentLotNo ?? "";

            var results = new List<ReconcileResult>();
            var rows = new List<ReconcileRow>();
            lock (_gate)
            {
                foreach (var l in listeners)
                {
                    int m = l.Channel.Machine;
                    int? count = l.Channel.CompletedPwbs;
                    string? err = l.Channel.LastReportError;

                    // Only treat the stored count as THIS pass's reading if it was refreshed during it —
                    // CompletedPwbs is sticky, and a stale value would otherwise look like a fresh read.
                    if (count is not null && l.Channel.CompletedPwbsAt < now) count = null;

                    _previous.TryGetValue(m, out int prevVal);
                    int? prev = _previous.ContainsKey(m) ? prevVal : null;

                    var r = CounterReconciler.Reconcile(m, count, pvsPanels, prev, error: err);
                    results.Add(r);
                    rows.Add(new ReconcileRow(m, r.Status.ToString(), r.MachineCount, r.PvsCount,
                        r.PreviousMachineCount, r.Delta, r.Message, r.Error, pwbFor.GetValueOrDefault(m),
                        l.Channel.SuspectedMissedMessages));

                    if (count is int c) _previous[m] = c;   // only remember real readings
                }
            }

            // ---- the FOUR-way accuracy check for the running lot ----
            // The last machine (Cell4 = PCB-out) is the line's output, so its counter is the one compared against
            // the lot size, PVS's count and the operator's. Keyed on lot + SIDE + LINE, never the lot alone: one
            // lot number legitimately runs on both sides and on two lines at once.
            int lastMachine = listeners.Select(l => l.Channel.Machine).DefaultIfEmpty(0).Max();
            var lastRes = results.FirstOrDefault(r => r.Machine == lastMachine);
            var (accuracy, lastOperator) = await CheckAccuracyAsync(lot, lastRes, lastMachine);

            // The ONLY automatic correction on this path: adopt the last machine's count when it read AHEAD of
            // PVS (PVS missed boards while off/restarting). A LOWER machine value is an operator lot-end reset
            // (a backwards step -> ResetDetected) and is never adopted, so a reset can't wipe the count; an
            // over-lot-size or lot-size-unknown reading is refused by the four-way check before it gets here,
            // and AdoptLotCount applies its own target cap again on the way in.
            // Config-gated (TrustMachineCount, on by default). The operator's figure is NEVER touched.
            bool adopted = false;
            if (_line.Config.TrustMachineCount && _line.Coordinator is not null &&
                accuracy is not null && accuracy.ShouldAdoptMachineCount && lastRes?.MachineCount is int mc)
            {
                adopted = _line.Coordinator.AdoptLotCount(mc, $"reconcile: M{lastMachine} ahead of PVS") >= 0;
            }

            var spread = CounterReconciler.CrossMachineSpread(results);
            var run = new ReconcileRun(DateTime.Now, lot, trigger, rows,
                spread?.Min, spread?.Max, spread?.Spread, ToRow(accuracy, adopted, lastOperator));

            lock (_gate)
            {
                _recent.Add(run);
                while (_recent.Count > 50) _recent.RemoveAt(0);
            }
            Append(run);
            SavePrevious();

            // NOTE: the machine C1M report is NOT captured here anymore — a blind read during a reconcile pass hits
            // the head mid-motion (Appendix F → A4E00). It is captured in the board-complete gap instead
            // (OnBoardCaptureTrigger), the machine's own "Possible" window.
            return run;
        }
        finally { _running = false; }
    }

    /// <summary>
    /// Runs the four-way check for the lot currently on the line and logs the outcome. The two database legs
    /// (the order's size and the operator's recorded boards) are best-effort: a DB outage leaves them unknown,
    /// which the check REPORTS rather than assumes away, and never stops the machine-vs-PVS reconciliation.
    /// Returns the result plus the name of the operator who last wrote a row, for the run record.
    /// </summary>
    private async Task<(LotAccuracyResult? Result, string? LastOperator)> CheckAccuracyAsync(
        string lot, ReconcileResult? lastRes, int lastMachine)
    {
        var coord = _line.Coordinator;
        if (coord is null) return (null, null);

        // Fill in the lot target if the DB was down at lot start — the coordinator's own adopt cap needs it.
        try { await coord.EnsureLotTargetAsync(); }
        catch (Exception ex) { _log.LogDebug(ex, "Accuracy: lot target refresh failed."); }

        // lot + SIDE + LINE. The same lot number runs on both sides and on two lines at once (seen live:
        // HC20789020000 on Line 1 B-side and Line 5 A-side), so the lot alone is not a run.
        var scope = LotScope.For(lot, coord.Side, _line.Config.LineId);

        LotSizeRow? size = null;
        LotBoardTally? tally = null;
        if (scope.HasLot)
        {
            try { size = await _repo.GetLotSizeAsync(scope.Lot); }
            catch (Exception ex) { _log.LogDebug(ex, "Accuracy: lot size read failed."); }
            try { tally = await _repo.GetLotBoardTallyAsync(scope.Lot, scope.Side, _line.Config.LineId); }
            catch (Exception ex) { _log.LogDebug(ex, "Accuracy: operator count read failed."); }
        }

        // No operator rows yet is NOT a disagreement — mid-lot their application simply hasn't written one.
        int? operatorBoards = tally is not null && tally.OperatorRows > 0 ? tally.OperatorBoards : null;

        var result = LotAccuracyCheck.Compare(new LotAccuracyInput(
            scope, coord.CurrentLotPanels, coord.PerPanel,
            lastRes?.MachineCount, lastRes?.PreviousMachineCount, operatorBoards, size,
            lastMachine, lastRes?.Error));

        Report(result, tally);
        return (result, tally?.LastOperator);
    }

    /// <summary>
    /// Logs the check. A discrepancy nobody can act on is the failure this whole thing exists to remove, so
    /// anything needing a person is a WARNING carrying all four figures AND every finding — including the ones
    /// the single headline status hides — plus who last wrote the operator's rows.
    /// </summary>
    private void Report(LotAccuracyResult r, LotBoardTally? tally)
    {
        if (r.Status == AccuracyStatus.Agree)
        {
            _log.LogInformation("Board count OK — {Message}", r.Message);
            return;
        }
        string who = tally?.LastOperator is string op && op.Length > 0 ? $" [last operator row: {op} @ {tally!.LastSenderIp}]" : "";
        if (r.NeedsSupervisor)
            _log.LogWarning("BOARD COUNT {Status} — {Message}{Who}. {Detail}", r.Status, r.Message, who, r.Detail);
        else
            _log.LogInformation("Board count {Status} — {Message}{Who}. {Detail}", r.Status, r.Message, who, r.Detail);
        if (r.AdoptBlockedBecause is string why)
            _log.LogWarning("Board count: machine-count adoption REFUSED — {Why}. PVS keeps its own count (the safe direction).", why);
    }

    /// <summary>Flattens the check for the JSONL/UI. Nothing is dropped: every finding is carried as text.</summary>
    private static LotAccuracyRow? ToRow(LotAccuracyResult? r, bool adopted, string? lastOperator) =>
        r is null ? null : new LotAccuracyRow(
            r.Scope.Key, r.Scope.Lot, r.Scope.Side, r.Scope.Line, r.Status.ToString(),
            r.PvsBoards, r.MachineBoards, r.OperatorBoards, r.LotSizeBoards, r.LotSizeSource.ToString(),
            r.NeedsSupervisor, adopted, r.AdoptBlockedBecause, r.Message,
            r.Findings.Select(f => $"{f.Kind}: {f.Message} -> {f.Action}").ToList(), lastOperator);

    /// <summary>
    /// A board just completed on this machine — the head is now between motions (PWB transport), which Appendix F
    /// marks "Possible" for C1M. Fire the C1M report read RIGHT NOW so it lands in that window. Per-machine
    /// debounced to the reconcile interval, never overlaps a reconcile pass or itself. Runs off the serial thread,
    /// so it must not block — the actual read is on the thread pool.
    /// </summary>
    private void OnBoardCaptureTrigger(int machine)
    {
        if (_running) return;   // don't collide with a reconcile pass's own reads
        var now = DateTime.Now;
        lock (_gate)
        {
            if (_lastReportCap.TryGetValue(machine, out var last)
                && (now - last) < TimeSpan.FromMinutes(Math.Max(1, _intervalMinutes))) return;   // captured recently
        }
        if (!_capBusy.TryAdd(machine, 1)) return;   // a capture for this machine is already in flight
        var l = _line.Listeners.FirstOrDefault(x => x.Channel.Machine == machine);
        if (l is null || l.Channel.IsCollectingReport) { _capBusy.TryRemove(machine, out _); return; }
        // Send C1M NOW — synchronously, in the board-complete instant — so it hits the PWB-transport window before
        // the head restarts (Task.Run would delay it past the gap). Only the polling/storing runs async.
        var at = DateTime.Now;
        l.Channel.RequestProductionCount(at, StripExtension(l.Channel.ProgramName), timeoutSec: 40);
        _ = Task.Run(async () =>
        {
            try { if (await PollAndStoreReportAsync(l, machine, at)) { lock (_gate) _lastReportCap[machine] = DateTime.Now; } }
            catch (Exception ex) { _log.LogDebug(ex, "Board-gap C1M capture failed (M{M}).", machine); }
            finally { _capBusy.TryRemove(machine, out _); }
        });
    }

    /// <summary>Poll for a C1M report (already requested at <paramref name="at"/>) to land, then store the SUM-BY-LOT
    /// report by date/lot. A4E00 = the window was missed; the next board-complete tries again.</summary>
    private async Task<bool> PollAndStoreReportAsync(SerialPortListener l, int machine, DateTime at)
    {
        for (int i = 0; i < 22; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            if (l.Channel.RawProductionReport is { Length: > 0 } raw && l.Channel.CompletedPwbsAt >= at)
            {
                string lot = _line.Coordinator?.CurrentLotNo ?? "";
                var sdir = Path.Combine(AppContext.BaseDirectory, "supply-report");
                string lotDir = Path.Combine(sdir, at.ToString("yyyy-MM-dd"), SafeName(lot));
                try
                {
                    Directory.CreateDirectory(lotDir);
                    string table = FormatMachineReport(machine, lot, at, raw);
                    File.WriteAllText(Path.Combine(lotDir, $"M{machine}.txt"), table);
                    File.WriteAllText(Path.Combine(sdir, $"machine-report-M{machine}-latest.txt"), table);
                }
                catch (Exception ex) { _log.LogDebug(ex, "Machine-report write failed (M{M}).", machine); }
                _log.LogInformation("C1M report captured for M{Machine} in the board-complete gap (SUM BY LOT).", machine);
                return true;
            }
            if (l.Channel.LastReportError is not null) return false;   // refused — missed the window, retry next board
        }
        return false;
    }

    /// <summary>Read every machine's C1Z per-feeder report and save the raw text (overwrite-latest per machine)
    /// to <c>&lt;app&gt;\supply-report\</c>. Phase 1: capture only — no parsing yet.</summary>
    /// <summary>
    /// One tick of the automatic C1Z rotation: capture the NEXT serial machine's per-feeder report and leave it in
    /// the channel's <c>RawSupplyReport</c> for <c>/api/feederstats</c> + machine-inventory to render. Sends
    /// <c>C1Z000P&lt;program name&gt;</c> (program name mandatory — verified on the floor 2026-09-01: bare C1Z is
    /// refused A4E01, WITH the program name it dumps every feeder's VC/TC/MC/DC/PR). One machine per tick so it
    /// never contends with itself or the C1M reconcile; retries past the brief busy windows. Read-only.
    /// </summary>
    private async Task CaptureNextSupplyAsync()
    {
        if (System.Threading.Interlocked.Exchange(ref _supplyBusy, 1) == 1) return;   // a tick is still running
        try
        {
            if (!_autoC1z) return;                       // auto C1Z rotation switched OFF (setup page) — read only on demand
            if (_running || ManualReadActive) return;   // a C1M reconcile pass OR an operator manual read owns the pipeline — stand aside
            var serial = _line.Listeners.ToList();
            if (serial.Count == 0) return;
            // ONE machine per tick, ONE attempt — no retry storm. A refusal (A4E00, head mid-mount) just means "not
            // now": we advance to the next machine on the next tick (~45s) and this machine comes round again a cycle
            // later. Hammering it with rapid retries floods the serial line, starves C3P (→ the pinned-model verify
            // can't confirm) and board-out reception (→ machines read "Starved"), and blocks operator manual reads —
            // all for a per-feeder snapshot that is NOT time-critical. Calm rotation only. (Reverted the 38s retry-
            // into-window experiment 2026-09-02: it caused exactly that floor-wide contention. The deliberate,
            // on-demand read is the inventory-page "Stop machine → Read" button, which owns the line cleanly.)
            var l = serial[Math.Abs(_supplyRotation++) % serial.Count];
            var ch = l.Channel;
            if (!ch.IsOnline) return;                         // offline (A4E02) — skip; try next machine next tick
            var name = StripExtension(ch.ProgramName);
            if (string.IsNullOrWhiteSpace(name)) { ch.RequestProgram(); return; }   // ask C3P now so the NEXT tick can form C1Z000P<name>

            var at = DateTime.Now;
            ch.RequestSupplyReport(at, name, timeoutSec: 12);   // C1Z000P<name> — one shot
            var deadline = at.AddSeconds(13);
            while (DateTime.Now < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(1));
                if (ch.RawSupplyReport is { Length: > 50 } && ch.SupplyReportAt >= at)
                {
                    _log.LogInformation("C1Z per-feeder capture: M{Machine} landed ({Len} bytes).", ch.Machine, ch.RawSupplyReport.Length);
                    return;
                }
                if (ch.LastReportError is string e && (e.StartsWith("A4", StringComparison.Ordinal) || e.StartsWith("A5", StringComparison.Ordinal)))
                    return;   // refused right now — fine; the next tick advances to the next machine
            }
        }
        catch (Exception ex) { _log.LogDebug(ex, "C1Z rotation tick failed."); }
        finally { System.Threading.Interlocked.Exchange(ref _supplyBusy, 0); }
    }

    /// <summary>On a fresh C1Z, WhatsApp the manager when a feeder's pickup rate (PR) is below the configured
    /// threshold — ONCE per feeder per lot/reel, and only once the feeder has enough attempts to be meaningful.
    /// Read-only, fire-and-forget: it never blocks the serial thread and swallows its own errors.</summary>
    private void CheckPickupRates(int machine, string? raw)
    {
        var cfg = _line.Config.Alerts;
        if (!cfg.PickupAlert || _pickupAlertSender is null || !_pickupAlertSender.HasBridge) return;
        if (string.IsNullOrWhiteSpace(raw)) return;
        var rep = Pvs.Core.Serial.SonySupplyReport.Parse(raw);
        if (rep.Feeders is null || rep.Feeders.Count == 0) return;

        var coord = _line.Coordinator;
        string lot = coord?.CurrentLotNo ?? "";
        var recipients = (cfg.PickupAlertRecipients ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (recipients.Length == 0) return;

        foreach (var f in rep.Feeders)
        {
            if (f.Attempted < cfg.PickupAlertMinPicks) continue;          // too few picks to judge
            double rate = f.RateHundredthsPct / 100.0;
            if (rate <= 0 || rate >= cfg.PickupAlertRatePct) continue;    // only a valid, below-threshold rate

            var fs = coord?.FeederStateOf(machine, f.SupplyLocation);
            string part = fs?.PartNumber ?? "?";
            string uid = fs?.ReelUid ?? "";
            string key = $"{_line.Config.LineId}|{machine}|{f.SupplyLocation}|{lot}|{uid}";
            lock (_gate) { if (!_pickupAlerted.Add(key)) continue; }      // already alerted this feeder for this lot/reel

            long lost = f.Attempted - f.Successful;
            string msg = $"⚠️ PVS pickup alert\n{_line.Config.LineName} · M{machine} · F{f.SupplyLocation}\n" +
                         $"{part}\nPickup {rate:0.00}% (below {cfg.PickupAlertRatePct}%)\n" +
                         $"{f.Attempted} picks, {lost} not placed (miss {f.Missed} + abn {f.Abnormal})" +
                         (string.IsNullOrWhiteSpace(lot) ? "" : $"\nLot {lot}");
            var snd = _pickupAlertSender;
            _ = Task.Run(async () => { foreach (var r in recipients) await snd.SendToAsync(r, msg); });
            _log.LogWarning("Pickup-rate alert: {Line} M{M} F{F} {Part} {Rate}% ({Att} picks) — WhatsApp to {Rcpt}.",
                _line.Config.LineName, machine, f.SupplyLocation, part, rate.ToString("0.00"), f.Attempted, string.Join(",", recipients));
        }
    }

    /// <summary>Hand a landed C1Z to the coordinator's machine-tally sync (per-feeder successful pickups only).</summary>
    private void TallySyncFromReport(int machine, string? raw)
    {
        string mode = _tallySync;
        if (mode == "off" || string.IsNullOrWhiteSpace(raw)) return;
        var coord = _line.Coordinator;
        if (coord is null) return;
        var rep = Pvs.Core.Serial.SonySupplyReport.Parse(raw);
        if (rep.Feeders is null || rep.Feeders.Count == 0) return;
        coord.TallySyncFromMachine(machine, rep.Feeders.Select(f => (f.SupplyLocation, f.Successful)).ToList(), mode);
    }

    private async Task CaptureSupplyReportsAsync()
    {
        var listeners = _line.Listeners.ToList();
        var sdir = Path.Combine(AppContext.BaseDirectory, "supply-report");
        string lot = _line.Coordinator?.CurrentLotNo ?? "";
        var written = new HashSet<int>();

        // C1Z is refused (A4E00) intermittently exactly like C1M — and an accepted report streams slowly (each D0
        // line is ACK-round-tripped, often well past a minute). So: RETRY the C1Z on refusal, but never interrupt a
        // machine that accepted and is mid-stream — only re-send to machines that actually refused (LastReportError
        // is A4/A5; a streaming machine has it cleared). Poll until each report lands, and write it the instant it
        // does. Bounded to ~4 min total so a reconcile pass can't run away.
        int attempts = Math.Max(1, _line.Config.ReconcileRetries + 1);
        var overall = DateTime.Now.AddSeconds(240);
        for (int a = 0; a < attempts && written.Count < listeners.Count && DateTime.Now < overall; a++)
        {
            var pending = listeners.Where(l => !written.Contains(l.Channel.Machine)).ToList();
            var at = DateTime.Now;
            // PER-LOT machine report: C1M000P<PWB data name> ("Summary by PWB file / by the lot", manual 6.2.2).
            // C1M — NOT C1Z (Danial): these machines refuse C1Z but answer C1M. The data name is the program name
            // WITHOUT the .PWx extension (the extension is the file type, not part of the name). No name = overall.
            // A4E00 here = transient machine-state block (Appendix F), not the request — the retry loop catches the
            // next clear window. This report is machine-level (PC/VC/TC/MC/DC/RC/PR/EP), not per-feeder.
            foreach (var l in pending)
                l.Channel.RequestProductionCount(at, StripExtension(l.Channel.ProgramName), timeoutSec: 100);

            var attemptDeadline = at.AddSeconds(100);
            while (DateTime.Now < attemptDeadline && DateTime.Now < overall && written.Count < listeners.Count)
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                foreach (var l in pending)
                {
                    int machine = l.Channel.Machine;
                    if (written.Contains(machine)) continue;
                    var raw = l.Channel.RawProductionReport;
                    if (string.IsNullOrEmpty(raw) || l.Channel.CompletedPwbsAt < at) continue;   // C1M report not landed this attempt yet
                    try
                    {
                        // Organise the stored reports BY DATE then BY LOT: supply-report\<yyyy-MM-dd>\<lot>\  (Danial).
                        string safeLot = SafeName(string.IsNullOrWhiteSpace(lot) ? "no-lot" : lot);
                        string lotDir = Path.Combine(sdir, at.ToString("yyyy-MM-dd"), safeLot);
                        Directory.CreateDirectory(lotDir);
                        // 1) parsed, human-readable machine report (completed/attempted/used/errors/rate/parts-out).
                        string table = FormatMachineReport(machine, lot, at, raw);
                        File.WriteAllText(Path.Combine(lotDir, $"M{machine}.txt"), table);
                        // 2) raw report verbatim in the same lot folder (for field-layout diagnostics / re-parsing).
                        File.WriteAllText(Path.Combine(lotDir, $"M{machine}-raw.txt"),
                            $"# captured {at:yyyy-MM-dd HH:mm:ss}  cmd={l.Channel.LastReportCommand}  len={raw.Length}{Environment.NewLine}{raw}");
                        // 3) a flat latest-per-machine snapshot for quick access.
                        File.WriteAllText(Path.Combine(sdir, $"machine-report-M{machine}-latest.txt"), table);
                        written.Add(machine);   // done — don't re-write this machine
                    }
                    catch (Exception ex) { written.Add(machine); _log.LogWarning(ex, "Machine-report write failed for M{Machine}.", machine); }
                }
                // If every still-pending machine has REFUSED (none mid-stream), stop waiting and re-send now.
                bool allRefused = pending.All(l => written.Contains(l.Channel.Machine)
                    || (l.Channel.LastReportError is string e && (e.StartsWith("A4", StringComparison.Ordinal) || e.StartsWith("A5", StringComparison.Ordinal))));
                if (allRefused && written.Count < listeners.Count) break;   // go round for another attempt
            }
        }
        if (written.Count < listeners.Count)
            _log.LogInformation("C1Z capture: {Written}/{Total} machine reports landed (rest refused after {Attempts} attempts).",
                written.Count, listeners.Count, attempts);
    }

    /// <summary>
    /// Persist the FINISHING lot's per-feeder machine data to a lot-named text file on the PVS, before the
    /// operator resets the machines (which starts a fresh C1Z report page and wipes the counts). Writes the
    /// most recent capture PVS holds for each machine — the operator resets per lot, so that cache is this lot's
    /// data. Best-effort; never throws into the lot-change flow. Called from <c>SessionCoordinator.LotFinalizing</c>.
    /// </summary>
    public void SaveLotFinalFeederStats(string lot)
    {
        if (string.IsNullOrWhiteSpace(lot)) return;
        try
        {
            var sdir = Path.Combine(AppContext.BaseDirectory, "supply-report");
            string safeLot = SafeName(lot);
            int saved = 0;
            foreach (var l in _line.Listeners)
            {
                var raw = l.Channel.RawProductionReport;
                if (string.IsNullOrEmpty(raw)) continue;   // never captured this machine — nothing to save
                // BY DATE then BY LOT, dated by when the C1M report was actually captured; "-final" marks the last read.
                DateTime capAt = l.Channel.CompletedPwbsAt == default ? DateTime.Now : l.Channel.CompletedPwbsAt;
                string lotDir = Path.Combine(sdir, capAt.ToString("yyyy-MM-dd"), safeLot);
                Directory.CreateDirectory(lotDir);
                string table = FormatMachineReport(l.Channel.Machine, lot, capAt, raw);
                File.WriteAllText(Path.Combine(lotDir, $"M{l.Channel.Machine}-final.txt"), table);
                saved++;
            }
            _log.LogInformation("Saved finished-lot feeder stats for lot {Lot} ({N} machine file(s)).", lot, saved);
        }
        catch (Exception ex) { _log.LogDebug(ex, "SaveLotFinalFeederStats failed for lot {Lot}.", lot); }
    }

    /// <summary>
    /// SHADOW comparison (observe-only): for each loaded feeder, contrast the MACHINE's own pickup consumption
    /// (attempted VC since the reel was loaded — the confirmed reel-draw-down rule) with what PVS's per-board
    /// decrement currently shows. Writes NOTHING to stock; appends a per-feeder drift line to a daily log so the
    /// C1Z model can be validated on a live line (e.g. the F114 gap) before <see cref="LineConfig.C1zDecrement"/>
    /// is ever turned on. Baselines are (re)established on a new reel UID or a per-lot counter reset.
    /// </summary>
    private void ShadowCompare(int machine, string lot, DateTime at, string raw, string sdir)
    {
        try
        {
            var rep = Pvs.Core.Serial.SonySupplyReport.Parse(raw);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"# SHADOW {_line.Config.LineName} M{machine} lot {lot} @ {at:yyyy-MM-dd HH:mm:ss}  (observe-only, no stock written)");
            sb.AppendLine("Feeder Part            UID              MachUsed  MachAttr  PVSrem  C1Zrem  Drift");
            foreach (var f in rep.Feeders)
            {
                var fs = _line.Coordinator?.FeederStateOf(machine, f.SupplyLocation);
                if (fs is null || !fs.IsTracked || string.IsNullOrWhiteSpace(fs.ReelUid)) continue;   // only loaded, tracked reels
                var key = (machine, f.SupplyLocation);
                string uid = fs.ReelUid!;
                // (Re)baseline on a new reel, a counter reset (VC below the baseline), or first sighting.
                if (!_shadow.TryGetValue(key, out var b) || b.Uid != uid || f.Attempted < b.VcAtLoad)
                {
                    b = new ShadowBaseline(uid, f.Attempted, f.Successful, fs.Remaining, at);
                    _shadow[key] = b;
                }
                long machUsed = Pvs.Core.Inventory.PickupConsumption.Used(f.Successful, b.TcAtLoad);
                long machAttr = Pvs.Core.Inventory.PickupConsumption.Attrition(f.Attempted, b.VcAtLoad, f.Successful, b.TcAtLoad);
                int c1zRem = Pvs.Core.Inventory.PickupConsumption.Remaining(b.PvsRemAtLoad, f.Attempted, b.VcAtLoad);
                int drift = fs.Remaining - c1zRem;   // + = PVS thinks MORE remains than the machine's pickups say
                sb.AppendLine($"{f.SupplyLocation,-6} {fs.PartNumber,-15} {uid,-16} {machUsed,8} {machAttr,9} {fs.Remaining,7} {c1zRem,7} {drift,6}");
                if (System.Math.Abs(drift) >= 30)
                    _log.LogWarning("C1Z shadow M{M} F{F} {Part} ({Uid}): PVS remaining {Pvs} vs machine-pickup {C1z} — drift {Drift} pcs.",
                        machine, f.SupplyLocation, fs.PartNumber, uid, fs.Remaining, c1zRem, drift);
            }
            File.AppendAllText(Path.Combine(sdir, $"shadow-M{machine}-{at:yyyy-MM-dd}.log"), sb.ToString() + Environment.NewLine);
        }
        catch (Exception ex) { _log.LogDebug(ex, "C1Z shadow compare failed (M{M}).", machine); }
    }

    /// <summary>Renders a C1M machine report (PC/VC/TC/MC/DC/RC/PR/EP) as a readable summary — completed PWBs,
    /// attempted vs successful pickups, misses/abnormal/recognition, attrition (MC+DC), rate and parts-out stops.</summary>
    private string FormatMachineReport(int machine, string lot, DateTime at, string raw)
    {
        var pl = PerLotFields(machine, lot, raw);   // SUM BY LOT (this lot only), not the accumulated
        long G(string c) => pl.TryGetValue(c, out var v) ? v : 0;
        long vc = G("VC"), mc = G("MC"), dc = G("DC"), rc = G("RC");
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# {_line.Config.LineName}  M{machine}  lot {lot}  captured {at:yyyy-MM-dd HH:mm:ss}  (C1M000P report — SUM BY LOT)");
        sb.AppendLine($"Boards produced   : {G("PC")}");
        sb.AppendLine($"Attempted pickups : {vc}");
        sb.AppendLine($"Successful pickups: {G("TC")}");
        sb.AppendLine($"Missed (MC)       : {mc}");
        sb.AppendLine($"Abnormal (DC)     : {dc}");
        sb.AppendLine($"Recognition (RC)  : {rc}");
        sb.AppendLine($"Attrition (MC+DC) : {mc + dc}");
        sb.AppendLine($"Pickup rate       : {(vc > 0 ? (vc - mc - dc - rc) * 100.0 / vc : 0):0.00}%");
        sb.AppendLine($"Parts-out stops   : {G("EP")}");
        return sb.ToString();
    }

    /// <summary>Renders one machine's C1Z capture as a readable per-feeder table (part joined from the feeder
    /// list): attempted/successful pickups, missed/abnormal/recognition errors, parts-out stops, pickup rate.</summary>
    private string FormatSupplyTable(int machine, string lot, DateTime at, string raw)
    {
        var rep = Pvs.Core.Serial.SonySupplyReport.Parse(raw);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# {_line.Config.LineName}  M{machine}  lot {lot}  captured {at:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"# tabulation window: {rep.Start:yyyy-MM-dd HH:mm} -> {rep.End:yyyy-MM-dd HH:mm}");
        sb.AppendLine("Feeder  Part            Attempt  Success  Missed  Abnorm  Recog  P-Out   Rate%");
        foreach (var f in rep.Feeders)
        {
            string? part = _line.Coordinator?.FeederStateOf(machine, f.SupplyLocation)?.PartNumber;
            if (part is null && !f.HasActivity) continue;   // skip the machine's empty, unloaded positions
            sb.AppendLine($"{f.SupplyLocation,-7} {part ?? "-",-15} {f.Attempted,7} {f.Successful,8} {f.Missed,7} " +
                          $"{f.Abnormal,7} {f.Recognition,6} {f.PartsOut,6} {f.RateHundredthsPct / 100.0,7:0.00}");
        }
        return sb.ToString();
    }

    /// <summary>Drop the cell-specific extension (".PW1".."PW4"/".PWB") — C1M wants the name only.</summary>
    private static string? StripExtension(string? program)
    {
        if (string.IsNullOrWhiteSpace(program)) return null;
        int dot = program.LastIndexOf('.');
        return dot > 0 ? program[..dot] : program;
    }

    /// <summary>A filesystem-safe folder name (letters, digits, - and _) for a lot number.</summary>
    private static string SafeName(string s)
    {
        string clean = string.Concat((s ?? "").Trim().Where(c => char.IsLetterOrDigit(c) || c is '-' or '_'));
        return clean.Length > 0 ? clean : "no-lot";
    }

    private string LogPath(DateTime at) => Path.Combine(_dir, $"reconcile-{at:yyyy-MM-dd}.jsonl");
    private string PreviousPath => Path.Combine(_dir, "reconcile-previous.json");

    private void Append(ReconcileRun run)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            File.AppendAllText(LogPath(run.At), JsonSerializer.Serialize(run) + Environment.NewLine);
        }
        catch { /* logging is best-effort; never let it disturb the line */ }
    }

    // The previous readings survive a restart, so a reset that happens while PVS is down is still
    // detectable as a backwards step on the next pass.
    private void LoadPrevious()
    {
        try
        {
            if (!File.Exists(PreviousPath)) return;
            var d = JsonSerializer.Deserialize<Dictionary<int, int>>(File.ReadAllText(PreviousPath));
            if (d is null) return;
            lock (_gate) foreach (var kv in d) _previous[kv.Key] = kv.Value;
        }
        catch { /* corrupt -> start without history; the first pass just has no previous */ }
    }

    private void SavePrevious()
    {
        try
        {
            Directory.CreateDirectory(_dir);
            Dictionary<int, int> copy;
            lock (_gate) copy = new Dictionary<int, int>(_previous);
            File.WriteAllText(PreviousPath, JsonSerializer.Serialize(copy));
        }
        catch { /* best-effort */ }
    }

    public void Dispose() { _timer?.Dispose(); _supplyTimer?.Dispose(); }
}
