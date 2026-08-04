using Pvs.Core.Events;
using Pvs.Core.Inventory;
using Pvs.Core.Serial;

namespace Pvs.Core.Runtime;

/// <summary>
/// The live processing pipeline for ONE machine/COM port. It takes the raw character stream from
/// the port, reassembles frames, decodes them, and drives the per-machine detectors, rate tracker
/// and inventory — while handling the wire protocol the way the real machines require:
///   - sends C5RO (real-time ON) on start AND again on every R1OL (machine came on-line), because
///     C5RO is rejected while off-line so a single startup send isn't enough (proven on hardware).
///   - auto-acknowledges every R (real-time) and D (data) message with A2, so the machine's 7-second
///     silence timeout never fires.
///   - drops any frame that fails the checksum.
///
/// It is deliberately hardware-free: bytes come in via <see cref="Feed"/> and frames go out via the
/// send callback, so the whole pipeline is unit-testable. The physical SerialPort adapter just wires
/// ReadExisting() -> Feed and the callback -> Write.
/// </summary>
public sealed class MachineChannel
{
    private readonly Action<string> _send;
    private string _buffer = string.Empty;

    public MachineChannel(int machine, Action<string> send, TimeSpan? partsOutDedupe = null, int boardRateWindow = 20)
    {
        Machine = machine;
        _send = send ?? throw new ArgumentNullException(nameof(send));
        PartsOut = new PartsOutDetector(machine, partsOutDedupe);
        BoardRate = new BoardRateTracker(boardRateWindow);
        Inventory = new MachineInventory(machine);
    }

    public int Machine { get; }
    public bool IsOnline { get; private set; }

    /// <summary>The production program (.PWB) name the machine last reported via a C3P query, or null.</summary>
    public string? ProgramName { get; private set; }

    public PartsOutDetector PartsOut { get; }
    public BoardRateTracker BoardRate { get; }
    public MachineInventory Inventory { get; }

    /// <summary>A confirmed, de-duplicated parts-out that should prompt the operator.</summary>
    public event Action<PartsOutEvent>? PartsOutDetected;
    /// <summary>Every decoded message (for logging / higher-level logic).</summary>
    public event Action<SonyMessage, DateTime>? MessageReceived;
    /// <summary>One board finished on this machine.</summary>
    public event Action<DateTime>? BoardCompleted;

    /// <summary>Call once when the port opens: switches the machine to real-time reporting.</summary>
    public void Start() => _send(SonyFrame.Build("C5RO"));

    /// <summary>
    /// Ask the machine which production program is loaded (C3P). The machine replies with a D0 data
    /// message carrying the "&lt;name&gt;.PWB" file name, which lands in <see cref="ProgramName"/>.
    /// (The reply is auto-acked with A2 like any D message, which is the C3P termination the manual requires.)
    /// </summary>
    public void RequestProgram() => _send(SonyFrame.Build("C3P"));

    /// <summary>The machine's own "Number of Completed PWBs" (the <c>PC</c> field of its C1M Production Report) —
    /// its authoritative board counter, which keeps counting even while PVS is off. Null until first read.</summary>
    public int? CompletedPwbs { get; private set; }
    /// <summary>When <see cref="CompletedPwbs"/> was last read from the machine.</summary>
    public DateTime CompletedPwbsAt { get; private set; }
    /// <summary>Raised when a fresh completed-PWB count is read from the machine via C1M.</summary>
    public event Action<int>? ProductionCountRead;

    /// <summary>Board-completes (machine cycles = PANELS) this channel has observed since it started.
    /// PVS's own count — contrast <see cref="CompletedPwbs"/>, the machine's internal counter.</summary>
    public int BoardsSeen { get; private set; }

    /// <summary>The exact C1M command string last sent — for diagnosing rejects.</summary>
    public string? LastReportCommand { get; private set; }
    /// <summary>The machine's rejection/busy acknowledgement to the last C1M request (e.g. <c>A4E00</c>,
    /// <c>A5E02</c>), or null if the request was not refused. Cleared on each new request.</summary>
    public string? LastReportError { get; private set; }

    /// <summary>Raw text of the last C1Z per-supply-location (per-feeder) report exactly as the machine streamed
    /// it — captured verbatim so the field layout can be confirmed before it's parsed. Null until first read.</summary>
    public string? RawSupplyReport { get; private set; }
    /// <summary>When <see cref="RawSupplyReport"/> was last read.</summary>
    public DateTime SupplyReportAt { get; private set; }
    /// <summary>Raised when a fresh C1Z per-feeder report is read from the machine.</summary>
    public event Action<string>? SupplyReportRead;

