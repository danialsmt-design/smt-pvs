namespace Pvs.LineApp.Runtime;

/// <summary>
/// In-memory record of the standby (spare, not-loaded) reel UIDs an operator has physically scanned during a
/// standby-check pass. It is compared against what the DB believes is at the line (StockOuts issued-at-line minus
/// what is loaded on a feeder) to surface PHANTOM spares — reels the system thinks are on the rack but that were
/// never scanned because they are not physically there (usually old empties never scanned back).
/// <para>Not persisted: a pass is a single sitting, and the write-off of phantoms is a separate, approved step —
/// this class never writes to the DB.</para>
/// </summary>
public sealed class StandbyScanState
{
    private readonly HashSet<string> _scanned = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    /// <summary>When the current pass started (first scan), or null if none is in progress.</summary>
    public DateTime? StartedAt { get; private set; }

    /// <summary>Record a scanned UID. Returns true if it was newly added (false if blank or already scanned).</summary>
    public bool Add(string? uid)
    {
        var u = (uid ?? string.Empty).Trim();
        if (u.Length == 0) return false;
        lock (_gate) { StartedAt ??= DateTime.Now; return _scanned.Add(u); }
    }

    /// <summary>Start a fresh pass — forget everything scanned so far.</summary>
    public void Reset() { lock (_gate) { _scanned.Clear(); StartedAt = null; } }

    public bool Has(string? uid) { lock (_gate) { return _scanned.Contains((uid ?? string.Empty).Trim()); } }

    public IReadOnlyList<string> All() { lock (_gate) { return _scanned.ToList(); } }
}
