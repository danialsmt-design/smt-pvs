using Pvs.Core.Inventory;
using Xunit;

namespace Pvs.Core.Tests;

public class MachineTallyTests
{
    [Fact]
    public void Panels_is_successful_over_mount_per_feeder()
    {
        // L1 M4 pre-reset C1Z 2026-09-06: 1504–1505 successful on 4-mount feeders = 376 panels (lot target was 375).
        var est = MachineTally.FromPickups(new[] { (111, 1505L, 4), (113, 1504L, 4), (115, 1504L, 4), (117, 1505L, 4) });
        Assert.NotNull(est);
        Assert.Equal(376, est!.Panels);
        Assert.Equal(4, est.FeedersUsed);
    }

    [Fact]
    public void Median_rejects_a_feeder_not_reset_with_the_lot()
    {
        // L1 M2 2026-09-06: 13 feeders at ~112–116 successful (28 panels) and F113 at 2128 (an un-reset feeder).
        var rows = new List<(int, long, int)>();
        for (int i = 0; i < 13; i++) rows.Add((100 + i, 112L + (i % 3), 4));
        rows.Add((113, 2128L, 4));
        var est = MachineTally.FromPickups(rows);
        Assert.Equal(28, est!.Panels);
        Assert.Equal(532, est.MaxPanels);   // visible in the spread, but not in the answer
    }

    [Fact]
    public void Too_few_feeders_gives_no_estimate()
    {
        Assert.Null(MachineTally.FromPickups(new[] { (1, 400L, 4), (2, 400L, 4) }));
        Assert.Null(MachineTally.FromPickups(new[] { (1, 0L, 4), (2, 0L, 4), (3, 0L, 4) }));   // no pickups yet
    }

    [Fact]
    public void Different_mounts_agree_on_the_same_panel_count()
    {
        var est = MachineTally.FromPickups(new[] { (1, 360L, 4), (2, 90L, 1), (3, 1710L, 19) });
        Assert.Equal(90, est!.Panels);
    }

    [Fact]
    public void Alignment_rejects_a_pre_reset_report_but_allows_wip_lead_and_drift()
    {
        Assert.False(MachineTally.IsLotAligned(machinePanels: 376, lotPanelsSoFar: 90));   // old lot's count
        Assert.True(MachineTally.IsLotAligned(machinePanels: 95, lotPanelsSoFar: 90));     // upstream WIP lead
        Assert.True(MachineTally.IsLotAligned(machinePanels: 300, lotPanelsSoFar: 280));   // 7% missed-R0 drift
        Assert.True(MachineTally.IsLotAligned(machinePanels: 8, lotPanelsSoFar: 0));       // lot just started
        Assert.False(MachineTally.IsLotAligned(machinePanels: 11, lotPanelsSoFar: 0));
    }
}
