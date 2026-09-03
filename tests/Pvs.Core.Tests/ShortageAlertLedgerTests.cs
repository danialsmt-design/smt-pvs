using Pvs.Core.Inventory;

namespace Pvs.Core.Tests;

/// <summary>
/// What is worth SAYING, given what was already said. These transitions decide whether the production manager
/// keeps reading this report or filters the sender — which is the difference between the whole thing working
/// and being worthless.
/// </summary>
public class ShortageAlertLedgerTests
{
    private static readonly DateTime Day1 = new(2026, 8, 7, 8, 0, 0);
    private static readonly ShortageAlertOptions Opt = new(TimeSpan.FromHours(24), MaterialChangePercent: 5);

    private static ShortageFinding Finding(string part, string lot, long shortBy, bool urgent = true) =>
        new(part, lot, "L254", Day1.Date.AddDays(urgent ? 1 : 9), 100, 100 + shortBy, shortBy,
            300, 31, null, urgent ? 1 : 9, urgent);

    private static ShortageForecast Forecast(params ShortageFinding[] findings) =>
        new(Day1, findings,
            findings.Select(f => f.LotNo).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            findings.Length, 0, 0, findings.Length, Array.Empty<string>(), ShortageCoverage.Unknown);

    /// <summary>A forecast where the named lots are still planned but nothing is short.</summary>
    private static ShortageForecast Clear(params string[] activeLots) =>
        new(Day1, Array.Empty<ShortageFinding>(), activeLots, activeLots.Length, 0, 0, 0,
            Array.Empty<string>(), ShortageCoverage.Unknown);

    private static ShortageAlertEntry Entry(string part, string lot, long shortBy, bool urgent, DateTime first, DateTime lastSent) =>
        new(part, lot, shortBy, urgent, first, lastSent);

    [Fact]
    public void A_new_shortage_is_sent()
    {
        var plan = ShortageAlertLedger.Decide(null, Forecast(Finding("P1", "HC1", 7_200)), Day1, Opt);

        var item = Assert.Single(plan.Items);
        Assert.Equal(ShortageChange.New, item.Change);
        Assert.True(plan.ShouldSend);
        var kept = Assert.Single(plan.NextState);
        Assert.Equal(7_200, kept.ShortBy);
        Assert.Equal(Day1, kept.FirstReported);
    }

    [Fact]
    public void An_unchanged_shortage_inside_the_reminder_interval_is_not_sent()
    {
        var state = new[] { Entry("P1", "HC1", 7_200, true, Day1, Day1) };
        var later = Day1.AddHours(6);

        var plan = ShortageAlertLedger.Decide(state, Forecast(Finding("P1", "HC1", 7_200)), later, Opt);

        Assert.False(plan.ShouldSend);
        Assert.Empty(plan.Items);
        Assert.Equal(1, plan.SuppressedQuiet);
        Assert.Equal(Day1, Assert.Single(plan.NextState).LastSent);      // clock not restarted
    }

    [Fact]
    public void An_unchanged_shortage_past_the_reminder_interval_is_sent_with_its_age()
    {
        var state = new[] { Entry("P1", "HC1", 7_200, true, Day1, Day1) };
        var twoDaysOn = Day1.AddDays(2);

        var plan = ShortageAlertLedger.Decide(state, Forecast(Finding("P1", "HC1", 7_200)), twoDaysOn, Opt);

        var item = Assert.Single(plan.Items);
        Assert.Equal(ShortageChange.Reminder, item.Change);
        Assert.Equal(2, item.DaysOutstanding(twoDaysOn));
        Assert.Equal(Day1, item.FirstReported);                          // age is measured from the FIRST report
        Assert.Equal(twoDaysOn, Assert.Single(plan.NextState).LastSent);
    }

    [Fact]
    public void A_materially_worse_shortage_is_sent_immediately()
    {
        var state = new[] { Entry("P1", "HC1", 1_000, true, Day1, Day1) };

        var plan = ShortageAlertLedger.Decide(state, Forecast(Finding("P1", "HC1", 5_000)), Day1.AddHours(1), Opt);

        var item = Assert.Single(plan.Items);
        Assert.Equal(ShortageChange.Worsened, item.Change);
        Assert.Equal(1_000, item.PreviousShortBy);
        Assert.Equal(5_000, item.ShortBy);
    }

