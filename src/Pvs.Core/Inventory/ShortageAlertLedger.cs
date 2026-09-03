using System.Text.Json;

namespace Pvs.Core.Inventory;

/// <summary>What happened to one shortage since the manager was last told about it.</summary>
public enum ShortageChange
{
    /// <summary>Never reported before.</summary>
    New,
    /// <summary>Materially more short than when last reported.</summary>
    Worsened,
    /// <summary>Materially less short, but still short. Progress is worth knowing.</summary>
    Improved,
    /// <summary>Same figure, but the lot has now entered the lead-time window — informational became urgent.</summary>
    Escalated,
    /// <summary>Unchanged and still outstanding; the reminder interval has elapsed.</summary>
    Reminder,
    /// <summary>Not short any more, and the lot is still to be built. Worth one all-clear, then silence.</summary>
    Resolved,
}

/// <summary>
/// What was last REPORTED for one part+lot. <see cref="ShortBy"/> is deliberately the last figure the manager
/// was actually shown, not the latest observation — thresholds must be measured against what he knows, or a
/// shortage that creeps up in small steps never trips them.
/// </summary>
public sealed record ShortageAlertEntry(
    string PartNumber, string LotNo, long ShortBy, bool Urgent, DateTime FirstReported, DateTime LastSent)
{
    public string Key => PartNumber + "|" + LotNo;
}

/// <summary>One line of an outgoing alert: what changed, and enough context to write it in plain words.</summary>
public sealed record ShortageAlertItem(
    ShortageChange Change, string PartNumber, string LotNo, string Model, DateTime? DeliveryDate,
    long ShortBy, long PreviousShortBy, bool Urgent, DateTime FirstReported, ShortageFinding? Finding)
{
    /// <summary>How long this shortage has been outstanding. An ageing unresolved warning should read as worse.</summary>
    public TimeSpan AgeAt(DateTime now) => now - FirstReported;

    /// <summary>Whole days outstanding (0 on the day it was first reported).</summary>
    public int DaysOutstanding(DateTime now) => Math.Max(0, (int)(now.Date - FirstReported.Date).TotalDays);
}

/// <summary>What to send this run, and the state to persist afterwards.</summary>
public sealed record ShortageAlertPlan(
    IReadOnlyList<ShortageAlertItem> Items,
    IReadOnlyList<ShortageAlertEntry> NextState,
    int SuppressedQuiet)
{
    public bool ShouldSend => Items.Count > 0;
    public IReadOnlyList<ShortageAlertItem> Outstanding => Items.Where(i => i.Change != ShortageChange.Resolved).ToList();
    public IReadOnlyList<ShortageAlertItem> Cleared => Items.Where(i => i.Change == ShortageChange.Resolved).ToList();
    public IReadOnlyList<ShortageAlertItem> UrgentItems => Outstanding.Where(i => i.Urgent).ToList();
}

/// <summary>How eagerly to re-tell the manager something he already knows.</summary>
public sealed record ShortageAlertOptions(
    TimeSpan ReminderInterval,
    double MaterialChangePercent = 5,
    long MaterialChangeMinimum = 1)
{
    public static ShortageAlertOptions Default => new(TimeSpan.FromHours(24));

    /// <summary>How much the figure must move before it is worth another email.</summary>
    public long Threshold(long baseline) =>
        Math.Max(Math.Max(1, MaterialChangeMinimum), (long)Math.Ceiling(Math.Abs(baseline) * MaterialChangePercent / 100.0));
}

/// <summary>
/// Decides what is worth SAYING, given what was already said. Pure — no clock, no files, no email.
/// <para>
/// The whole value of this monitor rests on the manager still reading it in a month. So: a shortage that has
/// not moved is repeated at most once per reminder interval; a shortage that got worse, got better, or became
/// urgent is always worth an email; a shortage that cleared earns exactly ONE all-clear and is then forgotten;
/// and a lot that was built or cancelled disappears without a word, because nothing happened worth reporting.
/// </para>
/// </summary>
public static class ShortageAlertLedger
{
    public static ShortageAlertPlan Decide(
        IEnumerable<ShortageAlertEntry>? state,
        ShortageForecast forecast,
        DateTime now,
        ShortageAlertOptions? options = null)
    {
        var opt = options ?? ShortageAlertOptions.Default;
        var previous = new Dictionary<string, ShortageAlertEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in state ?? Array.Empty<ShortageAlertEntry>())
        {
            if (e is null || string.IsNullOrWhiteSpace(e.PartNumber) || string.IsNullOrWhiteSpace(e.LotNo)) continue;
            previous[e.Key] = e;                                  // last write wins on a duplicated key
        }

