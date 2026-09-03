using System.Globalization;
using System.Text.RegularExpressions;

namespace Pvs.Core.Serial;

/// <summary>
/// One supply location's (feeder's) line in a Sony <c>C1Z</c> "Production Report — Summary by Supply Location".
/// All counters are cumulative for the report's tabulation window (<see cref="SonySupplyReport.Start"/>..<c>End</c>).
/// </summary>
/// <param name="SupplyLocation">The Sony supply-position number (the <c>Z</c> field) — the feeder. 1xx = cassette,
/// 5xx/6xx = tray on this floor.</param>
/// <param name="Attempted">Attempted pickups (<c>VC</c>).</param>
/// <param name="Successful">Successful pickups (<c>TC</c>) = Attempted − Missed − Abnormal.</param>
/// <param name="Missed">Missed pickup errors (<c>MC</c>).</param>
/// <param name="Abnormal">Abnormal pickup errors (<c>DC</c>).</param>
/// <param name="Recognition">Recognition errors (<c>RC</c>) — picked but failed vision.</param>
/// <param name="PartsOut">Parts-out stops on this feeder (<c>EP</c>).</param>
/// <param name="RateHundredthsPct">Successful pickup rate in 1/100 % (<c>PR</c>): 9921 = 99.21 %. Equals
/// (Attempted − Missed − Abnormal − Recognition) / Attempted.</param>
public sealed record SupplyLocationStat(
    int SupplyLocation,
    long Attempted,
    long Successful,
    long Missed,
    long Abnormal,
    long Recognition,
    long PartsOut,
    int RateHundredthsPct)
{
    /// <summary>All pickup/recognition errors on this feeder in the window.</summary>
    public long Errors => Missed + Abnormal + Recognition;
    /// <summary>True when the feeder recorded any activity — used to hide idle/unloaded positions.</summary>
    public bool HasActivity => Attempted > 0 || Errors > 0 || PartsOut > 0;
}

/// <summary>
/// Parses the Sony <c>C1Z</c> "Summary by Supply Location" report as the SI-F machines on this floor actually
/// stream it: PLAIN ASCII (the manual §6.2.4 says ZIP, but these machines send text), a
/// <c>SD&lt;10&gt;ED&lt;10&gt;</c> tabulation-window header followed by one comma-delimited record per feeder:
/// <code>Z110,VC00001893,TC00001893,MC00000000,DC00000000,RC00000005,SC00000000,NC00000000,BC00000000,EP00000000,PR00009973</code>
/// The tag→meaning map is confirmed against live captures (Line 1, 2026-08-27): <c>TC == VC − MC − DC</c> and
/// <c>PR == (VC − MC − DC − RC) / VC</c> hold on every active feeder. <c>SC/NC/BC</c> are reserved (always 0).
/// The stream ends with a trailing <c>*&lt;pwb file name&gt;</c>, which is ignored.
/// </summary>
public static class SonySupplyReport
{
    // SD/ED tabulation timestamps: 10 digits = YY MM DD HH mm.
    private static readonly Regex HdrStart = new(@"SD(\d{10})", RegexOptions.Compiled);
    private static readonly Regex HdrEnd = new(@"ED(\d{10})", RegexOptions.Compiled);
    // A per-feeder record starts at "Z" + 3 digits + a comma (distinguishes it from other Z* tokens).
    private static readonly Regex RecordSplit = new(@"(?=Z\d{3},)", RegexOptions.Compiled);
    private static readonly Regex Loc = new(@"^Z(\d{3}),", RegexOptions.Compiled);

    public static SonySupplyReportResult Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new SonySupplyReportResult(null, null, Array.Empty<SupplyLocationStat>());

        // Everything after the first '*' is the trailing PWB-file name echo — drop it.
        int star = text.IndexOf('*');
        string body = star >= 0 ? text[..star] : text;

        DateTime? start = ParseStamp(HdrStart.Match(text));   // read the stamps from the FULL text (before the cut)
        DateTime? end = ParseStamp(HdrEnd.Match(text));

        var feeders = new List<SupplyLocationStat>();
        foreach (var seg in RecordSplit.Split(body))
        {
            var lm = Loc.Match(seg);
            if (!lm.Success) continue;   // header chunk / stray text before the first Z-record
            int loc = int.Parse(lm.Groups[1].Value, CultureInfo.InvariantCulture);
            long vc = Tag(seg, "VC"), tc = Tag(seg, "TC"), mc = Tag(seg, "MC"),
                 dc = Tag(seg, "DC"), rc = Tag(seg, "RC"), ep = Tag(seg, "EP"), pr = Tag(seg, "PR");
            feeders.Add(new SupplyLocationStat(loc, vc, tc, mc, dc, rc, ep, (int)pr));
        }
        return new SonySupplyReportResult(start, end, feeders);
    }

    /// <summary>Reads one comma-delimited "&lt;TAG&gt;&lt;digits&gt;" counter from a single Z-record. 0 if absent.</summary>
    private static long Tag(string record, string code)
    {
        var m = Regex.Match(record, @"(?<![A-Za-z])" + Regex.Escape(code) + @"(\d{1,10})");
        return m.Success && long.TryParse(m.Groups[1].Value, out var v) ? v : 0;
    }

    private static DateTime? ParseStamp(Match m)
    {
        if (!m.Success) return null;
        string s = m.Groups[1].Value;   // YYMMDDHHmm
        return DateTime.TryParseExact(s, "yyMMddHHmm", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var dt) ? dt : (DateTime?)null;
    }
}

/// <summary>A parsed C1Z report: the tabulation window and every feeder's counters.</summary>
public sealed record SonySupplyReportResult(
    DateTime? Start,
    DateTime? End,
    IReadOnlyList<SupplyLocationStat> Feeders);
