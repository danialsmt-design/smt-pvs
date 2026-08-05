using System;
using System.Collections.Generic;
using Pvs.Core.Runtime;
using Xunit;

namespace Pvs.Core.Tests;

public class ShiftUptimeTests
{
    private static DateTime T(int d, int h, int m) => new(2026, 8, d, h, m, 0);

    // Day shift 07:30 -> 19:30, line up the whole time, no stops.
    [Fact]
    public void Full_shift_up_no_stops_is_all_production()
    {
        var (avail, down, prod, stops) = ShiftUptime.Compute(
            firstUp: T(5, 7, 30), spans: new List<DownSpan>(),
            windowStart: T(5, 7, 30), windowEnd: T(5, 19, 30), now: T(5, 19, 35));
        Assert.Equal(TimeSpan.FromHours(12), avail);
        Assert.Equal(TimeSpan.Zero, down);
        Assert.Equal(TimeSpan.FromHours(12), prod);
        Assert.Equal(0, stops);
    }

    // A one-hour stop mid-shift is deducted from production.
    [Fact]
    public void A_stop_is_deducted_from_production()
    {
        var spans = new List<DownSpan> { new(T(5, 12, 0), T(5, 13, 0)) };
        var (avail, down, prod, stops) = ShiftUptime.Compute(
            firstUp: T(5, 7, 30), spans: spans,
            windowStart: T(5, 7, 30), windowEnd: T(5, 19, 30), now: T(5, 19, 35));
        Assert.Equal(TimeSpan.FromHours(12), avail);
        Assert.Equal(TimeSpan.FromHours(1), down);
        Assert.Equal(TimeSpan.FromHours(11), prod);
        Assert.Equal(1, stops);
    }

    // Line starts late (08:30, an hour into the shift): the pre-start hour is NOT available time.
    [Fact]
    public void Pre_start_idle_is_excluded_from_available()
    {
        var (avail, down, prod, _) = ShiftUptime.Compute(
            firstUp: T(5, 8, 30), spans: new List<DownSpan>(),
            windowStart: T(5, 7, 30), windowEnd: T(5, 19, 30), now: T(5, 19, 35));
        Assert.Equal(TimeSpan.FromHours(11), avail);   // 08:30 -> 19:30
        Assert.Equal(TimeSpan.Zero, down);
        Assert.Equal(TimeSpan.FromHours(11), prod);
    }

    // Night shift crosses midnight (19:30 day 4 -> 07:30 day 5); a stop across midnight is counted whole.
    [Fact]
    public void Night_shift_crosses_midnight()
    {
        var spans = new List<DownSpan> { new(T(4, 23, 30), T(5, 0, 30)) };   // 1h stop straddling midnight
        var (avail, down, prod, stops) = ShiftUptime.Compute(
            firstUp: T(4, 19, 30), spans: spans,
            windowStart: T(4, 19, 30), windowEnd: T(5, 7, 30), now: T(5, 7, 35));
        Assert.Equal(TimeSpan.FromHours(12), avail);
        Assert.Equal(TimeSpan.FromHours(1), down);
        Assert.Equal(TimeSpan.FromHours(11), prod);
        Assert.Equal(1, stops);
    }

    // An in-progress shift only counts up to 'now'.
    [Fact]
    public void In_progress_shift_counts_up_to_now()
    {
        var (avail, _, prod, _) = ShiftUptime.Compute(
            firstUp: T(5, 7, 30), spans: new List<DownSpan>(),
            windowStart: T(5, 7, 30), windowEnd: T(5, 19, 30), now: T(5, 10, 30));
        Assert.Equal(TimeSpan.FromHours(3), avail);    // 07:30 -> 10:30
        Assert.Equal(TimeSpan.FromHours(3), prod);
    }

    // An open stop (still down at report time) is counted to the window end.
    [Fact]
    public void Open_stop_counts_to_window_end()
    {
        var spans = new List<DownSpan> { new(T(5, 18, 30), null) };   // went down at 18:30, never came back
        var (avail, down, prod, _) = ShiftUptime.Compute(
            firstUp: T(5, 7, 30), spans: spans,
            windowStart: T(5, 7, 30), windowEnd: T(5, 19, 30), now: T(5, 19, 30));
        Assert.Equal(TimeSpan.FromHours(12), avail);
        Assert.Equal(TimeSpan.FromHours(1), down);     // 18:30 -> 19:30
        Assert.Equal(TimeSpan.FromHours(11), prod);
    }

    // Line never came up this shift: nothing available, nothing produced.
    [Fact]
    public void Never_up_is_all_zero()
    {
        var (avail, down, prod, stops) = ShiftUptime.Compute(
            firstUp: null, spans: new List<DownSpan>(),
            windowStart: T(5, 7, 30), windowEnd: T(5, 19, 30), now: T(5, 19, 35));
        Assert.Equal(TimeSpan.Zero, avail);
        Assert.Equal(TimeSpan.Zero, down);
        Assert.Equal(TimeSpan.Zero, prod);
        Assert.Equal(0, stops);
    }
}
