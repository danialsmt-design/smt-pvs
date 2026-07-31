namespace Pvs.Core.Serial;

/// <summary>
/// Parses the SI-E2000 serial <c>C1M</c> "Production Report" for the fields PVS needs — chiefly
/// <b>PC = Number of Completed PWBs</b>, the machine's own board counter (authoritative; survives PVS
/// being off). Pure text: the machine streams the report as a run of ASCII <c>D0</c> data messages whose
/// payloads are 2-letter field codes each followed by a fixed-width integer, concatenated
/// (e.g. <c>"SD..ED..PC00000300VC00000312TC.."</c>). We collect those payloads and pick out the field.
///
/// Field codes (per SI-E1000/E2000 manual §7.2.2 / Appendix D): SD start-time, ED end-time, then the
/// counters PC (completed), VC (attempted pickups), TC (successful), MC (missed), DC (abnormal),
/// RC (recognition err), PR (pickup rate), EP/MP/TP/PP/BP (stops), and the T* time fields.
/// </summary>
public static class SonyProductionReport
{
    /// <summary>Number of Completed PWBs (the <c>PC</c> field) from the collected report text, or null if absent.</summary>
    public static int? CompletedPwbs(string? reportData) => Field(reportData, "PC");

    /// <summary>
    /// Read a 2-letter-code counter field (e.g. "PC", "VC") — the code immediately followed by its integer.
    /// The integer runs until the next non-digit (the next field code), so variable widths are handled.
    /// Returns null if the code isn't present. The code must be UPPERCASE letters so it can't match digits.
    /// </summary>
    public static int? Field(string? reportData, string code)
    {
        if (string.IsNullOrEmpty(reportData) || code is null || code.Length != 2) return null;
        // Anchor the match so a code preceded by a letter (part of a longer token) isn't a false hit:
        // a field code is preceded by digits (end of the previous field) or the message start.
        var m = System.Text.RegularExpressions.Regex.Match(
            reportData, @"(?<![A-Za-z])" + System.Text.RegularExpressions.Regex.Escape(code) + @"(\d{1,10})");
        return m.Success && int.TryParse(m.Groups[1].Value, out var v) ? v : (int?)null;
    }
}
