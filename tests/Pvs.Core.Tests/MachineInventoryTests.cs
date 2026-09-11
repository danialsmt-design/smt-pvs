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

    [Fact]
    public void Sync_to_higher_board_count_draws_feeders_down_by_missed_boards()
    {
        // PVS saw 10 boards but the machine's HMI shows 15 — 5 were missed (PVS was blind). The operator keys 15;
        // every tracked feeder must drop by mounted-per-board × 5 (the missed boards), on top of the 10 already applied.
        var inv = new MachineInventory(2);
        inv.Configure(108, "P1", 18);
        inv.LoadReel(108, "u1", 4000);
        for (int i = 0; i < 10; i++) inv.OnBoardComplete();      // 4000 - 180 = 3820, boardsApplied = 10
        Assert.Equal(10, inv.BoardsApplied);

        int delta = inv.SyncToBoardCount(15);                    // truth = 15 panels
        Assert.Equal(5, delta);
        Assert.Equal(15, inv.BoardsApplied);
        Assert.Equal(4000 - 15 * 18, inv.Get(108)!.Remaining);   // exactly 15 boards' worth consumed
    }

    [Fact]
    public void Sync_to_lower_board_count_hands_pieces_back()
    {
        // PVS double-counted to 20 but the HMI shows 12 — give back the 8 over-counted boards.
        var inv = new MachineInventory(2);
        inv.Configure(108, "P1", 10);
        inv.LoadReel(108, "u1", 1000);
        for (int i = 0; i < 20; i++) inv.OnBoardComplete();      // 1000 - 200 = 800
        int delta = inv.SyncToBoardCount(12);
        Assert.Equal(-8, delta);
        Assert.Equal(1000 - 12 * 10, inv.Get(108)!.Remaining);  // back to 12 boards' worth
    }

    [Fact]
    public void Seed_sets_tally_without_touching_feeders_and_prevents_double_decrement()
    {
        // Restart mid-lot: feeders' remaining is restored from the DB (already reflects 300 boards), but a fresh
        // inventory's tally is 0. Seeding the tally to 300 means a later HMI sync to 305 draws down only the 5
        // MISSED boards — NOT all 305 again.
        var inv = new MachineInventory(2);
        inv.Configure(108, "P1", 4);
        inv.LoadReel(108, "u1", startingQty: 4000 - 300 * 4);    // restored balance = 2800 (300 boards already gone)
        Assert.Equal(0, inv.BoardsApplied);

        inv.SeedBoardsApplied(300);                              // re-anchor to the lot's board count
        Assert.Equal(300, inv.BoardsApplied);
        Assert.Equal(2800, inv.Get(108)!.Remaining);            // feeders untouched by the seed

        inv.SyncToBoardCount(305);                               // operator keys the HMI truth
        Assert.Equal(2800 - 5 * 4, inv.Get(108)!.Remaining);    // only the 5 missed boards, not 305
    }

    [Fact]
    public void Correction_is_shared_by_boards_each_reel_was_on_the_machine_for()
    {
        var inv = new MachineInventory(1);
        inv.Configure(108, "OLD-PART", 4);
        inv.Configure(109, "NEW-PART", 4);
        inv.LoadReel(108, "old-uid", 4000);          // on the machine from the start of the run
        for (int i = 0; i < 100; i++) inv.OnBoardComplete();   // PVS counted 100 panels
        inv.LoadReel(109, "new-uid", 4000);          // loaded NOW, at panel 100
        // The operator's HMI says 120 panels: 20 were missed — all of them BEFORE the new reel was loaded.
        int delta = inv.SyncToBoardCount(120);
        Assert.Equal(20, delta);
        Assert.Equal(4000 - 100 * 4 - 20 * 4, inv.Get(108)!.Remaining);   // old reel takes the whole correction
        Assert.Equal(4000, inv.Get(109)!.Remaining);                       // the new reel ran none of them
        Assert.Equal(120, inv.BoardsApplied);
    }

    [Fact]
    public void Correction_share_is_proportional_for_a_reel_loaded_mid_run()
    {
        var inv = new MachineInventory(1);
        inv.Configure(110, "P", 2);
        inv.LoadReel(110, "u0", 1000);
        for (int i = 0; i < 50; i++) inv.OnBoardComplete();
        inv.LoadReel(110, "u1", 1000);               // swapped at panel 50
        for (int i = 0; i < 50; i++) inv.OnBoardComplete();   // now 100 applied; this reel ran 50 of them
        inv.SyncToBoardCount(110);                   // 10 missed, spread over the run -> this reel takes half
        Assert.Equal(1000 - 50 * 2 - 5 * 2, inv.Get(110)!.Remaining);
    }
}
