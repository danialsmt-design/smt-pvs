using System.Net;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using Pvs.Core.Config;

namespace Pvs.LineApp.Runtime;

/// <summary>
/// Sends PVS report/alert email from one Gmail (pvsbangi). Only the GATEWAY line (Email.GatewayUrl empty) has
/// internet and talks to Gmail over SMTP; every other line POSTs its message to the gateway's <c>/api/sendmail</c>
/// over Tailscale, so every email still originates from a line's PVS. The Gmail app password is read from the
/// <c>email-password.txt</c> sidecar next to the app (gateway line only). Best-effort: a failure is logged and
/// returned as false, never thrown into the line.
/// </summary>
public sealed class EmailSender
{
    private readonly EmailConfig _cfg;
    private readonly ILogger<EmailSender> _log;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly string _pwPath;

    public EmailSender(LineConfig config, ILogger<EmailSender> log)
    {
        _cfg = config.Email;
        _log = log;
        _pwPath = Path.Combine(AppContext.BaseDirectory, "email-password.txt");
    }

    public bool Enabled => _cfg.Enabled;
    public bool IsGateway => _cfg.IsGateway;

    /// <summary>Send to explicit recipient addresses. The gateway SMTPs; other lines relay to the gateway.</summary>
    public async Task<bool> SendAsync(IEnumerable<string> to, string subject, string body, CancellationToken ct = default)
    {
        var recips = to.Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (!_cfg.Enabled || recips.Count == 0) return false;
        return _cfg.IsGateway ? await SendSmtpAsync(recips, subject, body, ct)
                              : await RelayAsync(recips, subject, body, ct);
    }

    /// <summary>Send to THIS line's configured PICs, optionally filtered by role (e.g. only Production).</summary>
    public Task<bool> SendToPicsAsync(string subject, string body, Func<EmailRecipient, bool>? where = null, CancellationToken ct = default)
        => SendAsync(_cfg.Recipients.Where(r => where is null || where(r)).Select(r => r.Email), subject, body, ct);

    /// <summary>Real SMTP to Gmail (gateway line, or the gateway fulfilling a relayed request).</summary>
    public async Task<bool> SendSmtpAsync(IEnumerable<string> to, string subject, string body, CancellationToken ct = default)
    {
        string? pw = File.Exists(_pwPath) ? (await File.ReadAllTextAsync(_pwPath, ct)).Trim() : null;
        if (string.IsNullOrEmpty(pw)) { _log.LogWarning("Email not sent: email-password.txt missing/empty on the gateway."); return false; }
        try
        {
            using var msg = new MailMessage { From = new MailAddress(_cfg.From, _cfg.FromName), Subject = subject, Body = body };
            foreach (var r in to) msg.To.Add(r);
            using var smtp = new SmtpClient(_cfg.SmtpHost, _cfg.SmtpPort) { EnableSsl = true, Credentials = new NetworkCredential(_cfg.From, pw) };
            await smtp.SendMailAsync(msg, ct);
            _log.LogInformation("Email sent to {N}: {Subj}", msg.To.Count, subject);
            return true;
        }
        catch (Exception ex) { _log.LogWarning(ex, "SMTP send failed."); return false; }
    }

    /// <summary>True when a relayed request carries the shared key the gateway requires.</summary>
    public bool KeyOk(string? key) => string.IsNullOrEmpty(_cfg.GatewayKey) || string.Equals(key, _cfg.GatewayKey, StringComparison.Ordinal);

    private async Task<bool> RelayAsync(List<string> to, string subject, string body, CancellationToken ct)
    {
        try
        {
            var url = _cfg.GatewayUrl.TrimEnd('/') + "/api/sendmail";
            var payload = JsonSerializer.Serialize(new { key = _cfg.GatewayKey, to, subject, body });
            using var resp = await _http.PostAsync(url, new StringContent(payload, Encoding.UTF8, "application/json"), ct);
            if (resp.IsSuccessStatusCode) { _log.LogInformation("Email relayed via gateway: {Subj}", subject); return true; }
            _log.LogWarning("Email relay to gateway failed: {Status}", resp.StatusCode);
            return false;
        }
        catch (Exception ex) { _log.LogWarning(ex, "Email relay failed."); return false; }
    }
}
