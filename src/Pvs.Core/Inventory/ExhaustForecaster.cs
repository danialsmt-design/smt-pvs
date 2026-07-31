namespace Pvs.Core.Inventory;

/// <summary>A single feeder's contribution to the forecast (a snapshot across all machines).</summary>
public readonly record struct FeederConsumption(
    int Machine, int Feeder, string PartNumber, int MountedPerBoard, int Remaining);

/// <summary>Forecast for one part number, combined across every feeder/machine carrying it.</summary>
public readonly record struct PartForecast(
    string PartNumber,
    int Remaining,                                   // total pieces across all its feeders
    double PiecesPerHour,                            // combined consumption rate
    double? HoursToExhaust,                          // null when not currently consuming (rate 0)
    IReadOnlyList<(int Machine, int Feeder)> Locations);

/// <summary>
/// Builds the "what runs out next" forecast shown on the line's main monitor during production.
///
/// Per the agreed model:
///  - A feeder's consumption rate = mounted-per-board x that machine's boards/hour.
///  - The same part number on several feeders/machines is COMBINED into one row: sum the remaining
///    pieces and sum the consumption rates, then time-to-exhaust = combined remaining / combined rate.
///  - Ranked soonest-first; the monitor shows the top N (default 10).
///  - A part on machines with no known rate yet contributes 0 to the rate; if a part's total rate
///    is 0 it has no finite time-to-exhaust and sorts last.
/// </summary>
public static class ExhaustForecaster
{
    public static IReadOnlyList<PartForecast> Rank(
        IEnumerable<FeederConsumption> feeders,
        Func<int, double?> boardsPerHourByMachine,
        int topN = 10)
    {
        var byPart = feeders.GroupBy(f => f.PartNumber);
        var results = new List<PartForecast>();

        foreach (var group in byPart)
        {
            int remaining = 0;
            double perHour = 0;
            var locations = new List<(int, int)>();

            foreach (var f in group)
            {
                remaining += f.Remaining;
                locations.Add((f.Machine, f.Feeder));

                double? rate = boardsPerHourByMachine(f.Machine);
                if (rate is double bph && bph > 0)
                    perHour += f.MountedPerBoard * bph;
            }

            double? hours = perHour > 0 ? remaining / perHour : null;
            locations.Sort();

            results.Add(new PartForecast(group.Key, remaining, perHour, hours, locations));
        }

        // Soonest-to-exhaust first; parts with no finite forecast (rate 0) go to the bottom.
        return results
            .OrderBy(r => r.HoursToExhaust ?? double.PositiveInfinity)
            .ThenBy(r => r.PartNumber)
            .Take(topN)
            .ToList();
    }
}
