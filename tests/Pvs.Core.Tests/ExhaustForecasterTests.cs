using Pvs.Core.Inventory;
using Xunit;

namespace Pvs.Core.Tests;

public class ExhaustForecasterTests
{
    // Constant rate for every machine unless a test says otherwise.
    private static Func<int, double?> Rate(double perHour) => _ => perHour;

    [Fact]
    public void Time_to_exhaust_is_remaining_over_rate()
    {
        // 1 feeder: 3600 left, 10 placements/board, 60 boards/h -> 600 pieces/h -> 6 hours.
        var feeders = new[] { new FeederConsumption(2, 108, "PARTX", MountedPerBoard: 10, Remaining: 3600) };
        var top = ExhaustForecaster.Rank(feeders, Rate(60));

        Assert.Single(top);
        Assert.Equal(600, top[0].PiecesPerHour, 1);
        Assert.Equal(6.0, top[0].HoursToExhaust!.Value, 2);
    }

    [Fact]
    public void Same_part_on_two_machines_is_combined_into_one_row()
    {
        // PARTX on machine 2 (3600, 10/board) and machine 4 (400, 2/board), both at 60 boards/h.
        // Combined remaining 4000; combined rate = 10*60 + 2*60 = 720/h -> ~5.56 h.
        var feeders = new[]
        {
            new FeederConsumption(2, 108, "PARTX", 10, 3600),
            new FeederConsumption(4, 118, "PARTX", 2, 400),
        };
        var top = ExhaustForecaster.Rank(feeders, Rate(60));

        Assert.Single(top);
        Assert.Equal(4000, top[0].Remaining);
        Assert.Equal(720, top[0].PiecesPerHour, 1);
        Assert.Equal(4000.0 / 720.0, top[0].HoursToExhaust!.Value, 2);
        Assert.Equal(new[] { (2, 108), (4, 118) }, top[0].Locations);
    }

    [Fact]
    public void Ranked_soonest_first_and_limited_to_topN()
    {
        var feeders = new[]
        {
            new FeederConsumption(2, 1, "SLOW", 1, 10000),   // lasts ages
            new FeederConsumption(2, 2, "SOON", 20, 600),    // runs out first
            new FeederConsumption(2, 3, "MID",  5, 3000),
        };
        var top = ExhaustForecaster.Rank(feeders, Rate(60), topN: 2);

        Assert.Equal(2, top.Count);
        Assert.Equal("SOON", top[0].PartNumber);
        Assert.Equal("MID", top[1].PartNumber);   // SLOW is dropped by topN
    }

    [Fact]
    public void Part_on_idle_machine_has_no_finite_forecast_and_sorts_last()
    {
        // Machine 3 has no rate yet (null). Its part can't be forecast; a running part outranks it.
        Func<int, double?> rate = m => m == 2 ? 60.0 : (double?)null;
        var feeders = new[]
        {
            new FeederConsumption(3, 130, "IDLEPART", 5, 500),   // machine 3: no rate
            new FeederConsumption(2, 108, "RUNPART", 5, 5000),   // machine 2: running
        };
        var top = ExhaustForecaster.Rank(feeders, rate);

        Assert.Equal("RUNPART", top[0].PartNumber);
        Assert.NotNull(top[0].HoursToExhaust);
        Assert.Equal("IDLEPART", top[1].PartNumber);
        Assert.Null(top[1].HoursToExhaust);            // rate 0 -> no forecast
    }
}
