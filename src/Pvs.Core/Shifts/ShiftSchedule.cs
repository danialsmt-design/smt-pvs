namespace Pvs.Core.Shifts;

/// <summary>One shift window in the day. End may wrap past midnight (Night = 19:30 -> 07:30).</summary>
public sealed record ShiftDefinition(string Name, TimeOnly Start, TimeOnly End)
{
    /// <summary>True if a time-of-day falls in this shift (handles the midnight wrap).</summary>
    public bool Contains(TimeOnly t) =>
        Start <= End ? (t >= Start && t < End)   // same-day window
                     : (t >= Start || t < End);  // wraps midnight
}

/// <summary>
/// The line's shift layout. For line 1 (confirmed): Day 07:30-19:30, Night 19:30-07:30.
/// The shifts are contiguous and cover the whole 24 h, so each shift's End equals the next
/// shift's Start.
/// </summary>
public sealed class ShiftSchedule
{
    private readonly IReadOnlyList<ShiftDefinition> _shifts;

    public ShiftSchedule(IReadOnlyList<ShiftDefinition> shifts)
    {
        if (shifts is null || shifts.Count == 0)
            throw new ArgumentException("At least one shift is required.", nameof(shifts));
        _shifts = shifts;
    }

    /// <summary>The standard two-shift day used on line 1.</summary>
    public static ShiftSchedule Standard { get; } = new(new[]
    {
        new ShiftDefinition("Day",   new TimeOnly(7, 30),  new TimeOnly(19, 30)),
        new ShiftDefinition("Night", new TimeOnly(19, 30), new TimeOnly(7, 30)),
    });

    public ShiftDefinition ShiftAt(DateTime when)
    {
        var t = TimeOnly.FromDateTime(when);
        foreach (var s in _shifts)
            if (s.Contains(t)) return s;
        // Contiguous 24h coverage means this should never happen.
        throw new InvalidOperationException($"No shift covers {t}.");
    }

    /// <summary>
    /// A stable identity for the shift PERIOD containing <paramref name="when"/> — the calendar date on
    /// which that shift instance started plus its name (e.g. "2026-07-27|Night"). It changes exactly at
    /// each shift boundary, so two times within one shift share a key even across midnight. Used to reset
    /// per-shift status (a check done last shift no longer counts for this shift).
    /// </summary>
    public string ShiftKey(DateTime when)
    {
        var shift = ShiftAt(when);
        var start = when.Date + shift.Start.ToTimeSpan();
        if (start > when) start = start.AddDays(-1);   // shift started before midnight (night wrap)
        return start.ToString("yyyy-MM-dd") + "|" + shift.Name;
    }

    /// <summary>The shift that begins where <paramref name="shift"/> ends.</summary>
    public ShiftDefinition Next(ShiftDefinition shift)
    {
        foreach (var s in _shifts)
            if (s.Start == shift.End) return s;
        throw new InvalidOperationException($"No shift starts at {shift.End} (schedule not contiguous).");
    }

    /// <summary>First wall-clock instant at or after <paramref name="after"/> where <paramref name="shift"/> ends.</summary>
    public DateTime EndAfter(DateTime after, ShiftDefinition shift)
    {
        var end = after.Date + shift.End.ToTimeSpan();
        while (end <= after) end = end.AddDays(1);
        return end;
    }
}
