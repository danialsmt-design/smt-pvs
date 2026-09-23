namespace Pvs.Core.Inventory;

/// <summary>
/// Danial 2026-09-24: "recheck the balance — when it was loaded, when it exhausted, how many boards were completed;
/// if it does not match closely, adjust the balance; check the balance periodically and update it."
/// <para>
/// The reel's balance is re-derived from three facts that do not depend on one machine's serial stream:
/// the quantity confirmed at load, the LINE board clock (panels the line completed, kept across restarts and
/// HMI adoptions) at load and now, and the placements per panel — the feeder-list count, or the rate learned
/// from that part's own confirmed exhausts once enough have been seen. When the tracked balance drifts from
/// that by more than the tolerance it is set to the derived value.
/// </para>
/// Pure arithmetic; the coordinator applies it.
/// </summary>
public static class BalanceReconciler
{
    /// <summary>Panels' worth of parts the balance may drift before it is corrected (a few boards in flight,
    /// a restart gap under five minutes).</summary>
    public const int TolerancePanels = 3;
    /// <summary>Or 1 % of the loaded quantity, whichever is larger.</summary>
    public const double TolerancePct = 1.0;

    /// <summary>The balance the reel should have: load qty − rate × panels since load, never below zero.</summary>
    public static int Expected(int loadQty, double ratePerPanel, long panelsSinceLoad)
    {
        if (loadQty <= 0 || panelsSinceLoad < 0) return Math.Max(0, loadQty);
        double used = ratePerPanel * panelsSinceLoad;
        return (int)Math.Clamp(Math.Round(loadQty - used), 0, loadQty);
    }

    public static int Tolerance(int loadQty, double ratePerPanel) =>
        (int)Math.Max(Math.Ceiling(TolerancePanels * Math.Max(1.0, ratePerPanel)), Math.Ceiling(loadQty * TolerancePct / 100.0));

    /// <summary>True when the tracked balance is off the expected one by more than the tolerance.</summary>
    public static bool NeedsAdjust(int tracked, int expected, int tolerance) => Math.Abs(tracked - expected) > tolerance;

    /// <summary>The placements-per-panel to derive with: the learned real rate when the part has enough confirmed
    /// exhausts behind it and the learned rate is sane (within ±50 % of the list), otherwise the list count.</summary>
    public static double RateFor(int mountedPerPanel, PartCalibration? learned, int minSamples = 2)
    {
        if (learned is null || learned.Samples < minSamples || mountedPerPanel <= 0) return mountedPerPanel;
        double r = learned.CorrectedPerBoard(mountedPerPanel);
        if (r < mountedPerPanel * 0.5 || r > mountedPerPanel * 1.5) return mountedPerPanel;   // implausible learning — keep the list
        return r;
    }

    /// <summary>The real placements per panel a confirmed exhaust proves: the reel emptied, so everything it held
    /// went out over the panels it saw. Null when the run is too short to mean anything.</summary>
    public static double? RealRate(int loadQty, long panelsSinceLoad, int minPanels = 20) =>
        loadQty > 0 && panelsSinceLoad >= minPanels ? loadQty / (double)panelsSinceLoad : null;
}
