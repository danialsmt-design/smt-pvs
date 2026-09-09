using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pvs.Core.Config;

namespace Pvs.LineApp.Runtime;

/// <summary>
/// The MCS production schedule for THIS line, mirrored on the PVS screen. MCS (NAS, port 8090 — the same address
/// the robot dispatcher lives at) computes the plan for all lines from open POs, measured run rates and what each
/// line is doing now; PVS only READS it (every 5 min, and on demand) and shows the operator the day's lots in order:
/// what is running, what comes next, what is late. Nothing is written back; a fetch failure keeps the last plan
/// and reports the error. Danial 2026-09-09: "pull the MCS scheduling plan to PVS and display each day's plan".
/// </summary>
public sealed class PlanFeedService : BackgroundService
{
    public sealed record PlanItem(string Po, string Model, string Side, int Qty, int Done, int Remaining,
        DateTime Start, DateTime End, DateTime? Due, bool Running, bool Late, bool Held, bool Pinned, bool Moved,
        string Note, IReadOnlyList<(DateTime Start, DateTime End)> Segments);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(40) };
    private readonly LineConfig _config;
    private readonly ILogger<PlanFeedService> _log;
    private readonly object _gate = new();
    private List<PlanItem> _items = new();
    private DateTime? _generatedAt, _fetchedAt;
    private string? _error;
    private bool _online; private string _runningPo = "";
    private readonly SemaphoreSlim _fetchLock = new(1, 1);

    public PlanFeedService(LineConfig config, ILogger<PlanFeedService> log) { _config = config; _log = log; }

    private string? BaseUrl => string.IsNullOrWhiteSpace(_config.Robot.DispatcherUrl) ? null : _config.Robot.DispatcherUrl.TrimEnd('/');
    public bool Enabled => BaseUrl is not null;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!Enabled) { _log.LogInformation("Plan feed disabled (no MCS/dispatcher URL in config)."); return; }
        await Task.Delay(TimeSpan.FromSeconds(10), ct);
        while (!ct.IsCancellationRequested)
        {
            await FetchAsync(ct);
            try { await Task.Delay(TimeSpan.FromMinutes(5), ct); } catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Read the whole-plant plan from MCS and keep this line's items. Safe to call on demand.</summary>
    public async Task<bool> FetchAsync(CancellationToken ct = default)
    {
        if (BaseUrl is null) return false;
        if (!await _fetchLock.WaitAsync(0, ct)) return false;   // a fetch is already running
        try
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(BaseUrl + "/api/schedule", ct));
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var err)) throw new InvalidOperationException(err.GetString());
            var plan = root.GetProperty("plan");
            string me = _config.LineId.ToString();
            var items = new List<PlanItem>(); bool online = false; string runningPo = "";
            foreach (var line in plan.GetProperty("lines").EnumerateArray())
            {
                if (!string.Equals(line.GetProperty("line").GetString()?.Trim(), me, StringComparison.OrdinalIgnoreCase)) continue;
                online = line.TryGetProperty("online", out var on) && on.GetBoolean();
                runningPo = line.TryGetProperty("runningPo", out var rp) ? rp.GetString() ?? "" : "";
                foreach (var it in line.GetProperty("items").EnumerateArray())
                {
                    var segs = new List<(DateTime, DateTime)>();
                    if (it.TryGetProperty("segments", out var sg))
                        foreach (var s in sg.EnumerateArray()) segs.Add((s.GetProperty("start").GetDateTime(), s.GetProperty("end").GetDateTime()));
                    items.Add(new PlanItem(
                        S(it, "po"), S(it, "model"), S(it, "side"), I(it, "qty"), I(it, "done"), I(it, "remaining"),
                        it.GetProperty("start").GetDateTime(), it.GetProperty("end").GetDateTime(),
                        it.TryGetProperty("due", out var due) && due.ValueKind == JsonValueKind.String ? due.GetDateTime() : null,
                        B(it, "running"), B(it, "late"), B(it, "heldByFirstSide"), B(it, "pinned"), B(it, "moved"), S(it, "note"), segs));
                }
            }
            lock (_gate)
            {
                _items = items.OrderBy(i => i.Start).ToList();
                _generatedAt = plan.TryGetProperty("generatedAt", out var g) ? g.GetDateTime() : DateTime.Now;
                _fetchedAt = DateTime.Now; _error = null; _online = online; _runningPo = runningPo;
            }
            return true;
        }
        catch (Exception ex)
        {
            lock (_gate) { _error = ex.Message; }
            _log.LogWarning("Plan feed: MCS schedule not read ({Err}); showing the last plan.", ex.Message);
            return false;
        }
        finally { _fetchLock.Release(); }
    }

    private static string S(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    private static int I(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
    private static bool B(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>The plan for ONE day on this line: every lot with a working segment (or its start/end span) touching
    /// that day, in run order, with the part of it that falls on that day. Plus a 7-day strip of lot counts.</summary>
    public object Snapshot(DateTime day)
    {
        day = day.Date; var next = day.AddDays(1);
        List<PlanItem> items; DateTime? gen, fetched; string? error; bool online; string runningPo;
        lock (_gate) { items = _items; gen = _generatedAt; fetched = _fetchedAt; error = _error; online = _online; runningPo = _runningPo; }
        bool Touches(PlanItem i, DateTime d0, DateTime d1) =>
            i.Segments.Count > 0 ? i.Segments.Any(s => s.Start < d1 && s.End > d0) : (i.Start < d1 && i.End > d0);
        var today = items.Where(i => Touches(i, day, next)).Select(i =>
        {
            var segs = i.Segments.Where(s => s.Start < next && s.End > day).Select(s => (Start: s.Start < day ? day : s.Start, End: s.End > next ? next : s.End)).ToList();
            var from = segs.Count > 0 ? segs.Min(s => s.Start) : (i.Start < day ? day : i.Start);
            var to = segs.Count > 0 ? segs.Max(s => s.End) : (i.End > next ? next : i.End);
            return new
            {
                po = i.Po, model = i.Model, side = i.Side, qty = i.Qty, done = i.Done, remaining = i.Remaining,
                start = i.Start, end = i.End, due = i.Due, dayFrom = from, dayTo = to,
                continuesFromPrevDay = i.Start < day, continuesNextDay = i.End > next,
                running = i.Running, late = i.Late, held = i.Held, pinned = i.Pinned, moved = i.Moved, note = i.Note,
                status = i.Running ? "running" : i.Held ? "waiting first side" : i.Late ? "late" : "planned"
            };
        }).ToList();
        var days = Enumerable.Range(0, 7).Select(k => day.AddDays(k)).Select(d => new
        {
            date = d.ToString("yyyy-MM-dd"), lots = items.Count(i => Touches(i, d, d.AddDays(1))),
            boards = items.Where(i => Touches(i, d, d.AddDays(1))).Sum(i => i.Remaining)
        }).ToList();
        return new
        {
            enabled = Enabled, line = _config.LineId, date = day.ToString("yyyy-MM-dd"),
            generatedAt = gen, fetchedAt = fetched, error, lineOnlineInMcs = online, runningPo,
            totalLots = items.Count, items = today, days
        };
    }
}
