using System.Text.Json;
using Pvs.Core.Boards;

namespace Pvs.LineApp.Runtime;

/// <summary>
/// Host wrapper around <see cref="BoardInputLog"/> — the boards staged at a line's input for the current lot.
/// First-side = bare-board PACKS (scan UID → part+qty from StockOuts); second-side = MAGAZINES (scan the MCS
/// slip QR → pcs = QTY÷N). Count-only, no interlock. Bound to the running lot: a lot change resets the tally.
/// Persisted to <c>boardinput.json</c> so a restart mid-lot keeps the count.
/// </summary>
public sealed class BoardInputState
{
    private readonly object _gate = new();
    private readonly BoardInputLog _log = new();
    private string _lot = "";
    private readonly string _path = Path.Combine(AppContext.BaseDirectory, "boardinput.json");

    public BoardInputState() => Load();

    /// <summary>Bind to the running lot; a change resets the staged input (a new lot stages fresh).</summary>
    public void EnsureLot(string? lot)
    {
        var l = (lot ?? "").Trim();
        lock (_gate)
        {
            if (l == _lot) return;
            _log.Reset();
            _lot = l;
            Save();
        }
    }

    public (bool Added, string Message) AddMagazine(MagazineSlip s)
    {
        lock (_gate)
        {
            bool ok = _log.Add(new BoardToken("magazine", s.Key, $"MAG {s.MagNo}/{s.MagTotal}", s.PerMagazine, DateTime.Now), s.MagTotal);
            if (ok) Save();
            return (ok, ok ? $"MAG {s.MagNo}/{s.MagTotal} · +{s.PerMagazine} boards" : $"MAG {s.MagNo} already scanned");
        }
    }

    public (bool Added, string Message) AddPack(string uid, string part, int pcs)
    {
        lock (_gate)
        {
            bool ok = _log.Add(new BoardToken("pack", uid.Trim(), part, pcs, DateTime.Now));
            if (ok) Save();
            return (ok, ok ? $"{part} · +{pcs} boards" : "pack already scanned");
        }
    }

    public void Reset() { lock (_gate) { _log.Reset(); Save(); } }

    /// <summary>Live tally for the operator screen. <paramref name="producing"/> comes from the stop tracker so
    /// staged boards and whether the line is actually consuming them show together.</summary>
    public object State(bool producing)
    {
        lock (_gate)
        {
            return new
            {
                lot = _lot,
                totalPcs = _log.TotalPcs,
                count = _log.Count,
                magazinesScanned = _log.MagazinesScanned,
                expectedMagazines = _log.ExpectedMagazines,
                producing,
                tokens = _log.Tokens.OrderByDescending(t => t.At)
                    .Select(t => new { kind = t.Kind, key = t.Key, label = t.Label, pcs = t.Pcs, at = t.At })
                    .ToList()
            };
        }
    }

    private sealed record Persisted(string Lot, List<BoardToken> Tokens, int ExpectedMagazines);

    private void Save()
    {
        try { File.WriteAllText(_path, JsonSerializer.Serialize(new Persisted(_lot, _log.Tokens.ToList(), _log.ExpectedMagazines))); }
        catch { /* best-effort */ }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var p = JsonSerializer.Deserialize<Persisted>(File.ReadAllText(_path));
            if (p is null) return;
            _lot = p.Lot ?? "";
            _log.Restore(p.Tokens ?? new(), p.ExpectedMagazines);
        }
        catch { /* corrupt -> start clean */ }
    }
}
