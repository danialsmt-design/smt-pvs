using Pvs.Core.Requests;

namespace Pvs.Core.Tests;

public class PartsRequestPlannerTests
{
    private static ExhaustRow Row(string part, double? minutes, bool needs, bool spare = false, bool lasts = false,
        int machine = 1, int feeder = 101, int remaining = 500, int issued = 1000, int? needed = 4000) =>
        new(machine, feeder, part, remaining, minutes, needs, spare, lasts, issued, needed);

    [Fact]
    public void Opens_a_request_for_a_feeder_inside_the_threshold_that_needs_material()
    {
        var plan = PartsRequestPlanner.Plan(new[] { Row("VE4-2850-104", 30, needs: true) }, Array.Empty<string>(), 45);
        var r = Assert.Single(plan.Open);
        Assert.Equal("VE4-2850-104", r.Part); Assert.Equal(30, r.MinutesLeft);
        Assert.Equal(3000, r.PiecesNeeded);          // needed for the lot - already issued
        Assert.Empty(plan.Close);
    }

    [Fact]
    public void Does_not_open_when_too_early_or_unknown_rate_or_spare_staged()
    {
        var rows = new[]
        {
            Row("A", 120, needs: true),                  // outside the threshold
            Row("B", null, needs: true),                 // rate unknown -> no run-out time
            Row("C", 10, needs: false, spare: true),     // spare already at the line
        };
        var plan = PartsRequestPlanner.Plan(rows, Array.Empty<string>(), 45);
        Assert.Empty(plan.Open);
    }

    [Fact]
    public void One_request_per_part_even_across_feeders_and_not_twice_while_open()
    {
        var rows = new[] { Row("A", 20, true, feeder: 101), Row("A", 35, true, feeder: 205, machine: 2) };
        var plan = PartsRequestPlanner.Plan(rows, Array.Empty<string>(), 45);
        var r = Assert.Single(plan.Open);
        Assert.Equal(101, r.Feeder);                   // the soonest feeder names the request
        var again = PartsRequestPlanner.Plan(rows, new[] { "A" }, 45);
        Assert.Empty(again.Open); Assert.Empty(again.Close);
    }

    [Fact]
    public void Closes_when_the_part_is_covered_or_gone()
    {
        var plan = PartsRequestPlanner.Plan(
            new[] { Row("A", 400, needs: false, lasts: true), Row("B", 5, needs: false, spare: true) },
            new[] { "A", "B", "C" }, 45);
        Assert.Empty(plan.Open);
        Assert.Equal(3, plan.Close.Count);
        Assert.Contains(plan.Close, c => c.Part == "A" && c.Reason.Contains("lasts"));
        Assert.Contains(plan.Close, c => c.Part == "B" && c.Reason.Contains("spare"));
        Assert.Contains(plan.Close, c => c.Part == "C" && c.Reason.Contains("no longer"));
    }

    [Fact]
    public void Pieces_needed_never_negative_and_zero_when_lot_need_unknown()
    {
        var over = PartsRequestPlanner.Plan(new[] { Row("A", 10, true, issued: 9000, needed: 4000) }, Array.Empty<string>(), 45);
        Assert.Equal(0, over.Open.Single().PiecesNeeded);
        var unknown = PartsRequestPlanner.Plan(new[] { Row("A", 10, true, needed: null) }, Array.Empty<string>(), 45);
        Assert.Equal(0, unknown.Open.Single().PiecesNeeded);
    }
}
