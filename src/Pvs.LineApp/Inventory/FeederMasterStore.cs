using System.Text.Json;
using Pvs.Core.Feeders;

namespace Pvs.LineApp.Inventory;

/// <summary>
/// Persists the line's Feeder Master (one block per model + side) in feeder-master.json next to the app, atomically,
/// and appends every superseded block to feeder-master-history.jsonl so a bad edit can be put back.
/// </summary>
public sealed class FeederMasterStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private readonly string _path;
    private readonly string _historyPath;
    private readonly Dictionary<string, MasterBlock> _blocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public FeederMasterStore(string path)
    {
        _path = path;
        _historyPath = System.IO.Path.ChangeExtension(path, null) + "-history.jsonl";
        Load();
    }

    public MasterBlock? Get(string model, string side)
    {
        lock (_gate) return _blocks.TryGetValue(MasterBlock.KeyOf(model, side), out var b) ? b : null;
    }

    public IReadOnlyList<MasterBlock> All() { lock (_gate) return _blocks.Values.OrderBy(b => b.Model).ThenBy(b => b.Side).ToList(); }

    /// <summary>Save a block (new version); the previous version, if any, goes to the history file.</summary>
    public MasterBlock Upsert(MasterBlock block, string by, string source, bool reviewed)
    {
        lock (_gate)
        {
            var key = block.Key;
            int version = _blocks.TryGetValue(key, out var prev) ? prev.Version + 1 : 1;
            var saved = block with { Source = source, Reviewed = reviewed, UpdatedAt = DateTime.Now, UpdatedBy = by, Version = version,
                                     Side = block.Side.ToUpperInvariant(), Model = block.Model.Trim() };
            if (prev is not null)
            {
                try { File.AppendAllText(_historyPath, JsonSerializer.Serialize(prev) + Environment.NewLine); } catch { /* best effort */ }
            }
            _blocks[key] = saved;
            Write();
            return saved;
        }
    }

    private void Write()
    {
        var list = _blocks.Values.OrderBy(b => b.Model).ThenBy(b => b.Side).ToList();
        Pvs.Core.Persistence.AtomicFile.Write(_path, JsonSerializer.Serialize(list, JsonOpts));
    }

    private void Load()
    {
        try
        {
            var list = Pvs.Core.Persistence.AtomicFile.Load(_path, t => JsonSerializer.Deserialize<List<MasterBlock>>(t));
            if (list is null) return;
            foreach (var b in list) if (b is not null && !string.IsNullOrWhiteSpace(b.Model)) _blocks[b.Key] = b;
        }
        catch { /* corrupt file: start empty; the next apply re-imports the current list */ }
    }
}
