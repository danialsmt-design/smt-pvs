namespace Pvs.Core.Runtime;

/// <summary>
/// The plant's scheduled break windows (times-of-day that recur every day, covering both shifts). Pure and
/// testable. Two uses in downtime capture: suppress the "why stopped?" prompt during a break (the stop is
/// expected), and subtract break overlap from a parts-exhaust recovery so a reel that empties just before a
/// break doesn't read as a long changeover.
///
/// Day shift (07:30-19:30): 12:00-12:45 + 15:30-15:45. Night shift (19:30-07:30): mirrored 00:00-00:45 +
/// 03:30-03:45. Configured in <c>line.config.json</c>; these are the defaults.
/// </summary>
public sealed class BreakWindows
{
    private readonly List<(TimeOnly Start, TimeOnly End)> _windows;

    public BreakWindows(IEnumerable<(TimeOnly Start, TimeOnly End)> windows) =>
        _windows = windows.Where(w => w.End > w.Start).ToList();

    public bool Any => _windows.Count > 0;

    /// <summary>Is <paramref name="t"/> inside a scheduled break?</summary>
    public bool IsBreak(DateTime t)
    {
        var tod = TimeOnly.FromDateTime(t);
        return _windows.Any(w => tod >= w.Start && tod < w.End);
    }

    /// <summary>
    /// Minutes of the span [<paramref name="start"/>, <paramref name="end"/>] that fall inside any break window.
    /// Materialises each daily window on the dates the span touches, so a span that crosses a break (or midnight)
    /// is handled. Recovery/stop spans are short, so the day loop is tiny.
    /// </summary>
    public double OverlapMinutes(DateTime start, DateTime end)
    {
        if (end <= start || _windows.Count == 0) return 0;
        double mins = 0;
        for (var day = start.Date; day <= end.Date; day = day.AddDays(1))
        {
            foreach (var w in _windows)
            {
                var ws = day + w.Start.ToTimeSpan();
                var we = day + w.End.ToTimeSpan();
                var os = start > ws ? start : ws;
                var oe = end < we ? end : we;
                if (oe > os) mins += (oe - os).TotalMinutes;
            }
        }
        return mins;
    }

    /// <summary>Recovery/stop minutes with any break overlap removed (never negative).</summary>
    public double NetMinutes(DateTime start, DateTime end) =>
        Math.Max(0, (end - start).TotalMinutes - OverlapMinutes(start, end));
}
