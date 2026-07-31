namespace Pvs.Core.Shifts;

public static class ForecastWording
{
    /// <summary>Compact "time left" for the monitor chip: "16 min", "1 h 05 m", "4 h 05 m".</summary>
    public static string Duration(double? hoursToExhaust)
    {
        if (hoursToExhaust is not double h || double.IsNaN(h) || double.IsInfinity(h))
            return "—"; // em-dash: not consuming / unknown

        int totalMinutes = (int)Math.Round(h * 60);
        if (totalMinutes < 1) return "now";
        if (totalMinutes < 60) return $"{totalMinutes} min";
        return $"{totalMinutes / 60} h {totalMinutes % 60:D2} m";
    }

    /// <summary>
    /// Shift-aware wall-clock wording. The forecast is in PRODUCTIVE hours, so it is projected
    /// forward only through shifts the line actually runs:
    ///   - falls in the current shift        -> "runs out at 14:05"
    ///   - crosses into a running next shift  -> "runs out at 02:15 (Night shift)"
    ///   - would cross into a shift that is NOT running -> "lasts past 19:30"
    /// A wrong/forgotten "night running" toggle can only make this MORE conservative, never wrong.
    /// </summary>
    /// <param name="isShiftRunning">Given a shift name, is the line scheduled to run it? (e.g. night on/off)</param>
    public static string WallClock(
        DateTime now,
        double? hoursToExhaust,
        ShiftSchedule schedule,
        Func<string, bool> isShiftRunning)
    {
        if (hoursToExhaust is not double h || double.IsNaN(h) || double.IsInfinity(h) || h < 0)
            return "—";

        var cursor = now;
        var shift = schedule.ShiftAt(now);
        var remaining = TimeSpan.FromHours(h);
        bool crossedShift = false;

        for (int guard = 0; guard < 21; guard++) // cap ~10 days of shifts
        {
            var end = schedule.EndAfter(cursor, shift);

            if (isShiftRunning(shift.Name))
            {
                var available = end - cursor;
                if (remaining <= available)
                {
                    var at = cursor + remaining;
                    return crossedShift
                        ? $"runs out at {at:HH:mm} ({shift.Name} shift)"
                        : $"runs out at {at:HH:mm}";
                }
                remaining -= available;
                cursor = end;
                shift = schedule.Next(shift);
                crossedShift = true;
            }
            else
            {
                // Line stops at this boundary and we don't project across the gap.
                return $"lasts past {cursor:HH:mm}";
            }
        }

        return $"lasts past {cursor:HH:mm}";
    }
}
