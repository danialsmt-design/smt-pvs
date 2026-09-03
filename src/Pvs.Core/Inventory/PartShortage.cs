namespace Pvs.Core.Inventory;

/// <summary>
/// One ProductBOM row reduced to what a shortage forecast needs: which model, which side, which LINE's copy
/// of the layout, the part, and how many pieces one CHILD BOARD consumes.
/// </summary>
public sealed record BomUsageRow(string Model, string Side, int Line, string PartNumber, int QtyPerBoard);

/// <summary>How many pieces of one part a single board of one model consumes, after the BOM copies are reconciled.</summary>
public sealed record ModelPartUsage(string Model, string PartNumber, int QtyPerBoard);

/// <summary>
/// An upcoming production lot: the order, its model, when it is due, how big it is, and how many boards of it
/// are already built. <see cref="LotSize"/> null/0 means the order row carried no usable figure.
/// </summary>
public sealed record UpcomingLot(string LotNo, string Model, DateTime? DeliveryDate, int? LotSize, int BuiltBoards)
{
    /// <summary>True only when a POSITIVE lot size is known. Zero is never a lot size.</summary>
    public bool SizeKnown => LotSize is int n && n > 0;

    /// <summary>Boards still to build. Unknown size =&gt; 0, so an unsized lot never invents demand.</summary>
    public int RemainingBoards => SizeKnown ? Math.Max(0, LotSize!.Value - Math.Max(0, BuiltBoards)) : 0;
}

/// <summary>
/// What is on hand for one part.
/// <para>
/// <b>Both halves are needed.</b> <see cref="StoreQty"/> is un-issued stock in the store (StockIns.RemainingQty),
/// which is ZEROED the moment a reel is issued to a line. <see cref="LineQty"/> is the live balance of the reels
/// already issued (the latest StockOuts row per reel UID), which PVS writes back as the feeders run down.
/// Reading only the store makes a part with thousands of pieces loaded on machines read as ZERO stock.
/// </para>
/// <see cref="LineQtyTracked"/> is the part of <see cref="LineQty"/> whose balance was actually refreshed
/// recently — i.e. sitting on a line PVS syncs. The rest is the as-issued figure and may be long consumed.
/// </summary>
public sealed record PartStock(string PartNumber, long StoreQty, long LineQty, long LineQtyTracked, decimal? UnitPrice)
{
    public long Available => Math.Max(0, StoreQty) + Math.Max(0, LineQty);
}

/// <summary>
/// Which lines' reel balances are actually being kept live, so the report can state its own confidence
/// instead of implying a precision it does not have.
/// <para>
/// The answer that matters is WHICH LINES, not a percentage. Most of the piece count on any line is stale
/// rows for reels long consumed, so the tracked share of pieces reads far worse than reality even on a line
/// PVS syncs every few minutes — it is kept here for diagnostics but is not what the report should say.
/// </para>
/// </summary>
public sealed record ShortageCoverage(
    long LineQty, long LineQtyTracked, IReadOnlyList<string> TrackedLines, IReadOnlyList<string> UntrackedLines)
{
    public static readonly ShortageCoverage Unknown = new(0, 0, Array.Empty<string>(), Array.Empty<string>());

    /// <summary>Share of line-side pieces whose balance was refreshed recently (0..100). Diagnostic only.</summary>
    public double TrackedPercent => LineQty <= 0 ? 100 : Math.Round(100.0 * LineQtyTracked / LineQty, 1);

    /// <summary>True when every line holding reels is syncing its balances.</summary>
    public bool FullyTracked => UntrackedLines.Count == 0;

    /// <summary>True when nothing at all is known about reel tracking (the coverage query failed or found nothing).</summary>
    public bool NothingKnown => TrackedLines.Count == 0 && UntrackedLines.Count == 0;
}

/// <summary>
/// The first upcoming lot that runs a part out, and by how much. One finding per part — the lot named is the
/// EARLIEST one that cannot be completed, which is the one there is still time to do something about.
/// </summary>
public sealed record ShortageFinding(
    string PartNumber, string LotNo, string Model, DateTime? DeliveryDate,
    long Available, long DemandThroughLot, long ShortBy,
    int LotBoards, int QtyPerBoard, decimal? UnitPrice, int DaysToDelivery, bool Urgent)
{
    /// <summary>Value of the missing pieces, when a unit price is known.</summary>
    public decimal? ShortValue => UnitPrice is decimal p ? p * ShortBy : null;

    /// <summary>Identity used to remember this shortage between runs.</summary>
    public string Key => PartNumber + "|" + LotNo;
}

