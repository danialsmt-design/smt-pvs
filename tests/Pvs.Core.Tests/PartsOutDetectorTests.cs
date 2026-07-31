using Pvs.Core.Events;
using Pvs.Core.Serial;
using Xunit;

namespace Pvs.Core.Tests;

public class PartsOutDetectorTests
{
    private static readonly DateTime T0 = new(2026, 7, 22, 19, 27, 27);

    // Feed a payload at an offset (seconds) from T0; return any event raised.
    private static PartsOutEvent? Feed(PartsOutDetector d, string payload, double sec)
        => d.Observe(SonyMessage.Parse(payload), T0.AddSeconds(sec));

    [Fact]
    public void Real_burst_raises_exactly_one_event_on_the_E03()
    {
        // The actual Cell-2 burst from the capture: E02 -> E02 -> E04 -> E03, feeder 124.
        var d = new PartsOutDetector(machine: 2);

        Assert.Null(Feed(d, "R2E02S000N0080Z124M000T000", 0.0)); // pickup error - no alert
        Assert.Null(Feed(d, "R2E02S000N0080Z124M000T000", 2.0)); // again - no alert
        Assert.Null(Feed(d, "R2E04S000N0080Z124M000T000", 2.1)); // alternation - no alert

        var ev = Feed(d, "R2E03S000N0080Z124M000T000", 2.2);     // genuine parts-out - ALERT
        Assert.NotNull(ev);
        Assert.Equal(2, ev!.Value.Machine);
        Assert.Equal(124, ev.Value.Feeder);
        Assert.Equal(80, ev.Value.Step);
    }

    [Fact]
    public void Pickup_error_jam_never_alerts()
    {
        // Tray feeder 504 looped E02/E02/E08 ~10x and NEVER reached E03 -> zero prompts.
        var d = new PartsOutDetector(machine: 3);
        for (int i = 0; i < 10; i++)
        {
            Assert.Null(Feed(d, "R2E02S000N0001Z504M000T000", i * 10.0));
            Assert.Null(Feed(d, "R2E02S000N0001Z504M000T000", i * 10.0 + 3));
            Assert.Null(Feed(d, "R2E08S000N0001Z000M000T000", i * 10.0 + 3.1));
        }
    }

    [Fact]
    public void Same_feeder_exhausting_again_later_is_a_distinct_event()
    {
        // Feeder 123 genuinely exhausted 5x across the shift, minutes apart.
        var d = new PartsOutDetector(machine: 2);

        var first = Feed(d, "R2E03S000N0033Z123M000T000", 0);
        var second = Feed(d, "R2E03S000N0026Z123M000T000", 13 * 60);  // ~13 min later (20:12 -> 20:25)

        Assert.NotNull(first);
        Assert.NotNull(second);
    }

    [Fact]
    public void Duplicate_E03_inside_the_window_is_suppressed()
    {
        var d = new PartsOutDetector(machine: 2);
        Assert.NotNull(Feed(d, "R2E03S000N0080Z124M000T000", 0));
        Assert.Null(Feed(d, "R2E03S000N0080Z124M000T000", 3));   // within 5s -> same exhaustion
    }

    [Fact]
    public void Two_machines_are_independent()
    {
        // A cross-machine detector must not confuse feeder 124 on different machines.
        var d2 = new PartsOutDetector(machine: 2);
        var d3 = new PartsOutDetector(machine: 3);

        var a = d2.Observe(SonyMessage.Parse("R2E03S000N0080Z124M000T000"), T0);
        var b = d3.Observe(SonyMessage.Parse("R2E03S000N0080Z124M000T000"), T0);

        Assert.Equal(2, a!.Value.Machine);
        Assert.Equal(3, b!.Value.Machine);
    }
}
