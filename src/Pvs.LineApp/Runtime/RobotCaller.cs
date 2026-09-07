using System.Text;
using System.Text.Json;

namespace Pvs.LineApp.Runtime;

/// <summary>
/// The line's door to the delivery robot. PVS never addresses the robot: it asks the MCS dispatcher on the NAS
/// (<c>/api/robot/*</c>, the single writer to the robot) to bring the robot to THIS line, reads back where it is,
/// and releases it when the operator has unloaded. Never throws - a dead NAS just reads as "not reachable".
/// </summary>
public sealed class RobotCaller
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly string? _url;

    public RobotCaller(string? dispatcherUrl)
    {
        _url = string.IsNullOrWhiteSpace(dispatcherUrl) ? null : dispatcherUrl.Trim().TrimEnd('/');
    }

    public bool Enabled => _url is not null;

    /// <summary>Ask for the robot at this line. Returns the dispatcher's answer (ok/duplicate/ahead/message), or null.</summary>
    public Task<object?> CallAsync(int line, string by, string reason) =>
        PostAsync("/api/robot/call", new { line = line.ToString(), by, reason });

    /// <summary>Release the robot after unloading: finds this line's ARRIVED job and marks it done.</summary>
    public async Task<object?> DoneAsync(int line, string by)
    {
        var mine = await MyJobAsync(line);
        int? jobId = mine is JsonElement j && j.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number ? id.GetInt32() : null;
        return await PostAsync("/api/robot/done", new { by, jobId });
    }

    /// <summary>The dispatcher snapshot reduced to what this line's operator needs.</summary>
    public async Task<object?> StatusForLineAsync(int line)
    {
        var snap = await GetAsync("/api/robot/status");
        if (snap is not JsonElement s) return null;
        string ln = line.ToString();
        JsonElement? my = null; int ahead = 0;
        if (s.TryGetProperty("current", out var cur) && cur.ValueKind == JsonValueKind.Object)
        {
            if (IsMine(cur, ln)) my = cur; else ahead = 1;
        }
        if (my is null && s.TryGetProperty("queue", out var q) && q.ValueKind == JsonValueKind.Array)
            foreach (var j in q.EnumerateArray())
            {
                if (IsMine(j, ln)) { my = j; break; }
                ahead++;
            }
        if (my is null) ahead = 0;
        return new
        {
            enabled = true,
            reachable = Prop(s, "reachable")?.ValueKind == JsonValueKind.True,
            state = Prop(s, "state")?.GetString() ?? "",
            battery = Prop(s, "battery") is JsonElement b && b.ValueKind == JsonValueKind.Number ? b.GetInt32() : (int?)null,
            estop = Prop(s, "estop")?.ValueKind == JsonValueKind.True,
            nav = Prop(s, "nav"),
            myJob = my,
            ahead,
        };
    }

    private async Task<JsonElement?> MyJobAsync(int line)
    {
        var st = await StatusForLineAsync(line);
        if (st is null) return null;
        var el = JsonSerializer.SerializeToElement(st);
        return el.TryGetProperty("myJob", out var my) && my.ValueKind == JsonValueKind.Object ? my : null;
    }

    private static bool IsMine(JsonElement job, string line) =>
        job.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String && k.GetString() == "Deliver"
        && job.TryGetProperty("line", out var l) && l.ValueKind == JsonValueKind.String && l.GetString() == line;

    private static JsonElement? Prop(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v : null;

    private async Task<JsonElement?> GetAsync(string path)
    {
        if (_url is null) return null;
        try
        {
            using var resp = await Http.GetAsync(_url + path);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            return doc.RootElement.Clone();
        }
        catch { return null; }
    }

    private async Task<object?> PostAsync(string path, object body)
    {
        if (_url is null) return null;
        try
        {
            using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using var resp = await Http.PostAsync(_url + path, content);
            var text = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.Clone();
        }
        catch { return null; }
    }
}
