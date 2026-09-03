using System.Text;
using Pvs.Core.Inventory;

namespace Pvs.LineApp.Runtime;

/// <summary>
/// Writes the parts-shortage email. A production manager reads this on a phone, standing up, between two other
/// problems — so it leads with the single worst case in one sentence, then a table he can scan, and nothing
/// else. No field names, no lot/BOM jargon, no code identifiers, no explanation of how it was worked out.
/// </summary>
public static class ShortageReport
{
    /// <summary>Subject line: the worst case, complete on its own, because that is all many people will read.</summary>
    public static string Subject(ShortageAlertPlan plan, ShortageForecast forecast)
    {
        var outstanding = plan.Outstanding;
        if (outstanding.Count == 0)
            return plan.Cleared.Count > 0 ? "Parts: shortage cleared" : "Parts: nothing short";

        var urgent = plan.UrgentItems;
        var lead = (urgent.Count > 0 ? urgent : outstanding)
            .OrderBy(i => i.DeliveryDate ?? DateTime.MaxValue)
            .ThenByDescending(i => i.ShortBy)
            .First();

        // How many parts share that same first-to-fail lot — "short 7 parts" is the number he acts on.
        int sameLot = outstanding.Count(i => string.Equals(i.LotNo, lead.LotNo, StringComparison.OrdinalIgnoreCase));
        string when = lead.DeliveryDate is DateTime d ? $" due {d:dd-MMM}" : "";
        string tag = urgent.Count > 0 ? "PARTS SHORTAGE" : "Parts shortage ahead";
        return $"{tag}: {lead.Model} lot {lead.LotNo}{when} will be short {sameLot} part{(sameLot == 1 ? "" : "s")}";
    }

    /// <summary>The plain-text body.</summary>
    public static string Body(ShortageAlertPlan plan, ShortageForecast forecast, int leadTimeDays, DateTime now)
    {
        var sb = new StringBuilder();
        var outstanding = plan.Outstanding;
        var urgent = plan.UrgentItems;
        var later = outstanding.Where(i => !i.Urgent).ToList();

        if (outstanding.Count == 0)
        {
            sb.AppendLine(plan.Cleared.Count > 0
                ? "Good news — a parts shortage has cleared."
                : "No parts shortage expected.");
            sb.AppendLine();
        }
        else
        {
            var lead = (urgent.Count > 0 ? urgent : outstanding)
                .OrderBy(i => i.DeliveryDate ?? DateTime.MaxValue)
                .ThenByDescending(i => i.ShortBy)
                .First();
            int sameLot = outstanding.Count(i => string.Equals(i.LotNo, lead.LotNo, StringComparison.OrdinalIgnoreCase));
            string when = lead.DeliveryDate is DateTime d ? $" due {d:dd-MMM}" : "";
            sb.AppendLine($"{lead.Model} lot {lead.LotNo}{when} will be short {sameLot} part{(sameLot == 1 ? "" : "s")}.");
            sb.AppendLine($"Worst: {lead.PartNumber}, short {lead.ShortBy:n0} pieces.");
            sb.AppendLine();
        }

        if (urgent.Count > 0)
        {
            sb.AppendLine($"NEEDED WITHIN {leadTimeDays} DAY{(leadTimeDays == 1 ? "" : "S")} — ASK CANON TO SHIP");
            Table(sb, urgent, now);
            sb.AppendLine();
        }

        if (later.Count > 0)
        {
            sb.AppendLine("COMING UP LATER — for information");
            Table(sb, later, now);
            sb.AppendLine();
        }

        if (plan.Cleared.Count > 0)
        {
            sb.AppendLine("NOW COVERED — no action needed");
            foreach (var c in plan.Cleared.OrderBy(c => c.PartNumber, StringComparer.OrdinalIgnoreCase))
                sb.AppendLine($"  {c.PartNumber} for lot {c.LotNo} — enough material now (was short {c.PreviousShortBy:n0}).");
            sb.AppendLine();
        }

        decimal value = outstanding.Sum(i => i.Finding?.ShortValue ?? 0m);
        if (value > 0) sb.AppendLine($"Value of the missing pieces: about RM {value:n0}.");

        sb.AppendLine(Confidence(forecast));
        sb.AppendLine();
        sb.AppendLine($"Checked {forecast.LotsConsidered} upcoming order{(forecast.LotsConsidered == 1 ? "" : "s")} " +
                      $"and {forecast.PartsConsidered} part{(forecast.PartsConsidered == 1 ? "" : "s")} on {now:ddd dd-MMM-yyyy HH:mm}.");
        foreach (var caveat in Caveats(forecast)) sb.AppendLine(caveat);
        sb.AppendLine();
        sb.AppendLine("— PVS, automatic parts check");
        return sb.ToString();
    }

