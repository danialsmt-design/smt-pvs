namespace Pvs.Core.Runtime;

/// <summary>
/// Availability summary for one shift window: how long the line was operating (available), stopped (down),
/// and actually producing (production = available - down). Durations are wall-clock, clipped to the window.
/// </summary>
public sealed record ShiftUptimeSummary(
    string ShiftKey,
    string ShiftName,
    DateTime WindowStart,
    DateTime WindowEnd,
    DateTime? FirstUp,
    TimeSpan Available,
    TimeSpan Down,
    TimeSpan Production,
    int Stops);

/// <summary>
/// Pure availability maths over a single shift window, computed from the line's first-up time and its
/// down spans (a "down" span = no machine online). Kept separate from the sampling clock so it is
/// unit-testable and shared by the live view and the archived-shift report.
/// </summary>
public static class ShiftUptime
{
    /// <summary>
    /// Availability over <c>[windowStart, min(windowEnd, now)]</c>.
    /// <para>
    /// <b>Available</b> = from the line's first-up (or the window start, whichever is later) to the window
    /// end — the operating window within the shift, so pre-start idle (line never came up yet) is excluded.
    /// <b>Down</b> = the down spans intersected with that operating window. <b>Production</b> = Available - Down.
    /// </para>
    /// </summary>
    public static (TimeSpan Available, TimeSpan Down, TimeSpan Production, int Stops) Compute(
        DateTime? firstUp, IEnumerable<DownSpan> spans, DateTime windowStart, DateTime windowEnd, DateTime now)
    {
        if (windowEnd > now) windowEnd = now;                       // an in-progress shift only counts up to now
        if (firstUp is null || windowEnd <= windowStart)
            return (TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, 0);

        var opStart = firstUp.Value > windowStart ? firstUp.Value : windowStart;
        if (opStart >= windowEnd) return (TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, 0);

        var available = windowEnd - opStart;
        var down = TimeSpan.Zero;
        int stops = 0;
        foreach (var s in spans)
        {
            var a = s.Start > opStart ? s.Start : opStart;         // clip each stop to the operating window
            var e = s.End ?? now;
            if (e > windowEnd) e = windowEnd;
            if (e > a) { down += e - a; stops++; }
        }
        if (down > available) down = available;                     // never report more down than the window itself
        return (available, down, available - down, stops);
    }
}
