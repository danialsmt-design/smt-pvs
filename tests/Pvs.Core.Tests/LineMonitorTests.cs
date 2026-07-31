using Pvs.Core.Runtime;
using Pvs.Core.Serial;
using Xunit;

namespace Pvs.Core.Tests;

public class LineMonitorTests
{
    private static readonly DateTime T0 = new(2026, 7, 23, 12, 0, 0);

    // A channel with a steady 60 boards/h and one loaded feeder.
    private static MachineChannel Running(int machine, int feeder, string part, int mountedPerBoard, int qty)
    {
        var ch = new MachineChannel(machine, _ => { });
        ch.Inventory.Configure(feeder, part, mountedPerBoard);
        ch.Inventory.LoadReel(feeder, $"uid-{machine}-{feeder}", qty);
        // Establish a 60 boards/h productive rate.
        ch.Feed(SonyFrame.Build("R1ST"), T0);
        for (int i = 1; i <= 3; i++) ch.Feed(SonyFrame.Build("R0CT"), T0.AddSeconds(i * 60));
        return ch;
    }

    [Fact]
    public void Combines_same_part_across_machines_and_ranks_soonest_first()
    {
        // PARTA on machines 2 and 4 (combined); PARTB on machine 3 lasts far longer.
        var m2 = Running(2, 108, "PARTA", mountedPerBoard: 10, qty: 3600);
        var m4 = Running(4, 118, "PARTA", mountedPerBoard: 2, qty: 400);
        var m3 = Running(3, 130, "PARTB", mountedPerBoard: 1, qty: 100000);

        var line = new LineMonitor(new[] { m2, m3, m4 });
        var forecast = line.Forecast();

        Assert.Equal("PARTA", forecast[0].PartNumber);              // runs out first
        // 3 boards already ran (that's how the rate was established), so each feeder is drawn down:
        // m2: 3600 - 3*10 = 3570; m4: 400 - 3*2 = 394; combined = 3964.
        Assert.Equal(3964, forecast[0].Remaining);
        Assert.Equal(new[] { (2, 108), (4, 118) }, forecast[0].Locations);
        Assert.Equal("PARTB", forecast[1].PartNumber);
    }

    [Fact]
    public void Untracked_feeders_do_not_appear()
    {
        var ch = new MachineChannel(2, _ => { });
        ch.Inventory.Configure(108, "PARTA", 10);   // configured but no reel loaded
        var line = new LineMonitor(new[] { ch });
        Assert.Empty(line.Forecast());
    }
}
