namespace Pvs.Core.Verification;

/// <summary>
/// Part-number matching for the verification interlock. Plant convention: a part number that contains a
/// slash ('/') in the ProductBOM / feeder list lists a PRIMARY part and its approved SUBSTITUTE(s) — any of
/// the slash-separated values is an acceptable part for that feeder position (e.g. "A/B" or "A/B/C", with or
/// without spaces around the slash). A reel is therefore a valid match when ANY alternative on the expected
/// side equals ANY alternative on the scanned side (case-insensitive, whitespace-trimmed).
/// <para>
/// A part number with NO slash behaves exactly as a plain trimmed, case-insensitive equality (unchanged from
/// before), so this only ever LOOSENS a match to accept a listed substitute — it never accepts a part that
/// isn't named in the BOM/feeder field.
/// </para>
/// </summary>
public static class PartNumber
{
    /// <summary>True if <paramref name="a"/> and <paramref name="b"/> name the same part, honouring
    /// slash-substitute lists on either side.</summary>
    public static bool Matches(string? a, string? b)
    {
        string x = a?.Trim() ?? "";
        string y = b?.Trim() ?? "";
        // No substitutes on either side -> exact original behaviour (incl. ""=="" ).
        if (x.IndexOf('/') < 0 && y.IndexOf('/') < 0)
            return string.Equals(x, y, StringComparison.OrdinalIgnoreCase);
        // Substitute-aware: a slash lists alternatives; match if any alternative lines up.
        foreach (var p in Alternatives(x))
            foreach (var q in Alternatives(y))
                if (string.Equals(p, q, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>The set of acceptable part numbers a field represents: split on '/', trimmed, non-empty.</summary>
    public static IReadOnlyList<string> Alternatives(string? part)
    {
        if (string.IsNullOrWhiteSpace(part)) return Array.Empty<string>();
        var list = new List<string>();
        foreach (var seg in part.Split('/'))
        {
            var t = seg.Trim();
            if (t.Length > 0) list.Add(t);
        }
        return list;
    }
}
