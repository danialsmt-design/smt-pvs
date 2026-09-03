using System.Text;

namespace Pvs.LineApp.Runtime;

/// <summary>
/// Sends a WhatsApp text via the plant's gms-wabridge Pi (POST /api/send {recipient, message}). The bridge is on
/// Tailscale — a DIFFERENT route than the intranet DB — so a DB-path outage can still be reported out. Fire-and-
/// forget: it swallows its own errors and has its own short timeout, so raising an alert can never block or break
/// the caller (the health probe).
/// </summary>
public sealed class WhatsAppSender
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly string? _url;
    private readonly string? _to;

    public WhatsAppSender(string? bridgeUrl, string? recipient)
    {
        _url = string.IsNullOrWhiteSpace(bridgeUrl) ? null : bridgeUrl.TrimEnd('/');
        _to = string.IsNullOrWhiteSpace(recipient) ? null : recipient.Trim();
    }

    public bool Configured => _url is not null && _to is not null;

    /// <summary>Posts the message to the bridge. Returns true on a 2xx, false on anything else (never throws).</summary>
    public async Task<bool> SendAsync(string message)
    {
        if (!Configured) return false;
        try
        {
            var body = System.Text.Json.JsonSerializer.Serialize(new { recipient = _to, message });
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await Http.PostAsync(_url + "/api/send", content);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }
}
