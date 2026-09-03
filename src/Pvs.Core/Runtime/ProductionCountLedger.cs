namespace Pvs.Core.Runtime;

/// <summary>The production context a run of panels belongs to — one DailyProductionCount row per context per window.</summary>
public readonly record struct ProductionKey(string Lot, string Model, string Side);

/// <summary>Unwritten panels for one context, and the instant that window opened (its first unwritten panel).</summary>
public sealed record ProductionBucket(ProductionKey Key, long Panels, DateTime FirstAt);

/// <summary>
/// A row the writer should append: the context, the panels to write, and the window they were produced in.
/// <see cref="Start"/> is always &lt;= <see cref="End"/> and always falls on <see cref="End"/>'s calendar day,
/// because DailyProductionCount stores one Date plus two TIME-OF-DAY strings — a start stamped from an earlier
/// day would land on the row's date and read as a window that ends before it begins.
/// </summary>
public sealed record ProductionWindow(ProductionKey Key, long Panels, DateTime Start, DateTime End);

/// <summary>
/// The DailyProductionCount writer's accounting: how many panels have been produced but not yet written, per
/// production context, and what window each unwritten run covers.
/// <para>
/// The ledger is an INCREMENT ledger and nothing else. One panel goes in per real completed board off the last
/// machine (<see cref="AddPanel"/>), and a successful DB write takes exactly what it wrote back out
/// (<see cref="Commit"/>). It is never derived from a lot total, a machine counter, or anything else that
/// accumulates across the whole lot run — that is precisely the mistake that produced the 2026-08-05 Line 5 row
/// (120 boards stamped into a 5-minute window, which is the lot-to-date figure, not the window's production).
/// </para>
/// <para>
/// It is also FORWARD-ONLY. Boards produced while PVS was not running, or before it became the line's count
/// source, are not in the ledger and are never invented into it: back-filling them would mean fabricating a
/// time window nobody recorded, and would double-count against whatever the operator app already keyed in.
/// The caller is expected to LOG such a gap instead — a known, visible undercount is recoverable, a phantom
/// overcount corrupts the line's yield figures and nobody can tell it happened.
/// </para>
/// <para>Thread-safe: the line's board events and the flush timer touch this concurrently.</para>
/// </summary>
public sealed class ProductionCountLedger
{
    private readonly Dictionary<ProductionKey, ProductionBucket> _buckets = new();
    private readonly object _gate = new();

    /// <summary>
    /// Restored buckets older than this are dropped instead of written — a last-resort bound against truly
    /// abandoned/corrupt state, NOT a normal path. Held production is now written back dated to its own day
    /// (see <see cref="WindowFor"/>), so a DB outage lasting hours or a long weekend no longer risks a mis-dated
    /// lump and MUST NOT be discarded — the whole point is that the line never loses a count it made. Set well
    /// beyond any realistic outage (30 days). A drop at this age is logged (to the line's file log) as an audit
    /// event, never silent.
    /// </summary>
    public static readonly TimeSpan DefaultStaleAfter = TimeSpan.FromDays(30);

    /// <summary>How old a restored bucket may be before <see cref="Restore"/> discards it.</summary>
    public TimeSpan StaleAfter { get; set; } = DefaultStaleAfter;

    /// <summary>Counts one completed panel against a production context. Contexts without a lot AND a model are
    /// not attributable to any row and are ignored.</summary>
    public void AddPanel(ProductionKey key, DateTime at)
    {
        if (string.IsNullOrWhiteSpace(key.Lot) || string.IsNullOrWhiteSpace(key.Model)) return;
        lock (_gate)
        {
            _buckets[key] = _buckets.TryGetValue(key, out var b)
                ? b with { Panels = b.Panels + 1 }
                : new ProductionBucket(key, 1, at);
        }
    }

    /// <summary>Unwritten panels for one context (0 when it has none).</summary>
    public long PanelsFor(ProductionKey key)
    {
        lock (_gate) return _buckets.TryGetValue(key, out var b) ? b.Panels : 0;
    }

