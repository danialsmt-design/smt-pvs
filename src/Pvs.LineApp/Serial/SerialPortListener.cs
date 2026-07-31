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
    }

    public MachineChannel Channel { get; }
    public string Port => _machineCfg.Port;
    public bool IsOpen => _port?.IsOpen ?? false;

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
                Channel.Feed(chunk, DateTime.Now);
        }
        catch (Exception ex)
        {
            _log.LogDebug("Machine {Machine}: read error on {Port} — {Message}",
                _machineCfg.Machine, _machineCfg.Port, ex.Message);
        }
    }

    private void Send(string frame)
    {
        try { _port?.Write(frame); }
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
