namespace Pvs.Core.Inventory;

/// <summary>
/// Derives a machine's own PANEL count from its per-feeder pickup report (Sony C1Z). Every panel takes exactly
/// <c>MountPerPanel</c> SUCCESSFUL placements from a feeder, so <c>successful ÷ mount</c> is the number of panels
/// that machine has run since its counters were reset. The MEDIAN over the machine's tracked feeders rejects the
/// odd feeder that was not reset with the lot, or that spans a reel change. Successful (TC), not attempted (VC):
/// attempted includes retries after a missed/recognition pickup and so runs slightly high.
/// This is the machine's own board count — it corrects PVS's R0 tally the same way the operator's HMI key-in
/// does. It is NOT the per-feeder pickup-consumption model (attempted-since-load), which stays shadow-only.
/// Pure; no I/O.
/// </summary>
public static class MachineTally
{
    public sealed record Estimate(int Panels, int FeedersUsed, int MinPanels, int MaxPanels);

    /// <summary>Median panel count over the feeders that have mount &gt; 0 and at least one successful pickup.
    /// Null when fewer than <paramref name="minFeeders"/> feeders qualify (too little evidence to trust).</summary>
    public static Estimate? FromPickups(IEnumerable<(int Feeder, long Successful, int MountPerPanel)> feeders, int minFeeders = 3)
    {
        var per = new List<int>();
        foreach (var f in feeders)
        {
            if (f.MountPerPanel <= 0 || f.Successful <= 0) continue;
            per.Add((int)Math.Round(f.Successful / (double)f.MountPerPanel, MidpointRounding.AwayFromZero));
        }
        if (per.Count < Math.Max(1, minFeeders)) return null;
        per.Sort();
        int mid = per.Count / 2;
        int median = per.Count % 2 == 1 ? per[mid] : (int)Math.Round((per[mid - 1] + per[mid]) / 2.0, MidpointRounding.AwayFromZero);
        return new Estimate(median, per.Count, per[0], per[^1]);
    }

    /// <summary>
    /// A report belongs to the CURRENT lot only if its panel count is not beyond what this lot could have produced
    /// so far: PVS's lot count (last-machine R0s) plus in-line WIP slack (upstream machines lead the last machine
    /// by a few panels) plus 10 %. A report captured BEFORE the operator's per-lot reset carries the FINISHED lot's
    /// full count, far above that, and must be ignored — otherwise it would "correct" the new lot's tally upward.
    /// </summary>
    public static bool IsLotAligned(int machinePanels, int lotPanelsSoFar, int slackPanels = 10)
    {
        long cap = lotPanelsSoFar + Math.Max(slackPanels, (long)Math.Ceiling(lotPanelsSoFar * 0.10));
        return machinePanels <= cap;
    }
}
