using System.Text;
using System.Text.Json;
using Pvs.Core.Config;
using Pvs.Core.Requests;

namespace Pvs.LineApp.Runtime;

/// <summary>
/// Asks the store for material BEFORE a reel runs out. Every minute it reads this line's own exhaust forecast
/// (<c>/api/exhaust</c> - the same numbers the operator screen shows), lets <see cref="PartsRequestPlanner"/>
/// decide, and files/closes parts requests on the MCS app (<c>/api/requests</c>) - where the store terminals
/// display them and the store keeper picks the reels and sends the robot. PVS proposes and reports; it writes no
/// stock. Inert unless <c>robot.dispatcherUrl</c> is set and <c>robot.autoRequest</c> is on.
/// </summary>
public sealed class PartsRequestService : BackgroundService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly LineConfig _config;
    private readonly ILogger<PartsRequestService> _log;
    private readonly string _statePath;
    private readonly object _lock = new();
    // part -> open request (id + when + last known status from MCS)
    private Dictionary<string, OpenRequest> _open = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _closeVotes = new(StringComparer.OrdinalIgnoreCase);
    // part -> when the store DISMISSED its request: don't ask again for that part until the cool-down passes or the lot changes
    private readonly Dictionary<string, DateTime> _dismissed = new(StringComparer.OrdinalIgnoreCase);
    private string _lastLot = "";
    private string _lastError = "";
    private DateTime? _lastRun;

    public sealed class OpenRequest
    {
        public int Id { get; set; }
        public string Part { get; set; } = "";
        public int Machine { get; set; }
        public int Feeder { get; set; }
        public DateTime OpenedAt { get; set; }
        public double MinutesLeftAtOpen { get; set; }
        public int PiecesNeeded { get; set; }
        public string Status { get; set; } = "Open";     // as MCS reports it: Open / Acked / Picking / Sent / Closed / Cancelled
        public int ReelsPicked { get; set; }
        public string LotNo { get; set; } = "";
    }

    public PartsRequestService(LineConfig config, ILogger<PartsRequestService> log)
    {
        _config = config; _log = log;
        _statePath = Path.Combine(AppContext.BaseDirectory, "parts-requests.json");
        Load();
    }

    private RobotConfig Cfg => _config.Robot ?? new RobotConfig();
    public bool Enabled => Cfg.Enabled && Cfg.AutoRequest;

    public object Snapshot()
    {
        lock (_lock)
        {
            return new
            {
                enabled = Enabled, thresholdMinutes = Cfg.RequestMinutes, lastRun = _lastRun, error = _lastError,
                open = _open.Values.OrderBy(o => o.OpenedAt).ToList(),
                dismissed = _dismissed.Select(d => new { part = d.Key, at = d.Value }).ToList(),
            };
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!Enabled) { _log.LogInformation("Parts requests: off (robot.dispatcherUrl empty or robot.autoRequest=false)"); return; }
        _log.LogInformation("Parts requests: on - threshold {min} min, dispatcher {url}", Cfg.RequestMinutes, Cfg.DispatcherUrl);
        try { await Task.Delay(TimeSpan.FromSeconds(45), ct); } catch (OperationCanceledException) { return; }   // let serial + forecast settle
        while (!ct.IsCancellationRequested)
        {
            try { await RunOnceAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _lastError = ex.Message; _log.LogWarning(ex, "Parts requests: pass failed"); }
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(20, Cfg.PollSeconds)), ct); } catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>One pass: forecast -> plan -> file/close on MCS -> refresh statuses. Public so a test/endpoint can drive it.</summary>
    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        _lastRun = DateTime.Now;
        var exhaust = await GetAsync<ExhaustDto>(Cfg.SelfUrl.TrimEnd('/') + "/api/exhaust", ct);
        if (exhaust is null) { _lastError = "could not read /api/exhaust"; return; }
        _lastError = "";

        string lot = exhaust.LotNo ?? "";
        List<string> openParts;
        lock (_lock)
        {
            if (lot != _lastLot && _open.Count > 0)
            {
                // a new lot: everything the old lot asked for is moot; close them all
                foreach (var o in _open.Values.ToList()) _ = CloseOnMcsAsync(o, "lot changed", ct);
                _open.Clear(); _closeVotes.Clear(); Save();
            }
            if (lot != _lastLot) _dismissed.Clear();
            _lastLot = lot;
            // dismissed parts count as "open" for the planner so it does not re-file them during the cool-down
            foreach (var (part, at) in _dismissed.ToList())
                if (DateTime.Now - at > TimeSpan.FromMinutes(Cfg.DismissCooldownMinutes)) _dismissed.Remove(part);
            openParts = _open.Keys.Concat(_dismissed.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        var rows = (exhaust.Rows ?? new()).Select(r => new ExhaustRow(r.Machine, r.Feeder, r.Part ?? "", r.Remaining, r.Minutes,
            r.NeedsRequest, r.SpareReady, r.LastsLot, r.Issued, r.NeededLot)).ToList();
        var plan = PartsRequestPlanner.Plan(rows, openParts, Cfg.RequestMinutes);

        foreach (var o in plan.Open)
        {
            var created = await PostAsync<CreatedDto>(Cfg.DispatcherUrl.TrimEnd('/') + "/api/requests", new
            {
                line = _config.LineId.ToString(), lineName = _config.LineName, lotNo = lot, part = o.Part, machine = o.Machine,
                feeder = o.Feeder, minutesLeft = Math.Round(o.MinutesLeft), piecesNeeded = o.PiecesNeeded, remaining = o.Remaining, by = "PVS",
            }, ct);
            if (created is null || created.Id <= 0) { _lastError = $"MCS did not accept the request for {o.Part}"; continue; }
            lock (_lock)
            {
                _open[o.Part] = new OpenRequest { Id = created.Id, Part = o.Part, Machine = o.Machine, Feeder = o.Feeder, OpenedAt = DateTime.Now,
                    MinutesLeftAtOpen = o.MinutesLeft, PiecesNeeded = o.PiecesNeeded, Status = created.Status ?? "Open", LotNo = lot };
                _closeVotes.Remove(o.Part); Save();
            }
            _log.LogInformation("Parts request #{id} filed: {part} M{m} F{f}, {min} min left, {pcs} pcs needed", created.Id, o.Part, o.Machine, o.Feeder, Math.Round(o.MinutesLeft), o.PiecesNeeded);
        }

        // close only after two consecutive passes agree (a single noisy forecast read must not cancel a pick in progress)
        var stillNeeded = new HashSet<string>(openParts, StringComparer.OrdinalIgnoreCase);
        foreach (var c in plan.Close)
        {
            stillNeeded.Remove(c.Part);
            int votes;
            lock (_lock) { votes = _closeVotes[c.Part] = _closeVotes.TryGetValue(c.Part, out var v) ? v + 1 : 1; }
            if (votes < 2) continue;
            OpenRequest? o;
            lock (_lock) { _open.TryGetValue(c.Part, out o); }
            if (o is null) continue;   // a dismissed (cool-down) part: nothing to close on MCS
            if (await CloseOnMcsAsync(o, c.Reason, ct))
                lock (_lock) { _open.Remove(c.Part); _closeVotes.Remove(c.Part); Save(); }
        }
        lock (_lock) foreach (var p in stillNeeded) _closeVotes.Remove(p);

        // refresh what the store has done with them (acked / picking / sent / cancelled) for the operator chip
        var mine = await GetAsync<List<McsRequestDto>>(Cfg.DispatcherUrl.TrimEnd('/') + $"/api/requests?line={_config.LineId}&open=1", ct);
        if (mine is not null)
        {
            lock (_lock)
            {
                var byId = mine.ToDictionary(m => m.Id);
                foreach (var o in _open.Values.ToList())
                {
                    if (byId.TryGetValue(o.Id, out var m)) { o.Status = m.Status ?? o.Status; o.ReelsPicked = m.ReelsPicked; }
                    else if (DateTime.Now - o.OpenedAt > TimeSpan.FromMinutes(2))
                    {
                        // the store closed or DISMISSED it (or MCS lost it): stop tracking. A dismissal starts the cool-down
                        // so the planner does not file the same request again a minute later.
                        _open.Remove(o.Part);
                        _ = Task.Run(async () =>
                        {
                            var full = await GetAsync<McsRequestDto>(Cfg.DispatcherUrl.TrimEnd('/') + $"/api/requests/{o.Id}", CancellationToken.None);
                            bool dismissed = string.Equals(full?.Status, "Cancelled", StringComparison.OrdinalIgnoreCase);
                            lock (_lock) { if (dismissed) _dismissed[o.Part] = DateTime.Now; }
                            _log.LogInformation("Parts request #{id} for {part} no longer open on MCS ({status}){cool}", o.Id, o.Part, full?.Status ?? "?",
                                dismissed ? $" - not asking again for {Cfg.DismissCooldownMinutes} min" : "");
                        });
                    }
                }
                Save();
            }
        }
    }

    private async Task<bool> CloseOnMcsAsync(OpenRequest o, string reason, CancellationToken ct)
    {
        var r = await PostAsync<OkDto>(Cfg.DispatcherUrl.TrimEnd('/') + $"/api/requests/{o.Id}/close", new { by = "PVS", reason }, ct);
        if (r is null) { _lastError = $"could not close request #{o.Id} on MCS"; return false; }
        _log.LogInformation("Parts request #{id} closed: {reason}", o.Id, reason);
        return true;
    }

    // ---- transport (never throws) ----
    private static async Task<T?> GetAsync<T>(string url, CancellationToken ct) where T : class
    {
        try
        {
            using var resp = await Http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;
            return JsonSerializer.Deserialize<T>(await resp.Content.ReadAsStringAsync(ct), Json);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { return null; }
    }

    private static async Task<T?> PostAsync<T>(string url, object body, CancellationToken ct) where T : class
    {
        try
        {
            using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using var resp = await Http.PostAsync(url, content, ct);
            if (!resp.IsSuccessStatusCode) return null;
            return JsonSerializer.Deserialize<T>(await resp.Content.ReadAsStringAsync(ct), Json);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { return null; }
    }

    // ---- persistence ----
    private void Load()
    {
        try
        {
            if (!File.Exists(_statePath)) return;
            var s = JsonSerializer.Deserialize<StateDto>(File.ReadAllText(_statePath), Json);
            if (s?.Open is not null) _open = s.Open.Where(o => !string.IsNullOrWhiteSpace(o.Part)).ToDictionary(o => o.Part, o => o, StringComparer.OrdinalIgnoreCase);
            _lastLot = s?.LastLot ?? "";
        }
        catch (Exception ex) { _log.LogWarning(ex, "Parts requests: could not load state"); }
    }

    private void Save()
    {
        try { File.WriteAllText(_statePath, JsonSerializer.Serialize(new StateDto { Open = _open.Values.ToList(), LastLot = _lastLot })); }
        catch (Exception ex) { _log.LogWarning(ex, "Parts requests: could not save state"); }
    }

    private sealed class StateDto { public List<OpenRequest>? Open { get; set; } public string? LastLot { get; set; } }
    private sealed class CreatedDto { public int Id { get; set; } public string? Status { get; set; } }
    private sealed class OkDto { public bool Ok { get; set; } }
    private sealed class McsRequestDto { public int Id { get; set; } public string? Status { get; set; } public int ReelsPicked { get; set; } }
    private sealed class ExhaustDto { public string? LotNo { get; set; } public List<ExhaustRowDto>? Rows { get; set; } }
    private sealed class ExhaustRowDto
    {
        public int Machine { get; set; } public int Feeder { get; set; } public string? Part { get; set; } public int Remaining { get; set; }
        public double? Minutes { get; set; } public bool NeedsRequest { get; set; } public bool SpareReady { get; set; } public bool LastsLot { get; set; }
        public int Issued { get; set; } public int? NeededLot { get; set; }
    }
}
