using Pvs.Core.Inventory;

namespace Pvs.Core.Runtime;

/// <summary>
/// Aggregates the whole line's machines into the combined exhaust forecast shown on the monitor.
/// It reads a live snapshot of every tracked feeder across the machines and ranks the parts that
/// will run out soonest, combining the same part number across machines (per the agreed model).
/// </summary>
public sealed class LineMonitor
{
    private readonly IReadOnlyDictionary<int, MachineChannel> _channels;

    public LineMonitor(IEnumerable<MachineChannel> channels)
    {
        _channels = channels?.ToDictionary(c => c.Machine)
            ?? throw new ArgumentNullException(nameof(channels));
    }

    public IReadOnlyCollection<MachineChannel> Channels => (IReadOnlyCollection<MachineChannel>)_channels.Values;

    /// <summary>
    /// The top-N components closest to running out, combined across all machines. Uses each machine's
    /// current productive board rate; a machine with no rate yet contributes no consumption.
    /// </summary>
    public IReadOnlyList<PartForecast> Forecast(int topN = 10)
    {
        var consumption = new List<FeederConsumption>();
        foreach (var ch in _channels.Values)
        {
            foreach (var f in ch.Inventory.Feeders)
            {
                if (!f.IsTracked) continue;
                consumption.Add(new FeederConsumption(
                    ch.Machine, f.Feeder, f.PartNumber, f.MountedPerBoard, f.Remaining));
            }
        }

        return ExhaustForecaster.Rank(
            consumption,
            machine => _channels.TryGetValue(machine, out var ch) ? ch.BoardRate.BoardsPerHour : null,
            topN);
    }
}