        var items = new List<ShortageAlertItem>();
        var next = new List<ShortageAlertEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var partsStillShort = new HashSet<string>(
            forecast.Findings.Select(f => f.PartNumber), StringComparer.OrdinalIgnoreCase);
        var activeLots = new HashSet<string>(forecast.ActiveLots ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        int quiet = 0;

        foreach (var f in forecast.Findings)
        {
            seen.Add(f.Key);
            if (!previous.TryGetValue(f.Key, out var prev))
            {
                items.Add(Item(ShortageChange.New, f, 0, now, now));
                next.Add(new ShortageAlertEntry(f.PartNumber, f.LotNo, f.ShortBy, f.Urgent, now, now));
                continue;
            }

            long move = f.ShortBy - prev.ShortBy;
            long threshold = opt.Threshold(prev.ShortBy);
            bool worse = move >= threshold;
            bool better = -move >= threshold;
            bool escalated = f.Urgent && !prev.Urgent;
            bool due = now - prev.LastSent >= opt.ReminderInterval;

            ShortageChange? change =
                worse ? ShortageChange.Worsened :
                escalated ? ShortageChange.Escalated :
                better ? ShortageChange.Improved :
                due ? ShortageChange.Reminder : null;

            if (change is null)
            {
                // Nothing worth another email. Keep the LAST REPORTED figure as the baseline so a slow drift
                // still has to cross the threshold against what he was told, not against yesterday's silence.
                quiet++;
                next.Add(prev);
                continue;
            }

            items.Add(Item(change.Value, f, prev.ShortBy, prev.FirstReported, now));
            next.Add(new ShortageAlertEntry(f.PartNumber, f.LotNo, f.ShortBy, f.Urgent, prev.FirstReported, now));
        }

        foreach (var prev in previous.Values)
        {
            if (seen.Contains(prev.Key)) continue;

            // The lot is gone from the plan (built, cancelled, delivered, or past the horizon) — nothing
            // happened that the manager needs to hear. Likewise if the same part now runs out at a DIFFERENT
            // lot: the follow-on finding says everything, and an all-clear beside it would just confuse.
            if (!activeLots.Contains(prev.LotNo) || partsStillShort.Contains(prev.PartNumber)) continue;

            items.Add(new ShortageAlertItem(
                ShortageChange.Resolved, prev.PartNumber, prev.LotNo, "", null,
                0, prev.ShortBy, prev.Urgent, prev.FirstReported, null));
            // Deliberately NOT carried into next state: an all-clear is said once and never repeated.
        }

        return new ShortageAlertPlan(items, next, quiet);
    }

    private static ShortageAlertItem Item(ShortageChange change, ShortageFinding f, long was, DateTime first, DateTime now) =>
        new(change, f.PartNumber, f.LotNo, f.Model, f.DeliveryDate, f.ShortBy, was, f.Urgent, first, f);
}

/// <summary>
/// What the monitor remembers between runs: which day it last ran, when it last said "all fine", and every
/// shortage already reported. Without this, a restart re-sends everything and a shortage mentioned once then
/// never again reads as solved when it is not.
/// </summary>
public sealed class ShortageMonitorState
{
    /// <summary>Date (yyyy-MM-dd) of the last completed scheduled run, so a restart does not repeat it.</summary>
    public string LastRunDate { get; set; } = "";

    /// <summary>When the last periodic all-clear went out (null = never).</summary>
    public DateTime? LastAllClear { get; set; }

    /// <summary>Every shortage currently tracked, as last reported.</summary>
    public List<ShortageAlertEntry> Alerts { get; set; } = new();
}

/// <summary>
/// Reads and writes the monitor's state as JSON. Parsing is total: empty, truncated, hand-edited or simply
/// wrong content yields a FRESH state rather than an exception. Losing the memory costs one duplicated email;
/// throwing would take down the app that drives a live line's parts interlock, which is not a trade this
/// monitor is entitled to make. File handling lives in the app; this half is pure so it can be tested.
/// </summary>
public static class ShortageStateCodec
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    /// <summary>Parses state, or returns a fresh one. <paramref name="problem"/> explains why when it could not be read.</summary>
    public static ShortageMonitorState Read(string? json, out string? problem)
    {
        problem = null;
        if (string.IsNullOrWhiteSpace(json)) return new ShortageMonitorState();
        try
        {
            var state = JsonSerializer.Deserialize<ShortageMonitorState>(json, Options);
            if (state is null) { problem = "state file held no object"; return new ShortageMonitorState(); }
            state.Alerts ??= new List<ShortageAlertEntry>();
            // Drop anything that cannot identify itself rather than let it poison the comparison.
            state.Alerts.RemoveAll(a => a is null || string.IsNullOrWhiteSpace(a.PartNumber) || string.IsNullOrWhiteSpace(a.LotNo));
            return state;
        }
        catch (Exception ex)
        {
            problem = ex.Message;
            return new ShortageMonitorState();
        }
    }

    public static string Write(ShortageMonitorState state) => JsonSerializer.Serialize(state, Options);
}
