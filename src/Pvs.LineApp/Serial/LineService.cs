using System.Collections.Concurrent;
using Pvs.Core.Config;
using Pvs.Core.Data;
using Pvs.Core.Events;
using Pvs.Core.Runtime;
using Pvs.Core.Verification;
using Pvs.LineApp.Verification;

namespace Pvs.LineApp.Serial;

/// <summary>A parts-out (or notable machine event) for the live feed.</summary>
public sealed record LineEvent(DateTime At, int Machine, int Feeder, string Kind);

/// <summary>
/// Owns the line's serial listeners for the lifetime of the app. On start it opens every configured
/// machine's port (each failure is contained); it exposes the channels and the combined
/// <see cref="LineMonitor"/> for the API/UI to read.
/// </summary>
public sealed class LineService : IHostedService, IDisposable
{
    private readonly LineConfig _config;
    private readonly ILoggerFactory _loggerFactory;
    private readonly List<SerialPortListener> _listeners = new();

    private readonly IReelPartRepository _repo;
    private readonly ILineRecordSink _records;
    private readonly Pvs.LineApp.Inventory.FeederReelStore _reels;

    public LineService(LineConfig config, ILoggerFactory loggerFactory, IReelPartRepository repo, ILineRecordSink records,
        Pvs.LineApp.Inventory.FeederReelStore reels)
    {
        _config = config;
        _loggerFactory = loggerFactory;
        _repo = repo;
        _records = records;
        _reels = reels;
    }

    public SessionCoordinator? Coordinator { get; private set; }
    public Pvs.LineApp.Runtime.DowntimeService? Downtime { get; private set; }
    public Pvs.LineApp.Runtime.StopTrackingService? StopTracking { get; private set; }
    public Pvs.LineApp.Runtime.DbHealthService? DbHealth { get; private set; }
    public Pvs.LineApp.Runtime.ShiftUptimeService? ShiftUptime { get; private set; }
    public Pvs.LineApp.Runtime.CounterReconcilerService? Reconciler { get; private set; }
    public Pvs.LineApp.Inventory.FeederReelStore Reels => _reels;

    private readonly ConcurrentQueue<LineEvent> _events = new();
    private const int MaxEvents = 100;

    // Per-machine receive diagnostics: how many frames decoded, and the last one.
    private readonly ConcurrentDictionary<int, (long Count, string Last, DateTime At)> _rx = new();

    public (long Count, string Last, DateTime At) RxStats(int machine) =>
        _rx.TryGetValue(machine, out var v) ? v : (0, "", default);

    public LineConfig Config => _config;
    public IReadOnlyList<SerialPortListener> Listeners => _listeners;
    public LineMonitor? Monitor { get; private set; }