    [Fact]
    public void An_improvement_that_is_still_short_is_sent_and_shows_the_movement()
    {
        var state = new[] { Entry("P1", "HC1", 12_000, true, Day1, Day1) };

        var plan = ShortageAlertLedger.Decide(state, Forecast(Finding("P1", "HC1", 3_000)), Day1.AddHours(1), Opt);

        var item = Assert.Single(plan.Items);
        Assert.Equal(ShortageChange.Improved, item.Change);
        Assert.Equal(12_000, item.PreviousShortBy);
        Assert.Equal(3_000, item.ShortBy);
    }

    [Fact]
    public void A_trivial_drift_is_not_worth_an_email()
    {
        // 5% of 10,000 is 500; a 100-piece move stays quiet.
        var state = new[] { Entry("P1", "HC1", 10_000, true, Day1, Day1) };

        var plan = ShortageAlertLedger.Decide(state, Forecast(Finding("P1", "HC1", 10_100)), Day1.AddHours(2), Opt);

        Assert.False(plan.ShouldSend);
        // The baseline stays at the last REPORTED figure, so a slow creep still has to cross it.
        Assert.Equal(10_000, Assert.Single(plan.NextState).ShortBy);
    }

    [Fact]
    public void A_slow_creep_measured_against_the_last_reported_figure_eventually_trips()
    {
        var state = new[] { Entry("P1", "HC1", 10_000, true, Day1, Day1) };

        // Three quiet 200-piece steps, then the cumulative 600 crosses the 500 threshold.
        var quiet = ShortageAlertLedger.Decide(state, Forecast(Finding("P1", "HC1", 10_200)), Day1.AddHours(1), Opt);
        Assert.False(quiet.ShouldSend);
        var still = ShortageAlertLedger.Decide(quiet.NextState, Forecast(Finding("P1", "HC1", 10_400)), Day1.AddHours(2), Opt);
        Assert.False(still.ShouldSend);
        var trips = ShortageAlertLedger.Decide(still.NextState, Forecast(Finding("P1", "HC1", 10_600)), Day1.AddHours(3), Opt);

        Assert.Equal(ShortageChange.Worsened, Assert.Single(trips.Items).Change);
    }

    [Fact]
    public void A_shortage_that_becomes_urgent_is_sent_even_though_the_figure_did_not_move()
    {
        var state = new[] { Entry("P1", "HC1", 7_200, urgent: false, Day1, Day1) };

        var plan = ShortageAlertLedger.Decide(state, Forecast(Finding("P1", "HC1", 7_200, urgent: true)), Day1.AddHours(2), Opt);

        Assert.Equal(ShortageChange.Escalated, Assert.Single(plan.Items).Change);
        Assert.True(Assert.Single(plan.NextState).Urgent);
    }

    [Fact]
    public void A_resolved_shortage_gets_exactly_one_all_clear_and_is_then_forgotten()
    {
        var state = new[] { Entry("P1", "HC1", 7_200, true, Day1, Day1) };

        var plan = ShortageAlertLedger.Decide(state, Clear("HC1"), Day1.AddDays(1), Opt);

        var item = Assert.Single(plan.Items);
        Assert.Equal(ShortageChange.Resolved, item.Change);
        Assert.Equal(7_200, item.PreviousShortBy);
        Assert.Empty(plan.NextState);                                     // dropped, so it can never repeat

        // Run again with the state the first pass produced — silence.
        Assert.False(ShortageAlertLedger.Decide(plan.NextState, Clear("HC1"), Day1.AddDays(2), Opt).ShouldSend);
    }

    [Fact]
    public void A_lot_that_was_built_or_cancelled_is_dropped_silently()
    {
        var state = new[] { Entry("P1", "HC1", 7_200, true, Day1, Day1) };

        // HC1 is no longer among the lots needing boards — built, cancelled or delivered.
        var plan = ShortageAlertLedger.Decide(state, Clear("HC2"), Day1.AddDays(1), Opt);

        Assert.False(plan.ShouldSend);
        Assert.Empty(plan.Items);
        Assert.Empty(plan.NextState);
    }

