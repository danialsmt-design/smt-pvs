using System.Text.RegularExpressions;

namespace Pvs.Core.People;

public enum BadgeRole
{
    /// <summary>Level not recognised — treated as no authority.</summary>
    Unknown,
    /// <summary>L1 — line operator. Can badge in and run verification, cannot release an interlock.</summary>
    Operator,
    /// <summary>L2 — supervisor. Can release an interlock.</summary>
    Supervisor,
    /// <summary>L3 — manager. Can release an interlock.</summary>
    Manager
}

/// <summary>
/// A person resolved from a scanned badge UID against the Users table.
/// AccessLevel in the database reads like "Supervisor (L2)" / "Manager (L3)"; the authority is
/// driven by the "(Ln)" level, not the text label. Operators (L1) are being added later.
///
/// SAFETY: the interlock-release check is deliberately level-based (L2+), so a new label like
/// "Team Lead (L2)" still grants release, and any unrecognised level grants nothing.
/// </summary>
public sealed record Badge(string UserId, string Name, string AccessLevel)
{
    private static readonly Regex LevelRegex = new(@"L\s*(\d)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Parsed authority level (1/2/3), or 0 if none could be read.</summary>
    public int Level { get; } = ParseLevel(AccessLevel);

    public BadgeRole Role => Level switch
    {
        1 => BadgeRole.Operator,
        2 => BadgeRole.Supervisor,
        3 => BadgeRole.Manager,
        _ => BadgeRole.Unknown
    };

    /// <summary>May start a parts change / badge in (any recognised person, L1 and above).</summary>
    public bool CanOperate => Level >= 1;

    /// <summary>May release a stopped interlock. Supervisors and above only (L2+).</summary>
    public bool CanReleaseInterlock => Level >= 2;

    private static int ParseLevel(string? accessLevel)
    {
        var m = LevelRegex.Match(accessLevel ?? string.Empty);
        return m.Success ? int.Parse(m.Groups[1].Value) : 0;
    }
}
