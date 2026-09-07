using Pvs.Core.Inventory;
using Xunit;

namespace Pvs.Core.Tests;

public class MachineInventoryUnloadTests
{
    [Fact]
    public void UnloadReel_stops_tracking_but_keeps_the_feeder_configured()
    {
        var inv = new MachineInventory(2);
        inv.Configure(113, "VR8-5880-242", 4);
        inv.LoadReel(113, "880242-I148%", 4000);
        inv.OnBoardComplete();
        Assert.Equal(3996, inv.Get(113)!.Remaining);

        inv.UnloadReel(113);
        var f = inv.Get(113)!;
        Assert.False(f.IsTracked);
        Assert.Null(f.ReelUid);
        Assert.Equal(0, f.Remaining);
        Assert.Equal("VR8-5880-242", f.PartNumber);   // still expected here

        inv.OnBoardComplete();                         // an unloaded feeder decrements nothing
        Assert.Equal(0, inv.Get(113)!.Remaining);
        inv.UnloadReel(999);                           // unknown feeder: no-op, no throw
    }
}
