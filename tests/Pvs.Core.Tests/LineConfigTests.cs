using Pvs.Core.Config;
using Xunit;

namespace Pvs.Core.Tests;

public class LineConfigTests
{
    private const string Json = """
    {
      "lineId": 1,
      "lineName": "LINE1PVS",
      "shiftTimes": [
        { "name": "Day",   "start": "07:30", "end": "19:30" },
        { "name": "Night", "start": "19:30", "end": "07:30" }
      ],
      "machines": [
        { "machine": 1, "port": "COM5", "model": "F130", "hasTrayFeeder": false },
        { "machine": 3, "port": "COM6", "model": "F209", "hasTrayFeeder": true }
      ],
      "serial": { "baudRate": 9600, "dataBits": 7, "parity": "Even", "stopBits": 1 },
      "forecast": { "redMinutes": 45, "amberMinutes": 120 },
      "central": { "server": "DESKTOP-TECHNIC\\SQLEXPRESS", "database": "ReelPart-New" },
      "badge": { "uidPrefix": "9999" }
    }
    """;

    [Fact]
    public void Parses_the_line_config()
    {
        var c = LineConfig.Parse(Json);

        Assert.Equal(1, c.LineId);
        Assert.Equal("LINE1PVS", c.LineName);
        Assert.Equal(2, c.Machines.Count);
        Assert.Equal("COM6", c.Machines[1].Port);
        Assert.True(c.Machines[1].HasTrayFeeder);
        Assert.Equal(9600, c.Serial.BaudRate);
        Assert.Equal("Even", c.Serial.Parity);
        Assert.Equal(45, c.Forecast.RedMinutes);
        Assert.Equal("DESKTOP-TECHNIC\\SQLEXPRESS", c.Central.Server);
    }

    [Fact]
    public void Builds_the_shift_schedule()
    {
        var sched = LineConfig.Parse(Json).ToShiftSchedule();
        Assert.Equal("Day", sched.ShiftAt(new System.DateTime(2026, 7, 23, 10, 0, 0)).Name);
        Assert.Equal("Night", sched.ShiftAt(new System.DateTime(2026, 7, 23, 22, 0, 0)).Name);
    }

    [Fact]
    public void Robot_section_is_optional_and_parses_the_dispatcher_url()
    {
        Assert.False(LineConfig.Parse(Json).Robot.Enabled);
        var c = LineConfig.Parse(Json.TrimEnd().TrimEnd('}') + ""","robot": { "dispatcherUrl": "http://192.168.0.169:8090" } }""");
        Assert.True(c.Robot.Enabled);
        Assert.Equal("http://192.168.0.169:8090", c.Robot.DispatcherUrl);
    }

    [Theory]
    [InlineData("9999-E221%", true)]   // badge
    [InlineData("3008-A001%", false)]  // reel
    public void Recognises_a_badge_uid_by_prefix(string uid, bool expected)
    {
        Assert.Equal(expected, LineConfig.Parse(Json).LooksLikeBadge(uid));
    }
}
