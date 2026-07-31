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
