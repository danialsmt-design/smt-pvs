using Pvs.Core.Events;
using Pvs.Core.Serial;
using Xunit;

namespace Pvs.Core.Tests;

public class BoardRateTrackerTests
{
    private static readonly DateTime T0 = new(2026, 7, 22, 20, 0, 0);

    private static void Feed(BoardRateTracker t, string payload, double sec)
        => t.Observe(SonyMessage.Parse(payload), T0.AddSeconds(sec));

    [Fact]
    public void Null_until_two_boards_seen()
    {
        var t = new BoardRateTracker();
        Feed(t, "R1ST", 0);
        Assert.Null(t.BoardsPerHour);
        Feed(t, "R0CT", 30);
        Assert.Null(t.BoardsPerHour);   // one board isn't enough for a rate
    }

    [Fact]
    public void Steady_run_gives_expected_rate()
    {
        // A board every 60 s of running -> 60 boards/hour.
        var t = new BoardRateTracker();
        Feed(t, "R1ST", 0);
        for (int i = 1; i <= 5; i++) Feed(t, "R0CT", i * 60);
        Assert.Equal(60.0, t.BoardsPerHour!.Value, 1);
    }

    [Fact]
    public void Stopped_time_is_excluded_from_the_rate()
    {
        // Two boards 60 s apart in RUNNING time, but with a 10-minute stop in the middle.
        // Wall-clock would say ~11 min/board (~5/h); productive time says 60 s/board (~60/h).
        var t = new BoardRateTracker();
        Feed(t, "R1ST", 0);
        Feed(t, "R0CT", 30);       // board 1 at 30s productive
        Feed(t, "R1SP", 40);       // stop
        Feed(t, "R1ST", 40 + 600); // resume 10 min later
        Feed(t, "R0CT", 40 + 600 + 20); // board 2 -> 30s + (10 running after resume... )

        // productive gap between the two boards = (40-30) running before stop + 20 after resume = 30s
        // 1 interval / 30s * 3600 = 120 boards/hour
        Assert.Equal(120.0, t.BoardsPerHour!.Value, 1);
    }

    [Fact]
    public void Overnight_gap_does_not_deflate_the_rate()
    {
        // Mirrors the real 10.6h silent gap: boards before the stop, line off all night,
        // boards after resume. The rate must reflect running cadence, not the idle 10 hours.
        var t = new BoardRateTracker();
        Feed(t, "R1ST", 0);
        Feed(t, "R0CT", 60);
        Feed(t, "R0CT", 120);          // 60s/board while running
        Feed(t, "R1SP", 130);          // line stops for the night
        double night = 10.6 * 3600;
        Feed(t, "R1ST", 130 + night);  // morning restart
        Feed(t, "R0CT", 130 + night + 60);
        Feed(t, "R0CT", 130 + night + 120);

        // Still ~60 boards/hour despite the 10.6h gap.
        Assert.InRange(t.BoardsPerHour!.Value, 55, 65);
    }

    [Fact]
    public void Window_keeps_only_recent_boards()
    {
        // With a small window, an early slow patch should fall out and not drag the rate down.
        var t = new BoardRateTracker(windowSize: 3);
        Feed(t, "R1ST", 0);
        Feed(t, "R0CT", 300);   // slow first board (will be evicted)
        Feed(t, "R0CT", 360);   // then 60s cadence
        Feed(t, "R0CT", 420);
        Feed(t, "R0CT", 480);
        Assert.Equal(60.0, t.BoardsPerHour!.Value, 1);
    }
}
