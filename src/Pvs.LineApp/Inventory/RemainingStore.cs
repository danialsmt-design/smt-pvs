using System.Text.Json;

namespace Pvs.LineApp.Inventory;

/// <summary>One feeder's recorded remaining (the local record on the line PC).</summary>
/// <param name="BoardsRun">Boards this reel had been on the machine for when the record was written (its anchor), so
/// a restart can restore how much of the run the reel really saw. 0 = unknown / loaded just now.</param>
public sealed record RemainingEntry(int Machine, int Feeder, string Part, string Uid, int Remaining, DateTime At, int BoardsRun = 0);

/// <summary>
/// The line-local record of each feeder's remaining piece-count, persisted to remaining.json. This is the
/// authoritative source of "current remaining" now that the parts-control StockOuts write-back is off:
/// the exhaust forecast baselines from here (matched by reel UID), and supervisor corrections are recorded
/// here. StockOuts is only the issued-quantity reference for a brand-new reel not yet recorded locally.
/// </summary>
public sealed class RemainingStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,   // tolerate the older lowercase-keyed file
        WriteIndented = true
    };

    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<(int machine, int feeder), RemainingEntry> _map = new();

    public RemainingStore(string path)
    {
        _path = path;
        Load();
    }

    /// <summary>Replace the whole record with the current snapshot (one entry per feeder).</summary>
    public void Save(IEnumerable<RemainingEntry> entries)
    {
        lock (_gate)
        {
            _map = entries.ToDictionary(e => (e.Machine, e.Feeder));
            Write();
        }
    }

    /// <summary>Set/replace one feeder's recorded remaining (e.g. a supervisor correction).</summary>
    public void Set(RemainingEntry entry)
    {
        lock (_gate)
        {
            _map[(entry.Machine, entry.Feeder)] = entry;
            Write();
        }
    }

    public RemainingEntry? Get(int machine, int feeder)
    {
        lock (_gate) { return _map.TryGetValue((machine, feeder), out var e) ? e : null; }
    }

    private void Load()
    {
        try
        {
            var list = Pvs.Core.Persistence.AtomicFile.Load(_path, t => JsonSerializer.Deserialize<List<RemainingEntry>>(t, JsonOpts));
            if (list is null) return;
            lock (_gate)
                foreach (var e in list) _map[(e.Machine, e.Feeder)] = e;
        }
        catch { /* corrupt/empty -> start clean */ }
    }

    private void Write()
    {
        try { Pvs.Core.Persistence.AtomicFile.Write(_path, JsonSerializer.Serialize(_map.Values.ToList(), JsonOpts)); }
        catch { /* best-effort */ }
    }
}