    // Report collection is shared by C1M (production count) and C1Z (per-supply-location). Only one report is
    // in flight at a time; the kind decides how the accumulated D0 text is parsed when it completes.
    private enum ReportKind { None, Production, Supply }
    private ReportKind _reportKind = ReportKind.None;
    private double _reportTimeoutSec = 8;
    private bool _collectingReport;
    private readonly System.Text.StringBuilder _reportBuf = new();
    private DateTime _reportStart;

    /// <summary>
    /// Ask the machine for its Production Report (<c>C1M000</c> — read WITHOUT clearing) to read PC = completed
    /// PWBs. The reply streams back as ASCII <c>D0</c> lines which we accumulate and ack with <c>A0</c> (the
    /// report protocol) until the empty-<c>D0</c> terminator; the parsed count lands in
    /// <see cref="CompletedPwbs"/> and fires <see cref="ProductionCountRead"/>. Isolated: only D0 report-line
    /// acking changes while a report is in flight — real-time parts-out / board-complete handling is untouched —
    /// and it times out after 8s so a stalled transfer reverts to normal.
    /// </summary>
    /// <param name="pwbName">Optional PWB data-file name WITHOUT its extension (e.g.
    /// <c>L307 - B SIDE _Cell4</c>, from <see cref="ProgramName"/> minus <c>.PW4</c>) to get the summary for
    /// that one lot. Omit for the Entire Machine Status.</param>
    public void RequestProductionCount(DateTime now, string? pwbName = null)
    {
        _reportBuf.Clear();
        _collectingReport = true;
        _reportKind = ReportKind.Production;
        _reportTimeoutSec = 8;
        _reportStart = now;
        LastReportError = null;
        // SI-F manual 6.2.2: "C1Mmmm" ALONE = Entire Machine Status ("If the data name is not added, the
        // overall machine status is loaded"); "C1MmmmP<data name>" = summary for that one PWB file. The P is
        // part of the data-name form, NOT a mandatory separator — sending a bare "C1M000P" asks for a PWB
        // file with an empty name, which the machines reject with A4E00. mmm=000 reads WITHOUT clearing.
        var cmd = string.IsNullOrWhiteSpace(pwbName) ? "C1M000" : "C1M000P" + pwbName.Trim();
        LastReportCommand = cmd;
        _send(SonyFrame.Build(cmd));
    }

    /// <summary>
    /// Ask the machine for its Production Report "Summary by Supply Location" (<c>C1Z000</c> entire machine, or
    /// <c>C1Z000P&lt;pwb name&gt;</c> for one PWB file) — the per-feeder pickup/loss counts (attempted vs
    /// successful pickups, miss/abnormal/recognition errors, parts-out times). Same streaming protocol as C1M;
    /// the raw report text lands verbatim in <see cref="RawSupplyReport"/> and fires <see cref="SupplyReportRead"/>.
    /// Uses a longer window than C1M by default — a per-feeder report is one record per supply location, so it
    /// is much larger than the single PC field.
    /// </summary>
    public void RequestSupplyReport(DateTime now, string? pwbName = null, double timeoutSec = 60)
    {
        _reportBuf.Clear();
        _collectingReport = true;
        _reportKind = ReportKind.Supply;
        _reportTimeoutSec = timeoutSec;
        _reportStart = now;
        LastReportError = null;
        var cmd = string.IsNullOrWhiteSpace(pwbName) ? "C1Z000" : "C1Z000P" + pwbName.Trim();
        LastReportCommand = cmd;
        _send(SonyFrame.Build(cmd));
    }

    /// <summary>Parse/store the finished report text according to what was requested, then reset the kind.</summary>
    private void FinishReport(string text, DateTime now)
    {
        if (_reportKind == ReportKind.Production)
        {
            if (SonyProductionReport.CompletedPwbs(text) is int v) { CompletedPwbs = v; CompletedPwbsAt = now; ProductionCountRead?.Invoke(v); }
        }
        else if (_reportKind == ReportKind.Supply)
        {
            RawSupplyReport = text; SupplyReportAt = now; SupplyReportRead?.Invoke(text);
        }
        _reportKind = ReportKind.None;
    }

