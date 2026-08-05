using System.Text;
using Pvs.Core.Config;
using Pvs.Core.Data;
using Pvs.Core.Runtime;
using Pvs.Core.Shifts;

namespace Pvs.LineApp.Runtime;

/// <summary>One lot that ran during the shift, with the boards produced for it.</summary>
public sealed record ShiftLotLine(string LotNo, string Model, string Side, int Boards, int Excess);

/// <summary>The assembled per-line shift report: what ran, and how the line's time was spent.</summary>
public sealed record ShiftReportData(
    string LineName, int LineId, string ShiftName, DateTime WindowStart, DateTime WindowEnd,
    IReadOnlyList<ShiftLotLine> Lots, int TotalBoards, int TotalExcess, ShiftUptimeSummary? Uptime);

/// <summary>
/// Builds and formats the twice-daily per-line shift report: the models/lots completed in the shift that
/// just ended (from DailyProductionCount) plus the line's available / production / down time (from
/// <see cref="ShiftUptimeService"/>). Pure assembly + text formatting — the schedule and send live elsewhere.
/// </summary>
public static class ShiftReport
{
    // DailyProductionCount labels the day shift "Morning"; the ShiftSchedule calls it "Day".
    private static string DpcLabel(string shiftName) =>
        shiftName.Equals("Day", StringComparison.OrdinalIgnoreCase) ? "Morning" : shiftName;

    /// <summary>
    /// Gather the report for the shift identified by <paramref name="uptime"/> (its window drives which DB
    /// dates are queried). A Night shift straddles midnight, so both calendar dates are queried and filtered
    /// to the shift's label.
    /// </summary>
    public static async Task<ShiftReportData> BuildAsync(
        LineConfig config, IReelPartRepository repo, ShiftUptimeSummary uptime, CancellationToken ct = default)
    {
        string label = DpcLabel(uptime.ShiftName);
        string lineNo = config.LineId.ToString();

        // One date for a day shift; the night shift's rows are split across the day it started and the day it ended.
        var dates = new HashSet<string> { uptime.WindowStart.ToString("yyyy-MM-dd"), uptime.WindowEnd.ToString("yyyy-MM-dd") };
        var runs = new List<ProductionRun>();
        foreach (var d in dates)
        {
            try { runs.AddRange(await repo.GetDailyProductionAsync(lineNo, d, ct)); }
            catch { /* DB down for one date -> report what we can */ }
        }

        var lots = runs
            .Where(r => (r.Shift ?? "").Trim().Equals(label, StringComparison.OrdinalIgnoreCase))
            .GroupBy(r => (Lot: (r.LotNo ?? "").Trim(), Model: (r.Model ?? "").Trim(), Side: (r.Side ?? "").Trim()))
            .Select(g => new ShiftLotLine(g.Key.Lot, g.Key.Model, g.Key.Side, g.Sum(x => x.Quantity), g.Sum(x => x.ExcessQuantity)))
            .Where(l => l.Boards > 0 || !string.IsNullOrWhiteSpace(l.LotNo))
            .OrderByDescending(l => l.Boards)
            .ToList();

        return new ShiftReportData(config.LineName, config.LineId, uptime.ShiftName,
            uptime.WindowStart, uptime.WindowEnd, lots, lots.Sum(l => l.Boards), lots.Sum(l => l.Excess), uptime);
    }

    private static string Hm(TimeSpan t) => $"{(int)t.TotalHours}h {t.Minutes:00}m";

    public static string Subject(ShiftReportData r) =>
        $"PVS {r.LineName} — {r.ShiftName} shift {r.WindowStart:dd-MMM} — {r.TotalBoards} boards";

    /// <summary>Plain-text email body (SMTP body is plain text).</summary>
    public static string Body(ShiftReportData r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{r.LineName}  —  {r.ShiftName} shift report");
        sb.AppendLine($"{r.WindowStart:ddd dd-MMM-yyyy HH:mm}  →  {r.WindowEnd:HH:mm}");
        sb.AppendLine(new string('=', 46));
        sb.AppendLine();

        if (r.Uptime is { } u)
        {
            sb.AppendLine("MACHINE TIME");
            sb.AppendLine($"  Available (line up) : {Hm(u.Available)}");
            sb.AppendLine($"  Actual production   : {Hm(u.Production)}");
            sb.AppendLine($"  Downtime            : {Hm(u.Down)}   ({u.Stops} stop{(u.Stops == 1 ? "" : "s")})");
            if (u.Available > TimeSpan.Zero)
                sb.AppendLine($"  Utilisation         : {(u.Production.TotalMinutes / u.Available.TotalMinutes) * 100:0.0}%");
            sb.AppendLine();
        }

        sb.AppendLine($"MODELS / LOTS COMPLETED   (total {r.TotalBoards} boards)");
        if (r.Lots.Count == 0)
        {
            sb.AppendLine("  (no production recorded this shift)");
        }
        else
        {
            sb.AppendLine($"  {"Lot",-14} {"Model",-16} {"Side",-5} {"Boards",8}");
            sb.AppendLine($"  {new string('-', 12),-14} {new string('-', 14),-16} {new string('-', 4),-5} {new string('-', 7),8}");
            foreach (var l in r.Lots)
            {
                string lot = string.IsNullOrWhiteSpace(l.LotNo) ? "(no lot)" : l.LotNo;
                sb.AppendLine($"  {Trunc(lot, 14),-14} {Trunc(l.Model, 16),-16} {Trunc(l.Side, 5),-5} {l.Boards,8:n0}");
            }
            if (r.TotalExcess > 0) sb.AppendLine($"  (+{r.TotalExcess:n0} excess boards)");
        }

        sb.AppendLine();
        sb.AppendLine("— PVS, automatic shift report");
        return sb.ToString();
    }

    private static string Trunc(string s, int n) => string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[..(n - 1)] + "…");
}
