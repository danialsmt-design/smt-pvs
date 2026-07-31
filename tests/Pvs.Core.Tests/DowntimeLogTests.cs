using System;
using System.Linq;
using Pvs.Core.Runtime;
using Xunit;

namespace Pvs.Core.Tests;

public class DowntimeLogTests
{
    private static DateTime T(int h, int m) => new(2026, 7, 27, h, m, 0);

    [Fact]
    public void Boot_idle_before_first_online_is_not_counted()
    {
        var log = new DowntimeLog();
        log.Sample(false, T(7, 0));    // line not started yet
        log.Sample(false, T(7, 20));
        log.Sample(true, T(7, 30));    // first online
        Assert.Equal(TimeSpan.Zero, log.TotalDown(T(8, 0)));
        Assert.Empty(log.ProductionSpans);
    }

    [Fact]
    public void A_stop_during_production_is_counted()
    {
        var log = new DowntimeLog();
        log.Sample(true, T(7, 30));    // up
        log.Sample(false, T(10, 0));   // stop
        log.Sample(true, T(10, 25));   // resume
        var spans = log.ProductionSpans;
        Assert.Single(spans);
        Assert.Equal(TimeSpan.FromMinutes(25), log.TotalDown(T(12, 0)));
    }

    [Fact]
    public void An_open_stop_is_counted_up_to_now()
    {
        var log = new DowntimeLog();
        log.Sample(true, T(7, 30));
        log.Sample(false, T(11, 0));   // still down
        Assert.Equal(TimeSpan.FromMinutes(30), log.TotalDown(T(11, 30)));
        Assert.Null(log.ProductionSpans.Single().End);
    }

    [Fact]
    public void Multiple_stops_sum()
    {
        var log = new DowntimeLog();
        log.Sample(true, T(7, 30));
        log.Sample(false, T(9, 0)); log.Sample(true, T(9, 10));   // 10 min
        log.Sample(false, T(14, 0)); log.Sample(true, T(14, 5));  // 5 min
        Assert.Equal(TimeSpan.FromMinutes(15), log.TotalDown(T(15, 0)));
        Assert.Equal(2, log.ProductionSpans.Count);
    }

    [Fact]
    public void Restore_round_trips_state()
    {
        var log = new DowntimeLog();
        log.Sample(true, T(7, 30));
        log.Sample(false, T(9, 0));
        var restored = new DowntimeLog();
        restored.Restore(log.Spans, log.FirstUp, log.IsUp, log.Seeded);
        restored.Sample(true, T(9, 20));
        Assert.Equal(TimeSpan.FromMinutes(20), restored.TotalDown(T(10, 0)));
    }
}
