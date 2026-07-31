using System.Text.RegularExpressions;

namespace Pvs.Core.Feeders;

/// <summary>Which side of the machine a feeder sits on.</summary>
public enum FeederSide
{
    /// <summary>Front feeder bank (tape reels). Marked by a trailing "(F)".</summary>
    Front,
    /// <summary>Rear tray-feeder unit (IC machines). Marked by a trailing "(R)".</summary>
    Rear,
    /// <summary>No front/rear marker present (e.g. the "Fnn" / "Znn" machine-1 forms).</summary>
    Unspecified
}

/// <summary>
/// A parsed machine supply position.
///
/// ProductBOM.SupplyPosition is hand-maintained and drifts across at least five real
/// formats (observed 2026-07-23 across all models):
///   "[F]116 (F)"  front, with the constant "[F]" bracket prefix   (bulk of rows)
///   "116 (F)"     front, WITHOUT the bracket prefix               (positions 106-116)
///   "[F]501 (R)"  rear tray  -- note the prefix is still "[F]", the REAR marker is the trailing "(R)"
///   "F12"         machine-1 style, no front/rear marker
///   "Z12"         machine-1 style, no front/rear marker
///   ""            blank -- part not assigned to a feeder (hand-placed part, or a data gap)
///
/// Parse rule: the square-bracket "[F]" prefix is a CONSTANT and is ignored; the authoritative
/// front/rear indicator is the trailing round-paren "(F)"/"(R)". The position number is the first
/// run of digits. Anything with no digits is treated as unassigned.
/// </summary>
public readonly record struct SupplyPosition(string Raw, int? Number, FeederSide Side)
{
    /// <summary>True when a feeder position number was found (i.e. the part is assigned to a feeder).</summary>
    public bool IsAssigned => Number.HasValue;

    private static readonly Regex DigitsRegex = new(@"\d+", RegexOptions.Compiled);

    public static SupplyPosition Parse(string? raw)
    {
        var text = (raw ?? string.Empty).Trim();

        if (text.Length == 0)
            return new SupplyPosition(text, null, FeederSide.Unspecified);

        // Authoritative side = trailing round-paren marker. The "[F]" square-bracket prefix is
        // constant (appears even on rear rows like "[F]501 (R)"), so it is NOT used for side.
        FeederSide side;
        if (text.Contains("(R)", StringComparison.OrdinalIgnoreCase))
            side = FeederSide.Rear;
        else if (text.Contains("(F)", StringComparison.OrdinalIgnoreCase))
            side = FeederSide.Front;
        else
            side = FeederSide.Unspecified;

        var m = DigitsRegex.Match(text);
        int? number = m.Success ? int.Parse(m.Value) : null;

        return new SupplyPosition(text, number, side);
    }

    public override string ToString() =>
        IsAssigned ? $"{Number} ({Side})" : "(unassigned)";
}
