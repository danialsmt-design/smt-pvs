using Pvs.Core.Inventory;
using Xunit;

namespace Pvs.Core.Tests;

public class MachineInventoryTests
{
    [Fact]
    public void Decrements_by_mounted_per_board_each_board()
    {
        // Real L264 numbers: feeder 108 = VR8-1300-123, 18 placements/board.
        var inv = new MachineInventory(machine: 2);
        inv.Configure(108, "VR8-1300-123", mountedPerBoard: 18);
        inv.LoadReel(108, "3008-D001%", startingQty: 4000);

        inv.OnBoardComplete();
        inv.OnBoardComplete();

        Assert.Equal(4000 - 2 * 18, inv.Get(108)!.Remaining);
    }

    [Fact]
    public void Untracked_feeder_does_not_decrement()
    {
        // Configured but no reel loaded yet -> we don't know the qty, so it stays put.
        var inv = new MachineInventory(machine: 2);
        var f = inv.Configure(113, "VC8-9370-102", mountedPerBoard: 30);
        inv.OnBoardComplete();
        Assert.False(f.IsTracked);
        Assert.Equal(0, f.Remaining);
    }

    [Fact]
    public void Remaining_clamps_at_zero()
    {
        var inv = new MachineInventory(machine: 2);
        inv.Configure(108, "VR8-1300-123", 18);
        inv.LoadReel(108, "uid", startingQty: 20);   // only 20 left
        inv.OnBoardComplete();                         // -18 -> 2
        inv.OnBoardComplete();                         // -18 -> clamp 0, not -16
        Assert.Equal(0, inv.Get(108)!.Remaining);
    }

    [Fact]
    public void Reel_swap_zeroes_old_then_loads_new()
    {
        var inv = new MachineInventory(machine: 2);
        inv.Configure(124, "VC8-8380-106", 2);
        inv.LoadReel(124, "old-uid", 500);
        inv.OnBoardComplete();                 // 498

        inv.MarkExhausted(124);                // genuine parts-out: old reel -> 0
        Assert.Equal(0, inv.Get(124)!.Remaining);

        inv.LoadReel(124, "new-uid", 4000);    // new reel, operator-keyed qty
        Assert.Equal("new-uid", inv.Get(124)!.ReelUid);
        Assert.Equal(4000, inv.Get(124)!.Remaining);
    }

    [Fact]
    public void Mode_d_recount_sets_exact_remaining()
    {
        var inv = new MachineInventory(machine: 2);
        inv.Configure(108, "VR8-1300-123", 18);
        inv.LoadReel(108, "uid", 4000);
        inv.OnBoardComplete();                 // system estimate 3982

        inv.SetRemaining(108, 3500);           // operator counted 3500 on the component counter
        Assert.Equal(3500, inv.Get(108)!.Remaining);

        inv.OnBoardComplete();                 // continues from the corrected baseline
        Assert.Equal(3500 - 18, inv.Get(108)!.Remaining);
    }

    [Fact]
    public void Configuring_unknown_feeder_then_operating_throws_only_when_missing()
    {
        var inv = new MachineInventory(machine: 2);
        Assert.Throws<InvalidOperationException>(() => inv.LoadReel(999, "uid", 100));
    }
}
