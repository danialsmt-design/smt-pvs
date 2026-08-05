using System.Text.Json;
using System.Text.Json.Serialization;
using Pvs.Core.Shifts;

namespace Pvs.Core.Config;

public sealed class ShiftConfig
{
    public string Name { get; set; } = "";
    public string Start { get; set; } = "";   // "HH:mm"
    public string End { get; set; } = "";
}

public sealed class MachineConfig
{
    public int Machine { get; set; }
    public string Port { get; set; } = "";        // e.g. "COM5"
    public string Model { get; set; } = "";        // "F130" / "F209"
    public bool HasTrayFeeder { get; set; }
}

public sealed class SerialConfig
{
    public int BaudRate { get; set; } = 9600;
    public int DataBits { get; set; } = 7;
    public string Parity { get; set; } = "Even";
    public int StopBits { get; set; } = 1;
}

public sealed class ForecastConfig
{
    public int RedMinutes { get; set; } = 45;
    public int AmberMinutes { get; set; } = 120;
}

public sealed class CentralConfig
{
    public string Server { get; set; } = "";
    public string Database { get; set; } = "ReelPart-New";
    /// <summary>SQL login for the line PC (workgroup — cannot use Integrated Security to the remote box).</summary>
    public string UserId { get; set; } = "";
    public string Password { get; set; } = "";

    /// <summary>True when a SQL login is configured (so the app should attempt DB features).</summary>
    public bool HasCredentials => !string.IsNullOrWhiteSpace(Server) && !string.IsNullOrWhiteSpace(UserId);

    /// <summary>Builds the SQL Server connection string (SQL auth).</summary>
    public string ConnectionString() =>
        $"Server={Server};Database={Database};User ID={UserId};Password={Password};" +
        "TrustServerCertificate=True;Connect Timeout=15;";
}

public sealed class BadgeConfig
{
    /// <summary>Reserved prefix that distinguishes a badge UID from a reel UID (interim: "9999").</summary>
    public string UidPrefix { get; set; } = "";
}

/// <summary>One report/alert recipient (a line's Person-In-Charge) and the role they cover, so alerts can be
/// routed by type (e.g. cycle-time → Production; parts shortage → Parts Control).</summary>
public sealed class EmailRecipient
{
    public string Email { get; set; } = "";
    public string Role { get; set; } = "";
}

/// <summary>
/// Email reporting. All PVS reports/alerts are emailed from a single Gmail (pvsbangi). Only the ONE line with
/// internet actually talks to Gmail (SMTP) — it is the "gateway"; the others POST their message to the gateway's
/// <c>/api/sendmail</c> over Tailscale so everything still originates from a line's PVS.
/// </summary>
public sealed class EmailConfig
{
    public bool Enabled { get; set; }
    public string SmtpHost { get; set; } = "smtp.gmail.com";
    public int SmtpPort { get; set; } = 587;
    /// <summary>The sender account (pvsbangi@gmail.com). Its app password is read from the sidecar
    /// <c>email-password.txt</c> next to the app (only the gateway line needs it).</summary>
    public string From { get; set; } = "pvsbangi@gmail.com";
    public string FromName { get; set; } = "PVS";
    /// <summary>Empty = THIS line is the gateway (has internet; sends via SMTP). Set (e.g.
    /// <c>http://100.69.81.105:5199</c>) = relay each message to that PVS's /api/sendmail instead.</summary>
    public string GatewayUrl { get; set; } = "";
    /// <summary>Shared secret the /api/sendmail endpoint requires, so only the plant's lines can relay through it.</summary>
    public string GatewayKey { get; set; } = "";
    /// <summary>This line's PICs — who its lot-complete/alert emails go to.</summary>
    public List<EmailRecipient> Recipients { get; set; } = new();

    public bool IsGateway => string.IsNullOrWhiteSpace(GatewayUrl);
}

/// <summary>
/// The per-line configuration. One JSON file per line PC; the build is identical, only this differs.
/// </summary>
public sealed class LineConfig
{
    public int LineId { get; set; }
    public string LineName { get; set; } = "";
    public List<ShiftConfig> ShiftTimes { get; set; } = new();
    public List<MachineConfig> Machines { get; set; } = new();
    public SerialConfig Serial { get; set; } = new();
    public ForecastConfig Forecast { get; set; } = new();
    public CentralConfig Central { get; set; } = new();
    public BadgeConfig Badge { get; set; } = new();
    public EmailConfig Email { get; set; } = new();

    /// <summary>Password that authorises the on-screen "Shut down PVS" button (a clean stop frees the COM ports,
    /// e.g. to run serial-port test software). Defaults to "100732" when not set in the config.</summary>
    public string ShutdownPassword { get; set; } = "100732";

    /// <summary>
    /// Write live per-feeder remaining balances (by reel UID) back to StockOuts.Quantity every few minutes.
    /// Requires the pvs_ro login to have UPDATE on StockOuts.Quantity (already granted). Off by default.
    /// </summary>
    public bool SyncStockOuts { get; set; }

    /// <summary>
    /// Write PVS's live board count for this line to DailyProductionCount as an incremental row every 30 min
    /// (PVS becomes the production-count writer for this line). Requires pvs_ro INSERT on DailyProductionCount.
    /// Off by default until that grant is in place.
    /// </summary>
    public bool WriteProductionCount { get; set; }

    /// <summary>
    /// How often (minutes) to read each machine's own production counter (C1M) and reconcile it against
    /// PVS's board count. The heartbeat matters because the operator RESETS every machine's counter at end
    /// of lot (SOP): read only after that and the lot's final count is gone, so this bounds the worst-case
    /// loss to one interval. A read takes ~30s over serial, so don't set this low. 0/absent => 20.
    /// </summary>
    public int ReconcileMinutes { get; set; } = 20;

    /// <summary>
    /// Child boards per panel, by model name. One machine cycle (a board-complete / R0) mounts a full
    /// PANEL, so per-cycle consumption of a feeder = ProductBOM.Quantity (per child board) × this factor.
    /// e.g. L307 = 4 (a 4-up panel). Missing/0 => 1 (single board per cycle).
    /// </summary>
    public Dictionary<string, int> PanelBoards { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Child boards per panel for a model (defaults to 1 when not configured).</summary>
    public int PanelBoardsFor(string? model) =>
        !string.IsNullOrWhiteSpace(model) && PanelBoards.TryGetValue(model.Trim(), out var n) && n > 0 ? n : 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static LineConfig Parse(string json) =>
        JsonSerializer.Deserialize<LineConfig>(json, Options)
            ?? throw new InvalidOperationException("Line config is empty or invalid.");

    public static LineConfig Load(string path) => Parse(File.ReadAllText(path));

    /// <summary>Builds the shift schedule from the configured shift times.</summary>
    public ShiftSchedule ToShiftSchedule() =>
        new(ShiftTimes
            .Select(s => new ShiftDefinition(s.Name, TimeOnly.Parse(s.Start), TimeOnly.Parse(s.End)))
            .ToList());

    /// <summary>True if a scanned UID looks like a badge (per the configured prefix). Empty prefix => never.</summary>
    public bool LooksLikeBadge(string uid) =>
        !string.IsNullOrEmpty(Badge.UidPrefix) &&
        (uid ?? "").TrimStart().StartsWith(Badge.UidPrefix, StringComparison.OrdinalIgnoreCase);
}
