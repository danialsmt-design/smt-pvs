using System.Text.Json;

namespace Pvs.LineApp.Inventory;

/// <summary>The reel remembered on one feeder.</summary>
public sealed record FeederReel(int Machine, int Feeder, string Part, string Uid, DateTime At);

/// <summary>
/// Remembers which reel (UID + part) is on each (machine, feeder) so the machine-inventory view can
/// show quantities without re-scanning. Updated from every verified reel scan, persisted to a JSON
/// file next to the app so it survives restarts. Quantity itself is NOT stored here — it is read live
/// from StockOuts by UID — so this only holds the feeder -> reel mapping.
/// </summary>
public sealed class FeederReelStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private readonly Dictionary<(int machine, int feeder), FeederReel> _map = new();

    public FeederReelStore(string path)
    {
        _path = path;
        Load();
    }

    /// <summary>Record (or update) the reel now on a feeder.</summary>
    public void Set(int machine, int feeder, string part, string uid)
    {
        if (string.IsNullOrWhiteSpace(uid)) return;
        lock (_gate)
        {
            _map[(machine, feeder)] = new FeederReel(machine, feeder, (part ?? "").Trim(), uid.Trim(), DateTime.Now);
            Save();
        }
    }

    /// <summary>The reel remembered on a feeder, or null.</summary>
    public FeederReel? Get(int machine, int feeder)
    {
        lock (_gate) { return _map.TryGetValue((machine, feeder), out var r) ? r : null; }
    }

    /// <summary>All remembered reels on a machine.</summary>
    public IReadOnlyList<FeederReel> ForMachine(int machine)
    {
        lock (_gate) { return _map.Values.Where(r => r.Machine == machine).ToList(); }
    }

    /// <summary>Every reel currently remembered on any feeder (for the "issued but not loaded" staged view).</summary>
    public IReadOnlyList<FeederReel> All()
    {
        lock (_gate) { return _map.Values.ToList(); }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var list = JsonSerializer.Deserialize<List<FeederReel>>(File.ReadAllText(_path));
            if (list is null) return;
            lock (_gate)
                foreach (var r in list) _map[(r.Machine, r.Feeder)] = r;
        }
        catch { /* corrupt/empty file -> start clean */ }
    }

    private void Save()
    {
        try { File.WriteAllText(_path, JsonSerializer.Serialize(_map.Values.ToList())); }
        catch { /* best-effort persistence */ }
    }
}