    /// <summary>Unwritten panels across every context.</summary>
    public long TotalPanels { get { lock (_gate) return _buckets.Values.Sum(b => b.Panels); } }

    /// <summary>
    /// The rows due at <paramref name="winEnd"/> — one per context that has unwritten panels, each with a
    /// window clamped so it can never start after it ends nor carry a stamp from an earlier day. A context
    /// that a changeover opened mid-window comes back as its own row, so boards are never misattributed.
    /// </summary>
    public IReadOnlyList<ProductionWindow> Due(DateTime winEnd)
    {
        lock (_gate)
            return _buckets.Values
                .Where(b => b.Panels > 0)
                .Select(b =>
                {
                    var (start, end) = WindowFor(b.FirstAt, winEnd);
                    return new ProductionWindow(b.Key, b.Panels, start, end);
                })
                .ToList();
    }

    /// <summary>
    /// The written row is dated to the bucket's OWN day — the day its first unwritten board was produced — so a
    /// delayed write after a DB outage lands on the real production day, NOT the day it finally flushed. The end
    /// is capped to that same day (last tick) so a bucket that spanned midnight/an outage can never stamp a row
    /// whose date and end-time disagree. A start after the end collapses to the end. Both guarantee the row's
    /// Date, StartTime and EndTime are self-consistent and StartTime &lt;= EndTime.
    /// </summary>
    internal static (DateTime Start, DateTime End) WindowFor(DateTime firstAt, DateTime winEnd)
    {
        DateTime start = firstAt;
        DateTime dayEnd = firstAt.Date.AddDays(1).AddTicks(-1);
        DateTime end = winEnd < dayEnd ? winEnd : dayEnd;
        if (end < start) end = start;
        return (start, end);
    }

    /// <summary>
    /// Takes <paramref name="panels"/> back out of a context after its row is CONFIRMED written. Anything left
    /// (boards produced during the write, or held back under the caller's cap) stays for the next window and its
    /// window reopens at <paramref name="winEnd"/>, so the next row starts where this one ended — no overlap,
    /// no gap. Called only on success, so a DB failure just retries next window: no loss, no double-write.
    /// </summary>
    public void Commit(ProductionKey key, long panels, DateTime winEnd)
    {
        if (panels <= 0) return;
        lock (_gate)
        {
            if (!_buckets.TryGetValue(key, out var b)) return;
            long left = b.Panels - panels;
            if (left > 0) _buckets[key] = b with { Panels = left, FirstAt = winEnd };
            else _buckets.Remove(key);
        }
    }

    /// <summary>Every unwritten bucket, for persisting to dpc-state.json.</summary>
    public IReadOnlyList<ProductionBucket> Snapshot()
    {
        lock (_gate) return _buckets.Values.ToList();
    }

    /// <summary>
    /// Replaces the ledger with persisted buckets, dropping any that are stale (see <see cref="StaleAfter"/>)
    /// or empty. Returns the panels dropped so the caller can log/audit them — they are never written, but they
    /// are never swallowed quietly either.
    /// </summary>
    public long Restore(IEnumerable<ProductionBucket> buckets, DateTime now)
    {
        long dropped = 0;
        lock (_gate)
        {
            _buckets.Clear();
            foreach (var b in buckets)
            {
                if (b.Panels <= 0) continue;
                if (string.IsNullOrWhiteSpace(b.Key.Lot) || string.IsNullOrWhiteSpace(b.Key.Model)) continue;
                if (now - b.FirstAt > StaleAfter) { dropped += b.Panels; continue; }
                _buckets[b.Key] = b;
            }
        }
        return dropped;
    }

    /// <summary>Drops everything unwritten (used when the writer is disabled, so a later enable starts clean).
    /// Returns the panels discarded.</summary>
    public long Clear()
    {
        lock (_gate)
        {
            long had = _buckets.Values.Sum(b => b.Panels);
            _buckets.Clear();
            return had;
        }
    }
}
