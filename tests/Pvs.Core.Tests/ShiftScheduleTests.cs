using System;
using Pvs.Core.Shifts;
using Xunit;

namespace Pvs.Core.Tests;

public class ShiftScheduleTests
{
    private static readonly ShiftSchedule Std = ShiftSchedule.Standard;

    [Theory]
    [InlineData("2026-07-27 07:30", "Day")]
    [InlineData("2026-07-27 12:00", "Day")]
    [InlineData("2026-07-27 19:29", "Day")]
    [InlineData("2026-07-27 19:30", "Night")]
    [InlineData("2026-07-27 23:59", "Night")]
    [InlineData("2026-07-27 02:00", "Night")]
    [InlineData("2026-07-27 07:29", "Night")]
    public void ShiftAt_names_the_window(string when, string expected) =>
        Assert.Equal(expected, Std.ShiftAt(DateTime.Parse(when)).Name);

    [Fact]
    public void ShiftKey_is_stable_across_a_whole_day_shift()
    {
        var a = Std.ShiftKey(DateTime.Parse("2026-07-27 07:35"));
        var b = Std.ShiftKey(DateTime.Parse("2026-07-27 18:00"));
        Assert.Equal("2026-07-27|Day", a);
        Assert.Equal(a, b);
    }

    [Fact]
    public void ShiftKey_before_midnight_and_after_midnight_share_the_same_night_key()
    {
        // The night shift starts 2026-07-27 19:30 and runs to 2026-07-28 07:30 — both instants are the
        // SAME shift period, so both keys must be dated to the shift's start day (the 27th).
        var beforeMidnight = Std.ShiftKey(DateTime.Parse("2026-07-27 22:00"));
        var afterMidnight = Std.ShiftKey(DateTime.Parse("2026-07-28 03:00"));
        Assert.Equal("2026-07-27|Night", beforeMidnight);
        Assert.Equal(beforeMidnight, afterMidnight);
    }

    [Fact]
    public void ShiftKey_changes_at_each_boundary()
    {
        var lastNight = Std.ShiftKey(DateTime.Parse("2026-07-27 07:00")); // 26th night
        var day = Std.ShiftKey(DateTime.Parse("2026-07-27 08:00"));       // 27th day
        var night = Std.ShiftKey(DateTime.Parse("2026-07-27 20:00"));     // 27th night
        Assert.Equal("2026-07-26|Night", lastNight);
        Assert.Equal("2026-07-27|Day", day);
        Assert.Equal("2026-07-27|Night", night);
        Assert.NotEqual(lastNight, day);
        Assert.NotEqual(day, night);
    }
}
