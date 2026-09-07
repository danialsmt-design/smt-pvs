using Pvs.Core.Shifts;
using Xunit;

namespace Pvs.Core.Tests;

public class ShiftScheduleClockTests
{
    private static readonly ShiftSchedule S = new(new[]
    {
        new ShiftDefinition("Day",   new TimeOnly(7, 35),  new TimeOnly(19, 35)),
        new ShiftDefinition("Night", new TimeOnly(19, 35), new TimeOnly(7, 35)),
    });

    [Fact]
    public void Dpc_name_follows_the_configured_boundary()
    {
        var d = new DateTime(2026, 9, 7);
        Assert.Equal("Morning", S.DpcName(d.AddHours(7).AddMinutes(35)));
        Assert.Equal("Morning", S.DpcName(d.AddHours(19).AddMinutes(34)));
        Assert.Equal("Night",   S.DpcName(d.AddHours(19).AddMinutes(35)));
        Assert.Equal("Night",   S.DpcName(d.AddHours(3)));
    }

    [Fact]
    public void Slot_start_is_the_shift_start_or_midnight_for_the_night_tail()
    {
        var d = new DateTime(2026, 9, 7);
        Assert.Equal(d.AddHours(7).AddMinutes(35),  S.SlotStart(d.AddHours(10)));
        Assert.Equal(d.AddHours(19).AddMinutes(35), S.SlotStart(d.AddHours(23)));
        Assert.Equal(d.AddDays(1),                  S.SlotStart(d.AddDays(1).AddHours(3)));   // post-midnight tail
        Assert.Equal(d.AddHours(19).AddMinutes(35), S.ShiftStart(d.AddDays(1).AddHours(3)));  // but the shift began yesterday
    }

    [Fact]
    public void Window_gives_the_sheet_span_for_a_date_and_dpc_shift()
    {
        var d = new DateTime(2026, 9, 7);
        var m = S.Window(d, "Morning")!.Value;
        Assert.Equal(d.AddHours(7).AddMinutes(35), m.From); Assert.Equal(d.AddHours(19).AddMinutes(35), m.To);
        var n = S.Window(d, "Night")!.Value;
        Assert.Equal(d.AddHours(19).AddMinutes(35), n.From); Assert.Equal(d.AddDays(1).AddHours(7).AddMinutes(35), n.To);
        Assert.Null(S.Window(d, "Afternoon"));
    }
}
