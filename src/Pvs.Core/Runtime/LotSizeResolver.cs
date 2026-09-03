using Pvs.Core.Data;

namespace Pvs.Core.Runtime;

/// <summary>Which DeliveryDocuments column the lot size actually came from.</summary>
public enum LotSizeSource
{
    /// <summary>No usable figure — every candidate column was null or zero.</summary>
    Unknown,
    /// <summary>The original ordered quantity (Quantity).</summary>
    Quantity,
    /// <summary>A revision of the order (RevisedQty), which supersedes Quantity while the lot is live.</summary>
    RevisedQty,
    /// <summary>What was actually delivered (DeliveredQty) — the real figure once Status = 'Delivered'.</summary>
    DeliveredQty,
}

/// <summary>
/// The lot size as resolved from a DeliveryDocuments row: the figure, where it came from, and a note
/// explaining the choice for the log. <see cref="IsKnown"/> is the only thing callers should gate on.
/// </summary>
public sealed record LotSize(int? Boards, LotSizeSource Source, bool Delivered, string Note)
{
    /// <summary>True only when a POSITIVE board figure was resolved. Zero is never a lot size (see
    /// <see cref="LotSizeResolver"/>), so a zero row reads as unknown, not as "a lot of nothing".</summary>
    public bool IsKnown => Boards is int b && b > 0;

    /// <summary>Nothing known about the lot at all.</summary>
    public static readonly LotSize None = new(null, LotSizeSource.Unknown, false, "lot not found in DeliveryDocuments");
}

/// <summary>
/// Works out how many boards a lot was actually for, from the raw DeliveryDocuments columns.
/// <para>
/// While a lot is live the size is <c>RevisedQty</c> if it has been revised, else <c>Quantity</c>. Once the
/// lot is DELIVERED the order row is closed out: the real figure moves to <c>DeliveredQty</c> and RevisedQty
/// is zeroed. Reading a delivered lot with the live rule therefore returns the wrong number — and if
/// Quantity was zeroed too, it returns ZERO, which then makes every board produced look like
/// over-production. That is the whole reason this lives in its own tested function.
/// </para>
/// <para>
/// ZERO IS NOT A SIZE. Every candidate is only accepted when positive, and a row that yields nothing
/// positive resolves to <see cref="LotSizeSource.Unknown"/>. A zero target would otherwise silently turn
/// into "count exceeds lot size" on every single comparison, and — worse — into a cap that refuses every
/// legitimate machine-count adoption.
/// </para>
/// <para>
/// <b>Unverified against the live database.</b> The delivered-lot behaviour above is as reported by the
/// people who maintain the order data; it could not be confirmed against ReelPart-New from here (no DB
/// access), and the <c>DeliveredQty</c> column may not exist on every deployment. So the resolver is
/// written to degrade safely: a missing/null DeliveredQty on a delivered lot falls back to the live rule
/// and SAYS SO in <see cref="LotSize.Note"/>, rather than resolving to zero or throwing.
/// </para>
/// </summary>
public static class LotSizeResolver
{
    /// <summary>True when a DeliveryDocuments Status means the order has been closed out as delivered.</summary>
    public static bool IsDelivered(string? status) =>
        string.Equals(status?.Trim(), "Delivered", StringComparison.OrdinalIgnoreCase);

    /// <summary>Resolves the lot size. A null row (lot not in DeliveryDocuments) gives <see cref="LotSize.None"/>.</summary>
    public static LotSize Resolve(LotSizeRow? row)
    {
        if (row is null) return LotSize.None;
        bool delivered = IsDelivered(row.Status);

        if (delivered)
        {
            // Closed-out order: DeliveredQty is the truth, and RevisedQty has been zeroed.
            if (Positive(row.DeliveredQty) is int d)
                return new LotSize(d, LotSizeSource.DeliveredQty, true, "delivered lot: DeliveredQty");

            // No DeliveredQty (null column, or an old row that predates it). Fall back to the live rule but
            // flag it loudly — this figure may be the pre-revision order, not what actually shipped.
            if (Live(row) is (int b, LotSizeSource s))
                return new LotSize(b, s, true,
                    $"delivered lot with no DeliveredQty — fell back to {s}; verify against the delivery note");

            return new LotSize(null, LotSizeSource.Unknown, true,
                "delivered lot with no DeliveredQty and nothing positive in RevisedQty/Quantity — size unknown");
        }

        if (Live(row) is (int lb, LotSizeSource ls))
            return new LotSize(lb, ls, false, ls == LotSizeSource.RevisedQty ? "revised order qty" : "ordered qty");

        return new LotSize(null, LotSizeSource.Unknown, false, "no positive Quantity/RevisedQty on the order row");
    }

    /// <summary>The live-lot rule: RevisedQty when it has been set, else Quantity. Null when neither is positive.</summary>
    private static (int Boards, LotSizeSource Source)? Live(LotSizeRow row)
    {
        if (Positive(row.RevisedQty) is int r) return (r, LotSizeSource.RevisedQty);
        if (Positive(row.Quantity) is int q) return (q, LotSizeSource.Quantity);
        return null;
    }

    /// <summary>A quantity column is only usable when it is present AND positive.</summary>
    private static int? Positive(int? v) => v is int n && n > 0 ? n : null;
}