    /// <summary>Feed a chunk of received characters (e.g. from SerialPort.ReadExisting()).</summary>
    public void Feed(string chunk, DateTime now)
    {
        _buffer += chunk;
        var frames = SonyFrame.Extract(_buffer, out var remainder);
        _buffer = remainder;

        foreach (var raw in frames)
        {
            if (SonyFrame.TryParse(raw, out var payload) && payload is not null)
                Handle(SonyMessage.Parse(payload), now);
            // else: bad checksum / malformed -> dropped
        }
    }

    private void Handle(SonyMessage msg, DateTime now)
    {
        // --- C1M production-report collection (isolated) ---
        // While a report streams in, its D0 lines are report DATA: accumulate and ack with A0 (the report
        // protocol), NOT the usual A2, until the terminator (an empty D0). Only D0 report-line acking changes;
        // real-time R messages (parts-out / board-complete) still flow through the normal path below, so the
        // safety functions are untouched. Times out after 8s so a stalled/failed transfer reverts to normal.
        if (_collectingReport)
        {
            bool timedOut = (now - _reportStart) > TimeSpan.FromSeconds(_reportTimeoutSec);
            if (!timedOut && msg.Payload.StartsWith("D0", StringComparison.Ordinal))
            {
                string data = msg.Payload.Length > 2 ? msg.Payload[2..] : string.Empty;
                if (data.Length == 0)   // terminator: finalize, ack the end (A0) then close the transfer (A2)
                {
                    _collectingReport = false;
                    _send(SonyFrame.Build("A0"));
                    _send(SonyFrame.Build("A2"));
                    FinishReport(_reportBuf.ToString(), now);
                }
                else { _reportBuf.Append(data); _send(SonyFrame.Build("A0")); }
                return;   // report line handled — skip normal processing / A2 ack
            }
            // The machine refused (A3 / A4xx rejected, A5xx busy-retry) — that ENDS the transfer. Record the
            // code so a failed read says why instead of silently returning nothing. Falls through to normal
            // processing (no return) so the message is still seen by everything else.
            if (!timedOut && (msg.Payload.StartsWith("A4", StringComparison.Ordinal) ||
                              msg.Payload.StartsWith("A5", StringComparison.Ordinal) ||
                              msg.Payload.StartsWith("A3", StringComparison.Ordinal)))
            {
                LastReportError = msg.Payload;
                _collectingReport = false;
                _reportKind = ReportKind.None;
            }

            if (timedOut)   // salvage whatever arrived so far, then process this msg normally
            {
                FinishReport(_reportBuf.ToString(), now);
                _collectingReport = false;
            }
        }

        MessageReceived?.Invoke(msg, now);

        // C3P reply: the loaded production program name arrives as a D0 data message. The extension is
        // cell-specific — ".PW1".."PW4" per machine, or ".PWB" — so match ".PW" generally.
        if (msg.Payload.StartsWith("D0", StringComparison.Ordinal) &&
            msg.Payload.Contains(".PW", StringComparison.OrdinalIgnoreCase))
            ProgramName = msg.Payload[2..].Trim();

        // Track on-line state. R1OL is the explicit "came on-line" transition — re-enable real-time
        // there. But a machine that was ALREADY on-line before we connected won't send R1OL, so also
        // infer on-line from any evidence it's running/reporting (real-time valid, AUTO, started, or
        // a board completing). R1FL is the explicit off-line transition.
        if (msg.Kind == MessageKind.Status && msg.StatusCode is "OL")
        {
            IsOnline = true;
            _send(SonyFrame.Build("C5RO"));
        }
        else if (msg.Kind == MessageKind.Status && msg.StatusCode is "FL")
        {
            IsOnline = false;
        }
        else if (msg.Kind == MessageKind.BoardComplete ||
                 (msg.Kind == MessageKind.Status && msg.StatusCode is "RO" or "AU" or "ST"))
        {
            IsOnline = true;
        }

        BoardRate.Observe(msg, now);

        if (msg.Kind == MessageKind.BoardComplete)
        {
            BoardsSeen++;
            Inventory.OnBoardComplete();
            BoardCompleted?.Invoke(now);
        }

        if (PartsOut.Observe(msg, now) is PartsOutEvent e)
            PartsOutDetected?.Invoke(e);

        // Acknowledge real-time (R) and data (D) messages so the 7s timeout never fires.
        if (msg.Payload.Length > 0 && msg.Payload[0] is 'R' or 'D')
            _send(SonyFrame.Build("A2"));
    }
}