    /// <summary>Most-recent parts-out events, newest first.</summary>
    public IReadOnlyList<LineEvent> RecentEvents => _events.Reverse().ToList();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var log = _loggerFactory.CreateLogger<SerialPortListener>();
        foreach (var m in _config.Machines.OrderBy(m => m.Machine))
        {
            if (!m.IsSerial) continue;   // manual/off-serial cell (e.g. JUKI): in the feeder list, but no COM port
            var listener = new SerialPortListener(m, _config.Serial, log);
            int machineNo = m.Machine;
            var rxlog = _loggerFactory.CreateLogger<SerialPortListener>();
            listener.Channel.PartsOutDetected += OnPartsOut;
            listener.Channel.MessageReceived += (msg, at) =>
            {
                _rx.AddOrUpdate(machineNo,
                    (1, msg.Payload, at),
                    (_, prev) => (prev.Count + 1, msg.Payload, at));
                // Log the machine's REFUSAL replies (A4/A5/A3) so a report's send→reply timing is visible — this
                // is how we tell a direct A4E00 refusal from an interrupted D0 dump.
                if (msg.Payload.StartsWith("A4", StringComparison.Ordinal) || msg.Payload.StartsWith("A5", StringComparison.Ordinal)
                    || msg.Payload == "A3")
                    rxlog.LogInformation("M{Machine} RECV  [{Payload}]  (collecting={C})", machineNo, msg.Payload, listener.Channel.IsCollectingReport);
            };
            listener.Open();
            _listeners.Add(listener);
        }
        Monitor = new LineMonitor(_listeners.Select(l => l.Channel));
        var remaining = new Pvs.LineApp.Inventory.RemainingStore(
            Path.Combine(AppContext.BaseDirectory, "remaining.json"));
        // Manual (non-serial) machines — e.g. the Line-3 JUKI — get a serial-less channel so their feeders are
        // still tracked + decremented like every other machine. They open no port and send nothing; their feeder
        // decrement is driven off a serial machine's board-completes in the coordinator (the same boards pass
        // through them on the inline line).
        var manualChannels = _config.Machines.Where(m => !m.IsSerial && m.Machine > 0)
            .Select(m => new Pvs.Core.Runtime.MachineChannel(m.Machine, _ => { }))
            .ToList();
        var allChannels = _listeners.Select(l => l.Channel).Concat(manualChannels).ToList();
        Coordinator = new SessionCoordinator(
            _repo, allChannels, _records, _config,
            _loggerFactory.CreateLogger<SessionCoordinator>(), _reels, remaining);
        Downtime = new Pvs.LineApp.Runtime.DowntimeService(this,
            Path.Combine(AppContext.BaseDirectory, "downtime.json"));
        Downtime.Start();
        // Downtime CAPTURE: per-cell parts-exhaust recovery (auto) + operator reason stops by line (manual).
        StopTracking = new Pvs.LineApp.Runtime.StopTrackingService(
            _listeners.Select(l => l.Channel),
            (m, f) => Coordinator?.ExpectedPartAt(m, f),
            _config.ToShiftSchedule(), _config.ToBreakWindows(), _config.StopDetectSeconds,
            Path.Combine(AppContext.BaseDirectory, "stops.json"));
        StopTracking.Start();
        // DB connectivity heartbeat (only when a SQL login is configured) — feeds the line-health rollup AND
        // WhatsApps Danial when NEITHER DB route is reachable (both primary + fallback down).
        var dbAlert = new Pvs.LineApp.Runtime.WhatsAppSender(_config.Alerts.WhatsAppBridgeUrl, _config.Alerts.WhatsAppRecipient);
        DbHealth = new Pvs.LineApp.Runtime.DbHealthService(_repo, _config.Central.HasCredentials,
            dbAlert, _config.LineName, _config.Alerts.DbDownAlert);
        DbHealth.Start();
        try
        {
            ShiftUptime = new Pvs.LineApp.Runtime.ShiftUptimeService(this, _config.ToShiftSchedule(),
                Path.Combine(AppContext.BaseDirectory, "shift-uptime"));
            ShiftUptime.Start();
        }
        catch (Exception ex)
        {
            _loggerFactory.CreateLogger<LineService>().LogWarning(ex, "Shift-uptime sampler failed to start (non-fatal).");
        }
        Reconciler = new Pvs.LineApp.Runtime.CounterReconcilerService(this, _repo,
            _loggerFactory.CreateLogger<Pvs.LineApp.Runtime.CounterReconcilerService>(),
            Path.Combine(AppContext.BaseDirectory, "reconcile"), _config.ReconcileMinutes);
        Reconciler.Start();
        // When a lot finishes, save its final per-feeder machine data (C1Z) to a lot-named text file before the
        // operator resets the machines for the next lot.
        Coordinator.LotFinalizing += lot => Reconciler.SaveLotFinalFeederStats(lot);
        return Task.CompletedTask;
    }

    /// <summary>Re-open a machine's port and re-enable real-time reporting (the "bring online" button).</summary>
    public string Reconnect(int machine)
    {
        var l = _listeners.FirstOrDefault(x => x.Channel.Machine == machine);
        if (l is null) return $"Machine {machine} is not configured.";
        l.Reopen();
        return l.IsOpen
            ? $"Machine {machine}: reconnected on {l.Port}."
            : $"Machine {machine}: port {l.Port} still unavailable (check cable/power).";
    }

    /// <summary>Send C3P to every machine to read its loaded production program (.PWB). Replies land on each channel.</summary>
    public void QueryPrograms() { foreach (var l in _listeners) l.Channel.RequestProgram(); }

    private void OnPartsOut(PartsOutEvent e)
    {
        _events.Enqueue(new LineEvent(e.At, e.Machine, e.Feeder, "parts-out"));
        while (_events.Count > MaxEvents) _events.TryDequeue(out _);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Downtime?.Dispose();
        StopTracking?.Dispose();
        DbHealth?.Dispose();
        ShiftUptime?.Dispose();
        Reconciler?.Dispose();
        foreach (var l in _listeners) l.Dispose();
        _listeners.Clear();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        foreach (var l in _listeners) l.Dispose();
    }
}
