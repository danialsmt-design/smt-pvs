using Pvs.Core.Shifts;
using Xunit;

namespace Pvs.Core.Tests;

public class ForecastWordingTests
{
    private static readonly ShiftSchedule Sched = ShiftSchedule.Standard;

    // Day shift only (line 1's night is off).
    private static bool DayOnly(string name) => name == "Day";
    private static bool BothShifts(string _) => true;

    // ---- Duration chip ----

    [Theory]
    [InlineData(16.0 / 60, "16 min")]
    [InlineData(1 + 5.0 / 60, "1 h 05 m")]
    [InlineData(4 + 5.0 / 60, "4 h 05 m")]
    [InlineData(0.0, "now")]
    public void Duration_formats(double hours, string expected)
        => Assert.Equal(expected, ForecastWording.Duration(hours));

    [Fact]
    public void Duration_null_is_dash() => Assert.Equal("—", ForecastWording.Duration(null));

    // ---- Wall-clock, within the current shift ----

    [Fact]
    public void Within_current_shift_shows_the_time()
    {
        var now = new DateTime(2026, 7, 23, 12, 0, 0);           // midday, Day shift
        var s = ForecastWording.WallClock(now, 2.0, Sched, DayOnly);
        Assert.Equal("runs out at 14:00", s);
    }

    // ---- Wall-clock, beyond current shift, next shift NOT running ----

    [Fact]
    public void Beyond_shift_when_night_is_off_says_lasts_past_shift_end()
    {
        var now = new DateTime(2026, 7, 23, 17, 0, 0);           // 17:00 Day shift, 2.5h to 19:30
        var s = ForecastWording.WallClock(now, 6.0, Sched, DayOnly); // needs 6h, only 2.5h left today
        Assert.Equal("lasts past 19:30", s);
    }

    // ---- Wall-clock, crossing into a running next shift ----

    [Fact]
    public void Crossing_into_running_night_shift_labels_it()
    {
        var now = new DateTime(2026, 7, 23, 17, 0, 0);           // 2.5h left in Day, then Night runs
        // 9h: 2.5h finishes the day at 19:30, remaining 6.5h into the night -> 02:00.
        var s = ForecastWording.WallClock(now, 9.0, Sched, BothShifts);
        Assert.Equal("runs out at 02:00 (Night shift)", s);
    }

    [Fact]
    public void Exactly_at_shift_end_still_within_current_shift()
    {
        var now = new DateTime(2026, 7, 23, 17, 30, 0);          // exactly 2h to 19:30
        var s = ForecastWording.WallClock(now, 2.0, Sched, DayOnly);
        Assert.Equal("runs out at 19:30", s);
    }

    [Fact]
    public void Null_forecast_is_dash()
        => Assert.Equal("—", ForecastWording.WallClock(new DateTime(2026, 7, 23, 12, 0, 0), null, Sched, BothShifts));

    // ---- schedule basics ----

    [Fact]
    public void ShiftAt_identifies_day_and_night()
    {
        Assert.Equal("Day", Sched.ShiftAt(new DateTime(2026, 7, 23, 8, 0, 0)).Name);
        Assert.Equal("Night", Sched.ShiftAt(new DateTime(2026, 7, 23, 23, 0, 0)).Name);
        Assert.Equal("Night", Sched.ShiftAt(new DateTime(2026, 7, 23, 2, 0, 0)).Name); // after midnight
    }
}
