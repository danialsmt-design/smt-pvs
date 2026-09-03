using System.IO.Ports;
using Pvs.Core.Config;
using Pvs.Core.Runtime;

namespace Pvs.LineApp.Serial;

/// <summary>
/// The physical adapter between one Windows COM port and its <see cref="MachineChannel"/>.
/// Thin by design — all the real work lives in the (hardware-free, tested) MachineChannel. This
/// wires SerialPort.DataReceived -> ReadExisting() -> channel.Feed and channel's send callback ->
/// SerialPort.Write, mirroring exactly what the LineDiag proof-of-concept did on the real line.
///
/// It never throws to the caller: a port that can't open (unplugged card, port held by another
/// program, wrong name) is logged and left closed, so a fault on one machine can't stop the line PC.
/// </summary>
public sealed class SerialPortListener : IDisposable
{
    private readonly MachineConfig _machineCfg;
    private readonly SerialConfig _serialCfg;
    private readonly ILogger _log;
    private SerialPort? _port;

    public SerialPortListener(MachineConfig machineCfg, SerialConfig serialCfg, ILogger log)
    {
        _machineCfg = machineCfg;
        _serialCfg = serialCfg;
        _log = log;
        Channel = new MachineChannel(machineCfg.Machine, Send);
        // Live trace of decoded replies. Skip the bare 2-char line-acks (A0/A1/A2/A6) so the view stays readable;
        // keep errors (A4E00/A5xx), program answers (D0…), and the real-time production frames (R0/R1/R2).
        Channel.MessageReceived += (msg, _) =>
        {
            var p = msg.Payload;
            bool bareAck = p is { Length: <= 2 } && p.Length > 0 && p[0] == 'A';
            // RX frame in canonical form (STX + count + payload + checksum + ETX) — the wire format the machine
            // uses, so the serial page can show the full frame + checksum for a received reply too.
            if (!bareAck) Trace("RX", p, SafeFrame(p));
        };
    }

    public MachineChannel Channel { get; }
    public string Port => _machineCfg.Port;
    public bool IsOpen => _port?.IsOpen ?? false;

    // Live serial trace: a small per-machine ring of the last TX/RX frames, so the dedicated serial page can show the
    // command-send / reply-receive traffic in real time — payload AND the full on-the-wire frame (hex, incl. STX,
    // count, checksum, ETX). Sequence-numbered so the UI can poll incrementally.
    public readonly record struct SerialTrace(long Seq, DateTime T, string Dir, string Text, string Frame);
    private readonly System.Collections.Concurrent.ConcurrentQueue<SerialTrace> _trace = new();
    private long _traceSeq;
    private const int TraceMax = 250;
    private static string ToHex(string? frame)
    {
        if (string.IsNullOrEmpty(frame)) return "";
        var sb = new System.Text.StringBuilder(frame.Length * 2);
        foreach (char c in frame) sb.Append(((int)c & 0xFF).ToString("X2"));
        return sb.ToString();
    }
    // Rebuild a payload into its canonical Sony frame (for display of a received reply); never throws.
    private static string SafeFrame(string? payload)
    {
        try { return string.IsNullOrEmpty(payload) ? "" : Pvs.Core.Serial.SonyFrame.Build(payload); }
        catch { return ""; }
    }
    private void Trace(string dir, string text, string? frame = null)
    {
        if (string.IsNullOrEmpty(text)) return;
        _trace.Enqueue(new SerialTrace(System.Threading.Interlocked.Increment(ref _traceSeq), DateTime.Now, dir,
            text.Length > 160 ? text[..160] + "…" : text, ToHex(frame)));
        while (_trace.Count > TraceMax && _trace.TryDequeue(out _)) { }
    }
    /// <summary>Trace entries with Seq &gt; <paramref name="afterSeq"/> (0 = all held), oldest-first.</summary>
    public IReadOnlyList<SerialTrace> TraceSince(long afterSeq) =>
        _trace.Where(e => e.Seq > afterSeq).OrderBy(e => e.Seq).ToList();

