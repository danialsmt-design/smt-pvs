namespace Pvs.Core.Runtime;

/// <summary>One parts-exhaust → recovery span for a cell. <see cref="End"/> is null while the cell is still down.</summary>
public sealed record CellRecovery(int Cell, int Feeder, string? Part, DateTime Start, DateTime? End)
{
    public TimeSpan Duration(DateTime now) => (End ?? now) - Start;
}

/// <summary>
/// The automatic half of downtime capture: times every parts-out to the cell producing again, per cell — the
/// "number of exhausts vs recovery time, per cell" metric. Pure and in-memory. No operator action: PVS already
/// detects parts-outs and board-completes on the wire, so this just clocks them.
///
/// A cell has at most one open recovery at a time (it is stopped waiting for the swap): a parts-out opens it
/// (a second parts-out while one is open is ignored — the cell is already down), and the cell's next
/// board-complete closes it. Recovery = parts-out → produces again (the real line loss, not just the scan).
/// </summary>
public sealed class CellRecoveryLog
{
    private readonly List<CellRecovery> _done = new();
    private readonly Dictionary<int, CellRecovery> _open = new();

    public IReadOnlyList<CellRecovery> Completed => _done;
    public IReadOnlyCollection<CellRecovery> Open => _open.Values;

    /// <summary>A parts-out on <paramref name="cell"/>: start its recovery clock (ignored if one is already open).</summary>
    public void OnPartsOut(int cell, int feeder, string? part, DateTime at)
    {
        if (_open.ContainsKey(cell)) return;   // already waiting for this cell to come back
        _open[cell] = new CellRecovery(cell, feeder, part, at, null);
    }

    /// <summary>The cell produced a board — close its open recovery, if any.</summary>
    public void OnCellProduced(int cell, DateTime at)
    {
        if (!_open.TryGetValue(cell, out var r)) return;
        var end = at < r.Start ? r.Start : at;
        _done.Add(r with { End = end });
        _open.Remove(cell);
    }

    /// <summary>Reset to clean state (e.g. a new-day boundary).</summary>
    public void Clear() { _done.Clear(); _open.Clear(); }

    /// <summary>Drop completed recoveries that ended before <paramref name="before"/> (rolling retention).</summary>
    public int Prune(DateTime before) => _done.RemoveAll(r => r.End is DateTime e && e < before);

    /// <summary>Restore persisted state (host reload).</summary>
    public void Restore(IEnumerable<CellRecovery> done, IEnumerable<CellRecovery> open)
    {
        _done.Clear();
        _done.AddRange(done);
        _open.Clear();
        foreach (var r in open) _open[r.Cell] = r;
    }

    /// <summary>Per-cell rollup of COMPLETED recoveries: exhaust count, total and worst recovery time.</summary>
    public IReadOnlyList<(int Cell, int Count, TimeSpan Total, TimeSpan Max)> ByCell()
    {
        return _done.GroupBy(r => r.Cell).OrderBy(g => g.Key)
            .Select(g =>
            {
                var durs = g.Select(r => (r.End ?? r.Start) - r.Start).ToList();
                return (g.Key, g.Count(),
                    durs.Aggregate(TimeSpan.Zero, (a, d) => a + d),
                    durs.Count > 0 ? durs.Max() : TimeSpan.Zero);
            }).ToList();
    }
}