    [Fact]
    public void A_shortage_that_moves_to_a_later_lot_does_not_produce_a_confusing_all_clear()
    {
        var state = new[] { Entry("P1", "HC1", 7_200, true, Day1, Day1) };
        var forecast = new ShortageForecast(
            Day1, new[] { Finding("P1", "HC2", 3_000) }, new[] { "HC1", "HC2" },
            2, 0, 0, 1, Array.Empty<string>(), ShortageCoverage.Unknown);

        var plan = ShortageAlertLedger.Decide(state, forecast, Day1.AddDays(1), Opt);

        var item = Assert.Single(plan.Items);
        Assert.Equal(ShortageChange.New, item.Change);
        Assert.Equal("HC2", item.LotNo);
        Assert.DoesNotContain(plan.Items, i => i.Change == ShortageChange.Resolved);
    }

    [Fact]
    public void Several_parts_short_on_one_lot_are_all_carried()
    {
        var plan = ShortageAlertLedger.Decide(
            null,
            Forecast(Finding("P1", "HC1", 7_200), Finding("P2", "HC1", 68), Finding("P3", "HC1", 5, urgent: false)),
            Day1, Opt);

        Assert.Equal(3, plan.Items.Count);
        Assert.Equal(2, plan.UrgentItems.Count);
        Assert.Equal(3, plan.NextState.Count);
    }

    [Fact]
    public void State_survives_a_round_trip_so_a_restart_does_not_re_send()
    {
        var first = ShortageAlertLedger.Decide(null, Forecast(Finding("P1", "HC1", 7_200)), Day1, Opt);
        var saved = new ShortageMonitorState { LastRunDate = "2026-08-07", Alerts = first.NextState.ToList() };

        var reloaded = ShortageStateCodec.Read(ShortageStateCodec.Write(saved), out var problem);

        Assert.Null(problem);
        Assert.Equal("2026-08-07", reloaded.LastRunDate);
        var again = ShortageAlertLedger.Decide(reloaded.Alerts, Forecast(Finding("P1", "HC1", 7_200)), Day1.AddHours(3), Opt);
        Assert.False(again.ShouldSend);
    }

    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("[]")]
    [InlineData("\"a string\"")]
    [InlineData("{\"alerts\": \"not an array\"}")]
    public void A_corrupt_state_file_starts_fresh_instead_of_stopping_the_monitor(string junk)
    {
        var state = ShortageStateCodec.Read(junk, out var problem);

        Assert.NotNull(problem);
        Assert.Empty(state.Alerts);
        Assert.Equal("", state.LastRunDate);

        // And the monitor keeps working off the fresh state: the shortage is simply reported as new.
        var plan = ShortageAlertLedger.Decide(state.Alerts, Forecast(Finding("P1", "HC1", 7_200)), Day1, Opt);
        Assert.Equal(ShortageChange.New, Assert.Single(plan.Items).Change);
    }

    [Fact]
    public void A_missing_or_empty_state_file_starts_fresh_without_complaining()
    {
        foreach (var text in new[] { null, "", "   " })
        {
            var state = ShortageStateCodec.Read(text, out var problem);
            Assert.Null(problem);
            Assert.Empty(state.Alerts);
        }
    }

    [Fact]
    public void Half_written_state_entries_are_discarded_rather_than_poisoning_the_comparison()
    {
        var state = ShortageStateCodec.Read(
            "{\"alerts\":[{\"partNumber\":\"\",\"lotNo\":\"HC1\",\"shortBy\":5}," +
            "{\"partNumber\":\"P1\",\"lotNo\":\"\",\"shortBy\":5}," +
            "{\"partNumber\":\"P2\",\"lotNo\":\"HC1\",\"shortBy\":5,\"urgent\":true," +
            "\"firstReported\":\"2026-08-07T08:00:00\",\"lastSent\":\"2026-08-07T08:00:00\"}]}",
            out var problem);

        Assert.Null(problem);
        Assert.Equal("P2", Assert.Single(state.Alerts).PartNumber);
    }
}
