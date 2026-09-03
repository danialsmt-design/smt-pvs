namespace Pvs.Core.Boards;

/// <summary>One scanned board-input token — a first-side bare-board PACK (keyed by reel UID, qty from StockOuts)
/// or a second-side MAGAZINE (keyed by PO+MAG#, pcs = QTY÷N).</summary>
public sealed record BoardToken(string Kind, string Key, string Label, int Pcs, DateTime At);

/// <summary>
/// Pure, in-memory tally of the boards staged at a line's input for the current lot — count-only, no interlock
/// (see [[pvs-board-flow]]). Deduplicates by token key (a pack UID, or a magazine's PO+MAG#), sums pcs, and
/// tracks how many magazines are expected (the N from any scanned slip) so the UI can show "X of N".
/// </summary>
public sealed class BoardInputLog
{
    private readonly Dictionary<string, BoardToken> _tokens = new(StringComparer.OrdinalIgnoreCase);
    private int _expectedMagazines;

    public IReadOnlyCollection<BoardToken> Tokens => _tokens.Values;
    public int TotalPcs => _tokens.Values.Sum(t => t.Pcs);
    public int Count => _tokens.Count;
    public int MagazinesScanned => _tokens.Values.Count(t => t.Kind == "magazine");
    public int ExpectedMagazines => _expectedMagazines;

    public bool Has(string key) => _tokens.ContainsKey(key);

    /// <summary>Add a token. Returns false if its key was already scanned (dedupe). A magazine's <paramref
    /// name="expectedMagazines"/> raises the expected count (N).</summary>
    public bool Add(BoardToken token, int? expectedMagazines = null)
    {
        if (expectedMagazines is int e && e > _expectedMagazines) _expectedMagazines = e;
        if (_tokens.ContainsKey(token.Key)) return false;
        _tokens[token.Key] = token;
        return true;
    }

    public void Reset() { _tokens.Clear(); _expectedMagazines = 0; }

    public void Restore(IEnumerable<BoardToken> tokens, int expectedMagazines)
    {
        _tokens.Clear();
        foreach (var t in tokens) _tokens[t.Key] = t;
        _expectedMagazines = expectedMagazines;
    }
}
