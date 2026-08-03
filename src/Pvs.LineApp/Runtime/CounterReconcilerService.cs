using System.Text.Json;
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
    string? Pwb);

/// <summary>A whole reconciliation pass across the line.</summary>
public sealed record ReconcileRun(
    DateTime At,
    string LotNo,
    string Trigger,
    List<ReconcileRow> Machines,
    int? SpreadMin,
    int? SpreadMax,
    int? Spread);

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
/// This service only OBSERVES and RECORDS — it never rewrites PVS's counters. Every run is appended to a
/// daily JSONL so the discrepancies themselves become data (they show where PVS goes blind and whether the
/// reset SOP is actually followed).
/// </para>
/// </summary>
public sealed class CounterReconcilerService : IDisposable
{
    // A C1M report takes ~30s to stream at 9600 baud, so give it room. Machines have separate COM
    // ports, so all of them are read at once rather than one after another.
    private static readonly TimeSpan ReadWindow = TimeSpan.FromSeconds(45);

    private readonly LineService _line;
    private readonly string _dir;
    private readonly int _intervalMinutes;
    private readonly object _gate = new();
    private readonly Dictionary<int, int> _previous = new();   // machine -> its counter at our last read
    private readonly List<ReconcileRun> _recent = new();
    private Timer? _timer;
    private volatile bool _running;

    public CounterReconcilerService(LineService line, string dir, int intervalMinutes)
    {
        _line = line;
        _dir = dir;
        _intervalMinutes = intervalMinutes < 1 ? 20 : intervalMinutes;
    }

    public void Start()
    {
        LoadPrevious();
        // First pass a couple of minutes in, so the ports and the model/lot have settled after a restart.
        _timer = new Timer(_ => { _ = RunAsync("heartbeat"); }, null,
            TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(_intervalMinutes));
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

            // Ask each machine for the report scoped to ITS OWN loaded PWB file (per-lot), falling back to
            // the whole-machine total when the program name isn't known. The name must be byte-exact and
            // WITHOUT the extension, so it is always derived from C3P — never hand-written.
            var pwbFor = new Dictionary<int, string?>();
            foreach (var l in listeners)
            {
                string? pwb = StripExtension(l.Channel.ProgramName);
                pwbFor[l.Channel.Machine] = pwb;
                l.Channel.RequestProductionCount(now, pwb);
            }

            await Task.Delay(ReadWindow);

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
                        r.PreviousMachineCount, r.Delta, r.Message, r.Error, pwbFor.GetValueOrDefault(m)));

                    if (count is int c) _previous[m] = c;   // only remember real readings
                }
            }

            var spread = CounterReconciler.CrossMachineSpread(results);
            var run = new ReconcileRun(DateTime.Now, lot, trigger, rows,
                spread?.Min, spread?.Max, spread?.Spread);

            lock (_gate)
            {
                _recent.Add(run);
                while (_recent.Count > 50) _recent.RemoveAt(0);
            }
            Append(run);
            SavePrevious();
            return run;
        }
        finally { _running = false; }
    }

    /// <summary>Drop the cell-specific extension (".PW1".."PW4"/".PWB") — C1M wants the name only.</summary>
    private static string? StripExtension(string? program)
    {
        if (string.IsNullOrWhiteSpace(program)) return null;
        int dot = program.LastIndexOf('.');
        return dot > 0 ? program[..dot] : program;
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

    public void Dispose() => _timer?.Dispose();
}
