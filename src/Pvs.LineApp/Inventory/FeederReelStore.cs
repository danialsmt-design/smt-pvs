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

    /// <summary>Record the reel now on a feeder AND forget the same UID anywhere else: a reel is physically on ONE
    /// feeder, so a UID that was remembered on another feeder (a moved reel, or an earlier mis-scan) is a stale
    /// mapping. Returns the mappings that were displaced (so the caller can stop tracking them live).</summary>
    public IReadOnlyList<FeederReel> SetUnique(int machine, int feeder, string part, string uid)
    {
        if (string.IsNullOrWhiteSpace(uid)) return Array.Empty<FeederReel>();
        var u = uid.Trim();
        lock (_gate)
        {
            var displaced = _map.Values
                .Where(r => (r.Machine != machine || r.Feeder != feeder) && string.Equals(r.Uid, u, StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var d in displaced) _map.Remove((d.Machine, d.Feeder));
            _map[(machine, feeder)] = new FeederReel(machine, feeder, (part ?? "").Trim(), u, DateTime.Now);
            Save();
            return displaced;
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

    /// <summary>Sibling file holding the last unload snapshot, so a whole-line unload can be reversed.</summary>
    private string BackupPath => _path + ".unload-backup.json";

    /// <summary>
    /// Whole-line UNLOAD (month-end return-to-store / new-model changeover): snapshot every feeder→reel mapping,
    /// save it to a sibling backup so the unload can be reversed, then clear the map so no feeder is loaded.
    /// Quantity is NOT stored or changed here — this only clears the feeder→reel MAPPING; each reel keeps its
    /// StockOut count by UID. Returns the reels that were on feeders (the return manifest).
    /// </summary>
    public IReadOnlyList<FeederReel> ClearAll()
    {
        lock (_gate)
        {
            var snapshot = _map.Values.OrderBy(r => r.Machine).ThenBy(r => r.Feeder).ToList();
            try { File.WriteAllText(BackupPath, JsonSerializer.Serialize(snapshot)); } catch { /* best-effort undo backup */ }
            _map.Clear();
            Save();
            return snapshot;
        }
    }

    /// <summary>Sibling file holding the reels taken off by the last AUTOMATIC off-list unload (for reference / manual redo).</summary>
    private string AutoUnloadBackupPath => _path + ".auto-unload-backup.json";

    /// <summary>
    /// AUTOMATIC off-list unload: take the given feeders' reels off the mapping (they are not on the selected
    /// model's feeder list, so the machine is not picking from them). Snapshot the removed reels to a sibling
    /// backup, then remove ONLY those keys — everything on the feeder list stays loaded. Quantity is NOT stored
    /// or changed here; each reel keeps its remaining by UID. Returns the reels removed (may be empty).
    /// </summary>
    public IReadOnlyList<FeederReel> RemoveOffList(IEnumerable<(int Machine, int Feeder)> keys)
    {
        lock (_gate)
        {
            var removed = new List<FeederReel>();
            foreach (var k in keys)
                if (_map.Remove((k.Machine, k.Feeder), out var r)) removed.Add(r);
            if (removed.Count == 0) return removed;
            removed = removed.OrderBy(r => r.Machine).ThenBy(r => r.Feeder).ToList();
            try { File.WriteAllText(AutoUnloadBackupPath, JsonSerializer.Serialize(removed)); } catch { /* best-effort reference copy */ }
            Save();
            return removed;
        }
    }

    /// <summary>Reverse the last unload: re-load the feeder→reel mapping from the saved backup. Returns the reels
    /// restored (empty when there is no backup to restore from).</summary>
    public IReadOnlyList<FeederReel> RestoreLastUnload()
    {
        lock (_gate)
        {
            List<FeederReel>? list = null;
            try { if (File.Exists(BackupPath)) list = JsonSerializer.Deserialize<List<FeederReel>>(File.ReadAllText(BackupPath)); }
            catch { list = null; }
            if (list is null || list.Count == 0) return Array.Empty<FeederReel>();
            foreach (var r in list)
                if (!string.IsNullOrWhiteSpace(r.Uid)) _map[(r.Machine, r.Feeder)] = r;
            Save();
            return list;
        }
    }

    private void Load()
    {
        try
        {
            
            var list = Pvs.Core.Persistence.AtomicFile.Load(_path, t => JsonSerializer.Deserialize<List<FeederReel>>(t));
            if (list is null) return;
            lock (_gate)
                foreach (var r in list) _map[(r.Machine, r.Feeder)] = r;
        }
        catch { /* corrupt/empty file -> start clean */ }
    }

    private void Save()
    {
        try { Pvs.Core.Persistence.AtomicFile.Write(_path, JsonSerializer.Serialize(_map.Values.ToList())); }
        catch { /* best-effort persistence */ }
    }
}