    public void Open()
    {
        try
        {
            _port = new SerialPort(
                _machineCfg.Port,
                _serialCfg.BaudRate,
                Enum.Parse<Parity>(_serialCfg.Parity, ignoreCase: true),
                _serialCfg.DataBits,
                ParseStopBits(_serialCfg.StopBits))
            {
                Handshake = Handshake.None,
                DtrEnable = true,
                RtsEnable = true,
                ReadTimeout = 500,
                WriteTimeout = 800,
                NewLine = "\r"
            };
            _port.DataReceived += OnDataReceived;
            _port.Open();
            _log.LogInformation("Machine {Machine}: opened {Port} at {Baud} {Bits}{Parity}{Stop}",
                _machineCfg.Machine, _machineCfg.Port, _serialCfg.BaudRate, _serialCfg.DataBits,
                _serialCfg.Parity[..1], _serialCfg.StopBits);
            Channel.Start(); // enable real-time reporting (C5RO)
        }
        catch (Exception ex)
        {
            _log.LogWarning("Machine {Machine}: could not open {Port} — {Message}",
                _machineCfg.Machine, _machineCfg.Port, ex.Message);
            _port = null;
        }
    }

    private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        try
        {
            var chunk = _port?.ReadExisting();
            if (!string.IsNullOrEmpty(chunk))
            {
                // DIAGNOSTIC: log raw report-relevant bytes (D0 report dump lines / A-errors) so we can watch the
                // C1M dump on the wire. Control chars made visible; skips the frequent R0/R1/R2 real-time frames.
                if (chunk.Contains("D0") || chunk.Contains("A4E") || chunk.Contains("A5E") || chunk.Contains("A3"))
                {
                    var vis = chunk.Replace("\x02", "<STX>").Replace("\x03", "<ETX>").Replace("\r", "<CR>");
                    _log.LogInformation("M{Machine} RAW-RX [{Len}]: {Chunk}", _machineCfg.Machine, chunk.Length,
                        vis.Length > 400 ? vis[..400] + "…" : vis);
                }
                Channel.Feed(chunk, DateTime.Now);
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug("Machine {Machine}: read error on {Port} — {Message}",
                _machineCfg.Machine, _machineCfg.Port, ex.Message);
        }
    }

    private void Send(string frame)
    {
        try
        {
            // Log EVERY frame we put on the wire — payload + raw hex — so nothing PVS sends is invisible.
            // STX=02, ETX=03; frame = STX + count(2 hex) + payload + checksum(2 hex) + ETX. Commands (C…) log at
            // Information; the high-frequency A0/A2 acks log at Debug so they're there when wanted but don't flood
            // the default Information log.
            {
                Pvs.Core.Serial.SonyFrame.TryParse(frame, out var payload);
                var hex = new System.Text.StringBuilder();
                foreach (char c in frame) hex.Append(((int)c).ToString("X2"));
                bool isAck = payload is { Length: > 0 } && payload[0] == 'A';
                if (isAck)
                    _log.LogDebug("M{Machine} {Port} SEND  payload=[{Payload}]  frame(hex)={Hex}",
                        _machineCfg.Machine, _machineCfg.Port, payload ?? "(unparsed)", hex.ToString());
                else
                    _log.LogInformation("M{Machine} {Port} SEND  payload=[{Payload}]  frame(hex)={Hex}",
                        _machineCfg.Machine, _machineCfg.Port, payload ?? "(unparsed)", hex.ToString());
                if (!isAck) Trace("TX", payload ?? frame, frame);   // live trace: real commands (+ full frame), not the A0/A2 line-acks
            }
            _port?.Write(frame);
        }
        catch (Exception ex)
        {
            _log.LogDebug("Machine {Machine}: write error on {Port} — {Message}",
                _machineCfg.Machine, _machineCfg.Port, ex.Message);
        }
    }

    private static StopBits ParseStopBits(int stop) => stop switch
    {
        2 => StopBits.Two,
        0 => StopBits.None,
        _ => StopBits.One
    };

    /// <summary>Closes and re-opens the port (and re-enables real-time). Keeps the channel/state.
    /// Useful to bring a machine online after a cable fix without restarting the app.</summary>
    public void Reopen()
    {
        try { if (_port is not null) { _port.DataReceived -= OnDataReceived; _port.Close(); _port.Dispose(); } }
        catch { /* ignore */ }
        _port = null;
        Open();
    }

    public void Dispose()
    {
        try { if (_port is not null) { _port.DataReceived -= OnDataReceived; _port.Close(); _port.Dispose(); } }
        catch { /* ignore */ }
        _port = null;
    }
}
