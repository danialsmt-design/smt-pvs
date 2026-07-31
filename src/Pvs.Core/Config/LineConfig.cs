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
