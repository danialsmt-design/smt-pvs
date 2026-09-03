namespace Pvs.Core.Inventory;

/// <summary>
/// The feeder consumption model grounded in each machine's OWN per-feeder pickup counts (the Sony C1Z
/// "Summary by Supply Location"), which is the truth for how many parts left a reel — unlike
/// <c>boards × placements-per-board</c>, which rides on PVS's board count (drifts on missed serial) and
/// assumes zero pickup waste. Confirmed rule (Danial, 2026-08-27):
/// <list type="bullet">
///   <item><b>Parts removed from the reel</b> = <b>Attempted pickups (VC)</b> — every attempt takes a part off the tape.</item>
///   <item><b>Parts used</b> (placed on boards) = <b>Successful pickups (TC)</b>.</item>
///   <item><b>Attrition</b> (waste) = <b>VC − TC</b> = missed + abnormal pickups.</item>
/// </list>
/// The C1Z counters reset when the operator resets the machine each lot, so a feeder's VC/TC are cumulative
/// FOR THE CURRENT LOT. To scope to one reel, subtract the counter reading captured when that reel was loaded
/// (<c>*AtLoad</c>). Pure and side-effect free so it can be unit-tested and shared by the shadow check and the
/// live decrement.
/// </summary>
public static class PickupConsumption
{
    /// <summary>Parts removed from the reel since it was loaded = attempted pickups since load. Handles the
    /// per-lot reset: if the current reading is BELOW the load baseline, the operator reset the counter, so the
    /// post-reset reading is the whole consumption since the reset.</summary>
    public static long Consumed(long vcNow, long vcAtLoad) =>
        vcNow >= vcAtLoad ? vcNow - vcAtLoad : vcNow;

    /// <summary>Parts placed on boards since the reel was loaded = successful pickups since load.</summary>
    public static long Used(long tcNow, long tcAtLoad) =>
        tcNow >= tcAtLoad ? tcNow - tcAtLoad : tcNow;

    /// <summary>Attrition (wasted parts) since the reel was loaded = consumed − used = missed + abnormal.</summary>
    public static long Attrition(long vcNow, long vcAtLoad, long tcNow, long tcAtLoad) =>
        System.Math.Max(0, Consumed(vcNow, vcAtLoad) - Used(tcNow, tcAtLoad));

    /// <summary>Reel pieces remaining = starting quantity − parts removed (attempted) since load, clamped ≥ 0.</summary>
    public static int Remaining(long startQty, long vcNow, long vcAtLoad) =>
        (int)System.Math.Max(0, startQty - Consumed(vcNow, vcAtLoad));
}