    private static void Table(StringBuilder sb, IReadOnlyList<ShortageAlertItem> items, DateTime now)
    {
        sb.AppendLine($"  {"Part",-16} {"Model",-7} {"Lot",-15} {"Due",-8} {"Short by",10}");
        sb.AppendLine($"  {new string('-', 16),-16} {new string('-', 7),-7} {new string('-', 15),-15} {new string('-', 8),-8} {new string('-', 10),10}");
        foreach (var i in items.OrderBy(i => i.DeliveryDate ?? DateTime.MaxValue).ThenByDescending(i => i.ShortBy))
        {
            string due = i.DeliveryDate is DateTime d ? d.ToString("dd-MMM") : "-";
            sb.AppendLine($"  {Trunc(i.PartNumber, 16),-16} {Trunc(i.Model, 7),-7} {Trunc(i.LotNo, 15),-15} {due,-8} {i.ShortBy,10:n0}");
            string note = Note(i, now);
            if (note.Length > 0) sb.AppendLine($"      {note}");
        }
    }

    /// <summary>The one line that turns a number into news: what moved, and how long it has been outstanding.</summary>
    private static string Note(ShortageAlertItem i, DateTime now)
    {
        int days = i.DaysOutstanding(now);
        string age = days <= 0 ? "first reported today"
                   : days == 1 ? "first reported yesterday, still not covered"
                   : $"first reported {days} days ago, still not covered";
        return i.Change switch
        {
            ShortageChange.New => "",
            ShortageChange.Worsened => $"got worse — was short {i.PreviousShortBy:n0}, now {i.ShortBy:n0} ({age})",
            ShortageChange.Improved => $"improving — was short {i.PreviousShortBy:n0}, now {i.ShortBy:n0} ({age})",
            ShortageChange.Escalated => $"now inside the ordering window ({age})",
            ShortageChange.Reminder => $"unchanged — {age}",
            _ => "",
        };
    }

    /// <summary>
    /// States the figure's honesty plainly, by LINE rather than as a percentage. Reels on a line PVS does not
    /// track keep their as-issued quantity, so they look fuller than they are — which means a real shortage
    /// can be BIGGER than the one shown. That is the caveat that matters, and it is said in those words.
    /// </summary>
    /// <summary>The bucket a reel falls in when its line was never recorded — not a line anyone can be told about.</summary>
    public const string NoLine = "(unassigned)";

    private static string Confidence(ShortageForecast f)
    {
        var c = f.Coverage;
        if (c.NothingKnown) return "Stock figures include the store and the reels already at the lines.";
        if (c.FullyTracked) return "Stock figures are live: every line is reporting what is left on its reels.";

        var namedLive = c.TrackedLines.Where(l => l != NoLine).ToList();
        var namedDark = c.UntrackedLines.Where(l => l != NoLine).ToList();
        bool anyLoose = c.UntrackedLines.Any(l => l == NoLine);

        string live = namedLive.Count > 0
            ? $"Reel counts are live on line{(namedLive.Count == 1 ? "" : "s")} {string.Join(", ", namedLive)}. "
            : "";

        string dark;
        if (namedDark.Count > 0)
            dark = $"Line{(namedDark.Count == 1 ? "" : "s")} {string.Join(", ", namedDark)} " +
                   $"{(namedDark.Count == 1 ? "does" : "do")} not report" +
                   (anyLoose ? ", and some reels have no line recorded" : "") +
                   ", so those reels are counted as they were issued";
        else
            dark = "Some reels have no line recorded, so they are counted as they were issued";

        return live + dark + " — a shortage there could be bigger than shown here.";
    }

    private static IEnumerable<string> Caveats(ShortageForecast f)
    {
        if (f.LotsUnknownSize > 0)
            yield return $"{f.LotsUnknownSize} order{(f.LotsUnknownSize == 1 ? " has" : "s have")} no quantity on file and could not be checked.";
        if (f.ModelsWithoutBom.Count > 0)
            yield return $"No parts list on file for: {string.Join(", ", f.ModelsWithoutBom)} — those orders were not checked.";
        if (f.LotsAlreadyBuilt > 0)
            yield return f.LotsAlreadyBuilt == 1
                ? "1 order is already built and needs no material."
                : $"{f.LotsAlreadyBuilt} orders are already built and need no material.";
    }

    private static string Trunc(string s, int n) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[..(n - 1)] + "…");
}
