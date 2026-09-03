namespace Pvs.Core.Inventory;

/// <summary>What a recount of a feeder concluded.</summary>
public enum RecountVerdict
{
    /// <summary>Counted figure is within tolerance of what the account expected — accept it.</summary>
    Ok,
    /// <summary>Outside tolerance. A supervisor (L2+) must approve before the count is applied.</summary>
    NeedsSupervisor,
}

/// <summary>The outcome of a recount, with the numbers a supervisor needs to judge it.</summary>
/// <param name="Expected">loaded − mounted, i.e. what the account believed was left.</param>
/// <param name="Actual">What the operator physically counted.</param>
/// <param name="Difference">Actual − Expected. Negative means material is missing.</param>
/// <param name="TolerancePcs">The tolerance that applied, in pieces.</param>
public sealed record RecountResult(
    RecountVerdict Verdict,
    long Expected,
    long Actual,
    long Difference,
    double DifferencePct,
    long TolerancePcs,
    string Message);

/// <summary>
/// A running account of one feeder position — how much has been put in, and how much the machine has
/// taken out. Built for TRAY feeders (the 5xx positions on the IC machines), where there is no reel UID
/// to scan and the operator keys the quantity loaded, but it works for any feeder.
/// <para>
/// Two counters instead of one remaining figure, because the pair makes loss visible:
/// <c>loaded − mounted − what is physically left = loss on this feeder</c>. When a tray empties, that
/// arithmetic closes the loop on the highest-value parts in the factory (the ICs and bare boards) without
/// needing per-feeder pickup data from the machine.
/// </para>
/// <para>
/// Pure and hardware-free: the caller feeds it board completions and operator key-ins. It decides nothing
/// about badges or screens — it only keeps the arithmetic and says when a recount is out of tolerance.
/// </para>
/// </summary>
public sealed class FeederAccount
{
    /// <summary>Default recount tolerance as a FRACTION of total loaded (0.002 = 0.2%).
    /// Measured pickup loss on real machines is ~0.1% (C1M: 90,352 attempted vs 90,256 successful),
    /// so 0.2% sits at roughly twice observed attrition.</summary>
    public const double DefaultTolerancePct = 0.002;

    private readonly List<LoadEntry> _loads = new();

    public FeederAccount(int machine, int feeder, string part, double tolerancePct = DefaultTolerancePct)
    {
        Machine = machine;
        Feeder = feeder;
        Part = part;
        TolerancePct = tolerancePct > 0 ? tolerancePct : DefaultTolerancePct;
    }

    public int Machine { get; }
    public int Feeder { get; }
    public string Part { get; }
    public double TolerancePct { get; }

    /// <summary>Everything the operator has keyed in as loaded onto this feeder, cumulative.</summary>
    public long TotalLoaded { get; private set; }

    /// <summary>Everything the machine has placed from this feeder, cumulative.</summary>
    public long TotalMounted { get; private set; }

    /// <summary>loaded − mounted. Can go negative if the operator forgot to key a load in — that is a
    /// signal, not an error, so it is not clamped.</summary>
    public long Expected => TotalLoaded - TotalMounted;

    /// <summary>The key-ins recorded against this feeder, oldest first.</summary>
    public IReadOnlyList<LoadEntry> Loads => _loads;

    /// <summary>One operator key-in.</summary>
    /// <param name="Qty">Pieces added. A supervisor correction may be negative.</param>
    public readonly record struct LoadEntry(long Qty, string ByBadge, string ByName, DateTime At, bool IsCorrection, string? Note);

    /// <summary>
    /// Operator keys in a quantity loaded onto the feeder. ADDS to the running total — a tray gets topped
    /// up, so each key-in is another load rather than a replacement.
    /// </summary>
    public void Load(long qty, string byBadge, string byName, DateTime at, string? note = null)
    {
        if (qty <= 0) throw new ArgumentOutOfRangeException(nameof(qty), "A load must be a positive quantity.");
        _loads.Add(new LoadEntry(qty, byBadge, byName, at, IsCorrection: false, note));
        TotalLoaded += qty;
    }

    /// <summary>The machine placed <paramref name="perBoard"/> parts from this feeder for one completed board.</summary>
    public void OnBoardComplete(int perBoard)
    {
        if (perBoard <= 0) return;
        TotalMounted += perBoard;
    }

    /// <summary>Add mounted parts in bulk (e.g. re-baselining from a board count).</summary>
    public void AddMounted(long pieces)
    {
        if (pieces <= 0) return;
        TotalMounted += pieces;
    }

    /// <summary>
    /// The operator counted what is physically on the feeder. Compares against <see cref="Expected"/> and
    /// says whether it can be accepted or needs a supervisor.
    /// <para>
    /// Tolerance is a straight percentage of total loaded. On a small tray that is a very small number of
    /// pieces, so expect this to escalate readily — that is the configured intent.
    /// </para>
    /// </summary>
    public RecountResult Recount(long actual)
    {
        if (actual < 0) throw new ArgumentOutOfRangeException(nameof(actual), "A counted quantity cannot be negative.");

        long expected = Expected;
        long diff = actual - expected;
        long tol = (long)Math.Floor(TotalLoaded * TolerancePct);
        double pct = TotalLoaded > 0 ? (double)Math.Abs(diff) / TotalLoaded * 100.0 : (diff == 0 ? 0.0 : 100.0);

        if (Math.Abs(diff) <= tol)
            return new RecountResult(RecountVerdict.Ok, expected, actual, diff, pct, tol,
                $"counted {actual}, expected {expected} — within tolerance ({tol} pcs)");

        string dir = diff < 0 ? "short" : "over";
        return new RecountResult(RecountVerdict.NeedsSupervisor, expected, actual, diff, pct, tol,
            $"counted {actual}, expected {expected} — {Math.Abs(diff)} pcs {dir} ({pct:0.00}%), " +
            $"beyond the {TolerancePct * 100:0.##}% tolerance ({tol} pcs). Supervisor approval required.");
    }

    /// <summary>
    /// A supervisor accepts a counted figure that failed <see cref="Recount"/>, or corrects a missed key-in.
    /// The account is adjusted so <see cref="Expected"/> equals the counted figure, and the adjustment is
    /// recorded as a correction attributed to the supervisor — the history is never rewritten.
    /// </summary>
    public LoadEntry ApplySupervisorCorrection(long actual, string byBadge, string byName, DateTime at, string? note = null)
    {
        if (actual < 0) throw new ArgumentOutOfRangeException(nameof(actual), "A counted quantity cannot be negative.");
        long adjust = actual - Expected;
        var entry = new LoadEntry(adjust, byBadge, byName, at, IsCorrection: true, note);
        _loads.Add(entry);
        TotalLoaded += adjust;
        return entry;
    }

    /// <summary>
    /// Loss on this feeder once it is empty: what went in, minus what the machine placed, minus what is
    /// physically left. Null while the account has never been loaded, since the figure would be meaningless.
    /// </summary>
    public long? LossWhenEmptied(long actualRemaining)
    {
        if (TotalLoaded <= 0) return null;
        return TotalLoaded - TotalMounted - actualRemaining;
    }
}