/// <summary>Knobs for the forecast. Lead time is what makes a shortage URGENT rather than informational.</summary>
public sealed record ShortageOptions(int LeadTimeDays = 2, int HorizonDays = 21);

/// <summary>The whole forecast, including what it could NOT see — the caveats are part of the answer.</summary>
public sealed record ShortageForecast(
    DateTime GeneratedAt,
    IReadOnlyList<ShortageFinding> Findings,
    IReadOnlyList<string> ActiveLots,
    int LotsConsidered, int LotsAlreadyBuilt, int LotsUnknownSize,
    int PartsConsidered,
    IReadOnlyList<string> ModelsWithoutBom,
    ShortageCoverage Coverage)
{
    public IReadOnlyList<ShortageFinding> Urgent => Findings.Where(f => f.Urgent).ToList();
    public IReadOnlyList<ShortageFinding> Later => Findings.Where(f => !f.Urgent).ToList();
    public bool Any => Findings.Count > 0;

    public static ShortageForecast Empty(DateTime at) => new(
        at, Array.Empty<ShortageFinding>(), Array.Empty<string>(), 0, 0, 0, 0,
        Array.Empty<string>(), ShortageCoverage.Unknown);
}

/// <summary>
/// Turns raw ProductBOM rows into per-board usage per model+part.
/// <para>
/// Three traps make a naive SUM wrong, and all three are live in ReelPart-New:
/// </para>
/// <list type="number">
/// <item>A part can occupy SEVERAL feeders of one model+side (e.g. L254 mounts VE3-6800-105 from machine 2 at 9
/// per board and machine 1 at 1 more). Those rows must be SUMMED, not deduplicated.</item>
/// <item>A model's BOM is stored once per LINE and the copies DISAGREE (L261 'A Side' totals 55 placements under
/// Line 1 and 40 under Line 3; L311 'A Side' exists three times). Only ONE copy may be counted, otherwise
/// demand is multiplied by however many lines happen to hold a copy. We take the LARGEST copy per part: this is
/// a shortage warning, and quietly under-stating demand is the failure that lets a line stop.</item>
/// <item>A part can appear on BOTH sides of one model, and per-board usage is the SUM across sides. The sides
/// are often stored under DIFFERENT line numbers (L307 keeps 'B Side' under Line 1 and 'A Side' under Line 5),
/// so filtering to a single line silently loses half the bill of materials.</item>
/// </list>
/// 'Full' is its own side bucket, counted once — it means the row applies to the whole board, not once per side.
/// </summary>
public static class BomUsageResolver
{
    public static IReadOnlyList<ModelPartUsage> Resolve(IEnumerable<BomUsageRow>? rows)
    {
        if (rows is null) return Array.Empty<ModelPartUsage>();

        // 1. Same model+side+line+part on several feeders => one per-board figure.
        var perCopy = new Dictionary<(string Model, string Side, int Line, string Part), long>();
        foreach (var r in rows)
        {
            if (r is null) continue;
            string model = (r.Model ?? "").Trim();
            string part = (r.PartNumber ?? "").Trim();
            if (model.Length == 0 || part.Length == 0 || r.QtyPerBoard <= 0) continue;
            var key = (model, (r.Side ?? "").Trim().ToUpperInvariant(), r.Line, part);
            perCopy[key] = perCopy.TryGetValue(key, out var v) ? v + r.QtyPerBoard : r.QtyPerBoard;
        }

        // 2. The line copies of one side are duplicates that disagree => take the largest.
        var perSide = new Dictionary<(string Model, string Side, string Part), long>();
        foreach (var (k, qty) in perCopy)
        {
            var key = (k.Model, k.Side, k.Part);
            if (!perSide.TryGetValue(key, out var cur) || qty > cur) perSide[key] = qty;
        }

        // 3. A part used on both sides is consumed on both => sum across sides.
        var total = new Dictionary<(string Model, string Part), long>();
        foreach (var (k, qty) in perSide)
        {
            var key = (k.Model, k.Part);
            total[key] = total.TryGetValue(key, out var v) ? v + qty : qty;
        }

        return total
            .Where(kv => kv.Value > 0)
            .Select(kv => new ModelPartUsage(kv.Key.Model, kv.Key.Part, (int)Math.Min(int.MaxValue, kv.Value)))
            .OrderBy(u => u.Model, StringComparer.OrdinalIgnoreCase)
            .ThenBy(u => u.PartNumber, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

/// <summary>
/// Walks the upcoming lots in delivery-date order, accumulating each part's demand, and reports the FIRST lot
/// that a part cannot cover. Pure arithmetic over three inputs — lots, per-board usage, and what is on hand —
/// so every rule in it is testable without a database.
/// </summary>
public static class ShortageForecaster
{
    public static ShortageForecast Analyse(
        DateTime now,
        IEnumerable<UpcomingLot>? lots,
        IEnumerable<ModelPartUsage>? usage,
        IEnumerable<PartStock>? stock,
        ShortageOptions? options = null,
        ShortageCoverage? coverage = null)
    {
        var opt = options ?? new ShortageOptions();
        var lotList = (lots ?? Array.Empty<UpcomingLot>())
            .Where(l => l is not null && !string.IsNullOrWhiteSpace(l.LotNo) && !string.IsNullOrWhiteSpace(l.Model))
            .ToList();

        // Per-board usage indexed by model, and what is on hand indexed by part.
        var byModel = (usage ?? Array.Empty<ModelPartUsage>())
            .Where(u => u is not null && u.QtyPerBoard > 0 && !string.IsNullOrWhiteSpace(u.PartNumber))
            .GroupBy(u => u.Model.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ModelPartUsage>)g.ToList(), StringComparer.OrdinalIgnoreCase);

        var onHand = new Dictionary<string, PartStock>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in stock ?? Array.Empty<PartStock>())
        {
            if (s is null || string.IsNullOrWhiteSpace(s.PartNumber)) continue;
            onHand[s.PartNumber.Trim()] = s;
        }

        // Delivery-date order is the production order. A lot with no date is worked last, not first — an
        // undated row must never jump the queue and consume material ahead of a dated one.
        var ordered = lotList
            .OrderBy(l => l.DeliveryDate ?? DateTime.MaxValue)
            .ThenBy(l => l.LotNo, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var cumulative = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var found = new Dictionary<string, ShortageFinding>(StringComparer.OrdinalIgnoreCase);
        var active = new List<string>();
        var missingBom = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var parts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int built = 0, unsized = 0;

        foreach (var lot in ordered)
        {
            if (!lot.SizeKnown) { unsized++; continue; }
            int remaining = lot.RemainingBoards;
            if (remaining <= 0) { built++; continue; }          // already built — needs no material

            if (!byModel.TryGetValue(lot.Model.Trim(), out var lines) || lines.Count == 0)
            {
                missingBom.Add(lot.Model.Trim());
                continue;
            }

            active.Add(lot.LotNo.Trim());
            int days = DaysUntil(now, lot.DeliveryDate);

            foreach (var u in lines)
            {
                string part = u.PartNumber.Trim();
                parts.Add(part);
                long need = (long)remaining * u.QtyPerBoard;
                long cum = cumulative.TryGetValue(part, out var c) ? c + need : need;
                cumulative[part] = cum;

                if (found.ContainsKey(part)) continue;           // already reported at an earlier lot
                var have = onHand.TryGetValue(part, out var st) ? st : null;
                long avail = have?.Available ?? 0;
                if (cum <= avail) continue;

                found[part] = new ShortageFinding(
                    PartNumber: part, LotNo: lot.LotNo.Trim(), Model: lot.Model.Trim(),
                    DeliveryDate: lot.DeliveryDate, Available: avail, DemandThroughLot: cum,
                    ShortBy: cum - avail, LotBoards: remaining, QtyPerBoard: u.QtyPerBoard,
                    UnitPrice: have?.UnitPrice, DaysToDelivery: days,
                    Urgent: days <= opt.LeadTimeDays);
            }
        }

        var findings = found.Values
            .OrderBy(f => f.DeliveryDate ?? DateTime.MaxValue)
            .ThenByDescending(f => f.ShortBy)
            .ThenBy(f => f.PartNumber, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ShortageForecast(
            now, findings, active, ordered.Count, built, unsized, parts.Count,
            missingBom.ToList(), coverage ?? ShortageCoverage.Unknown);
    }

    /// <summary>Whole days from today to the delivery date. No date =&gt; treated as far away, never urgent.</summary>
    public static int DaysUntil(DateTime now, DateTime? deliveryDate) =>
        deliveryDate is DateTime d ? (int)(d.Date - now.Date).TotalDays : int.MaxValue;
}
