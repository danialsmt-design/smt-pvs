using Pvs.Core.Inventory;
using Xunit;

namespace Pvs.Core.Tests;

public class PickupConsumptionTests
{
    [Fact]
    public void Consumed_is_attempted_pickups_since_load()
    {
        // Reel loaded when the feeder's VC read 0; now VC = 1903.
        Assert.Equal(1903, PickupConsumption.Consumed(vcNow: 1903, vcAtLoad: 0));
        // Reel swapped on mid-lot at VC = 1200; now VC = 1903 -> this reel consumed 703.
        Assert.Equal(703, PickupConsumption.Consumed(vcNow: 1903, vcAtLoad: 1200));
    }

    [Fact]
    public void Used_is_successful_pickups_and_attrition_is_the_rest()
    {
        // Real F114 capture: VC=1903 attempted, TC=1891 successful.
        Assert.Equal(1891, PickupConsumption.Used(tcNow: 1891, tcAtLoad: 0));
        Assert.Equal(12, PickupConsumption.Attrition(1903, 0, 1891, 0));   // 1903 − 1891 = 7 missed + 5 abnormal
    }

    [Fact]
    public void Remaining_draws_the_reel_down_by_attempted()
    {
        // A reel that started at 3664 with 1903 attempted -> 1761 remain (NOT 0, the boards×perBoard bug).
        Assert.Equal(1761, PickupConsumption.Remaining(startQty: 3664, vcNow: 1903, vcAtLoad: 0));
        Assert.Equal(0, PickupConsumption.Remaining(startQty: 1000, vcNow: 1903, vcAtLoad: 0));   // clamps at 0
    }

    [Fact]
    public void A_per_lot_reset_is_handled_as_consumption_since_the_reset()
    {
        // Operator reset the machine mid-reel: VC dropped from 1900 (load) to 40 (post-reset). The 40 is the
        // consumption since the reset, not a negative.
        Assert.Equal(40, PickupConsumption.Consumed(vcNow: 40, vcAtLoad: 1900));
        Assert.Equal(0, PickupConsumption.Attrition(40, 1900, 40, 1900));
    }
}
