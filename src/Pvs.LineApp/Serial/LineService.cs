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
            var listener = new SerialPortListener(m, _config.Serial, log);
            int machineNo = m.Machine;
            listener.Channel.PartsOutDetected += OnPartsOut;
            listener.Channel.MessageReceived += (msg, at) =>
                _rx.AddOrUpdate(machineNo,
                    (1, msg.Payload, at),
                    (_, prev) => (prev.Count + 1, msg.Payload, at));
            listener.Open();
            _listeners.Add(listener);
        }
        Monitor = new LineMonitor(_listeners.Select(l => l.Channel));
        var remaining = new Pvs.LineApp.Inventory.RemainingStore(
            Path.Combine(AppContext.BaseDirectory, "remaining.json"));
        Coordinator = new SessionCoordinator(
            _repo, _listeners.Select(l => l.Channel), _records, _config,
            _loggerFactory.CreateLogger<SessionCoordinator>(), _reels, remaining);
        Downtime = new Pvs.LineApp.Runtime.DowntimeService(this,
            Path.Combine(AppContext.BaseDirectory, "downtime.json"));
        Downtime.Start();
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
        foreach (var l in _listeners) l.Dispose();
        _listeners.Clear();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        foreach (var l in _listeners) l.Dispose();
    }
}
