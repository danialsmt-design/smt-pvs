using Pvs.Core.Feeders;
using Pvs.Core.Inventory;
using Pvs.Core.People;

namespace Pvs.Core.Data;

/// <summary>A product/model the line can run (Products table).</summary>
public sealed record Product(int ProductId, string Name);

/// <summary>
/// One feeder assignment for a model+side (a ProductBOM row).
/// NOTE: QtyPerUnit is ProductBOM.Quantity = placements per UNIT, which is NOT the per-board
/// mount-step used for consumption. It is carried for reference only; the forecast uses the
/// machine feeder-list Mount Step, not this.
/// Position may be unassigned (a hand-placed part) — see SupplyPosition.IsAssigned.
/// </summary>
public sealed record FeederAssignment(int Machine, SupplyPosition Position, string PartNumber, int QtyPerUnit);

/// <summary>A reel row from StockIns, looked up by its UID.</summary>
public sealed record ReelInfo(string PartUid, string PartNumber, int RemainingQty, bool Active);

/// <summary>The model + side currently running on a line (from the production system's latest lot).</summary>
public sealed record CurrentLot(string Model, string Side, string LotNo = "");

/// <summary>A delivery order/lot from DeliveryDocuments — its PONumber (lot number), target qty and delivery date.</summary>
public sealed record LotOrder(string PoNumber, int Target, DateTime? DeliveryDate);

/// <summary>
/// The RAW quantity columns of a DeliveryDocuments row, straight off the DB with no interpretation.
/// Which one is the real lot size depends on <see cref="Status"/> — see
/// <see cref="Pvs.Core.Runtime.LotSizeResolver"/>, which owns that decision so it is unit-testable
/// instead of buried in a CASE expression.
/// <see cref="DeliveredQty"/> is null when the column is absent from this database.
/// </summary>
public sealed record LotSizeRow(string PoNumber, string? Status, int? Quantity, int? RevisedQty, int? DeliveredQty);

/// <summary>
/// How many boards a lot has logged in DailyProductionCount, split by WHO wrote the rows: the operator's own
/// application (SenderIp is a real IP, OperatorName a person) versus PVS's own rows (SenderIp = 'PVS').
/// The split matters because comparing PVS's live count against a total that already contains PVS's own
/// writes compares PVS with itself and always "agrees".
/// </summary>
public sealed record LotBoardTally(int OperatorBoards, int PvsBoards, int OperatorRows, string? LastOperator, string? LastSenderIp);

/// <summary>One reel issued (StockOuts) for a lot: part number, reel UID, quantity.</summary>
public sealed record IssuedReel(string PartNumber, string Uid, int Qty);

/// <summary>A reel retired to the ConsumedReels list — its UID, part, A/B/C rank, the remaining it had when
/// retired (so a restore can put it back), and the line/lot it came off.</summary>
public sealed record ConsumedReel(string Uid, string PartNumber, string Rank, int RemainingAtRetire, string Line, string LotNo);

/// <summary>A production-count row to append to DailyProductionCount (Quantity is a CHILD-BOARD count).</summary>
public sealed record ProductionCountEntry(
    string Date, string StartTime, string EndTime, string Model, string Side,
    int Quantity, string Line, string LotNo, string Shift, string OperatorName, string SenderIp, string SessionId);

/// <summary>One production run logged for a line on a date (a DailyProductionCount row).</summary>
public sealed record ProductionRun(
    string Model, string Side, int Quantity, int ExcessQuantity,
    string LotNo, string Shift, string StartTime, string EndTime);

/// <summary>A feeder's per-board placement count from a model/side/line BOM (for daily usage reconciliation).</summary>
public sealed record BomFeederUse(int Machine, string SupplyPosition, string PartNumber, int PerBoard);

/// <summary>
/// How much of one line's issued-reel stock is still being kept live by PVS. Reels on a line that does not
/// sync its balances keep their AS-ISSUED quantity forever, so their contribution to "available" is a guess.
/// </summary>
public sealed record LineReelTracking(string Line, int Reels, long Qty, int FreshReels, long FreshQty);

/// <summary>
/// Access to the material-control database (ReelPart-New). All methods are read-only queries EXCEPT
/// <see cref="UpdateRemainingQtyAsync"/>, the single write (a supervisor-authorised qty correction
/// during a Model-Change check). Implemented by Pvs.Data against SQL Server.
/// </summary>
public interface IReelPartRepository
{
    /// <summary>All runnable models (Products).</summary>
    /// <summary>Cheap connectivity probe (SELECT 1). Returns true if the DB answered; throws if unreachable —
    /// the DB-health heartbeat uses either outcome as the signal.</summary>
    Task<bool> PingAsync(CancellationToken ct = default);

    Task<IReadOnlyList<Product>> GetProductsAsync(CancellationToken ct = default);

    /// <summary>
    /// The feeder assignments for a model, running side ("A" or "B"; anything else = both), and line.
    /// Side is resolved to ProductBOM's 'A Side'/'B Side'/'Full' values internally.
    /// A model's BOM holds every line's feeders, tagged by ProductBOM.Line, so <paramref name="line"/>
    /// (the LineId from config) filters to this line's layout. Pass 0 to include all lines.
    /// </summary>
    Task<IReadOnlyList<FeederAssignment>> GetFeederMapAsync(int productId, string side, int line, CancellationToken ct = default);

    /// <summary>Resolves a scanned badge UID to a person + role, or null if unknown. Implementations that talk to
    /// the DB should keep a LOCAL cache of resolved badges so operator/supervisor auth still works when the DB is
    /// unreachable (a parts-exchange must never be blocked by a DB outage).</summary>
    Task<Badge?> FindBadgeAsync(string badgeUid, CancellationToken ct = default);

    /// <summary>Loads EVERY badge (UID→person+role) so the local offline cache is complete, not just the badges
    /// seen so far. Called on a heartbeat while the DB is up; returns what it cached (empty when the DB is down).</summary>
    Task<int> PreloadBadgesAsync(CancellationToken ct = default);

    /// <summary>Looks up a reel by its UID (+ part number), for pre-filling the remaining quantity. Null if not found.</summary>
    Task<ReelInfo?> FindReelAsync(string partUid, string partNumber, CancellationToken ct = default);

    /// <summary>
    /// The reel's quantity from StockOuts (most-recent stock-out row for the UID) — this, not StockIns,
    /// is the qty shown for confirmation in a Model-Change check. Null if the reel has no stock-out row
    /// (in which case the caller may fall back to a qty embedded in the scanned UID).
    /// </summary>
    Task<int?> FindStockOutQtyAsync(string partUid, string partNumber, CancellationToken ct = default);

    /// <summary>Looks up a reel (part + current StockOuts quantity) by UID alone — for the reel-qty editor. Null if not found.</summary>
    Task<ReelInfo?> FindStockOutReelAsync(string partUid, CancellationToken ct = default);

    /// <summary>The model + side currently running on a line (most-recent production lot), for auto model detection. Null if none.</summary>
    Task<CurrentLot?> GetCurrentLotAsync(int line, CancellationToken ct = default);

    /// <summary>The lot/PO target quantity (boards) from DeliveryDocuments (RevisedQty if set, else Quantity). Null if the lot isn't found.</summary>
    Task<int?> GetLotTargetAsync(string lotNo, CancellationToken ct = default);

    /// <summary>The model (DeliveryDocuments.ProductName) a lot/PO belongs to — used when a magazine-slip QR opens a
    /// lot, to check the slip's PO against the model the machines are running. Null if the PO isn't found.</summary>
    Task<string?> GetLotModelAsync(string lotNo, CancellationToken ct = default);

    /// <summary>
    /// The RAW quantity columns of a lot's DeliveryDocuments row (Status + Quantity + RevisedQty + DeliveredQty),
    /// left uninterpreted for <see cref="Pvs.Core.Runtime.LotSizeResolver"/> to resolve — unlike
    /// <see cref="GetLotTargetAsync"/>, which applies the live-lot rule in SQL and so reads a DELIVERED lot
    /// wrongly. Null if the lot isn't in DeliveryDocuments.
    /// </summary>
    Task<LotSizeRow?> GetLotSizeAsync(string lotNo, CancellationToken ct = default);

    /// <summary>
    /// Boards recorded in DailyProductionCount for a lot + side + line, SPLIT by who wrote the rows: the
    /// operator's own application versus PVS itself (SenderIp = 'PVS'). The four-way accuracy check needs the
    /// operator's figure on its own — the combined total in <see cref="GetProducedBoardsForLotAsync"/> contains
    /// PVS's own writes, so comparing PVS against it compares PVS with itself.
    /// </summary>
    Task<LotBoardTally> GetLotBoardTallyAsync(string lotNo, string side, int line, CancellationToken ct = default);

    /// <summary>
    /// Total pieces ISSUED (from StockOuts) per part number for a lot + side + line — i.e. the reels staged
    /// for that lot's run. Lets the app warn when the issued material won't cover the whole lot (a re-request
    /// is needed, which has lead time). StockOuts.Model holds the lot/PO number (sometimes two joined by " | ").
    /// </summary>
    Task<IReadOnlyDictionary<string, int>> GetIssuedForLotAsync(string lotNo, string side, int line, CancellationToken ct = default);

    /// <summary>Individual reels ISSUED (StockOuts) for a lot + side + line — part, reel UID, qty. Lets the app
    /// show which issued reels are staged but not yet loaded on a machine (issued − currently-loaded UIDs).</summary>
    Task<IReadOnlyList<IssuedReel>> GetIssuedReelsForLotAsync(string lotNo, string side, int line, CancellationToken ct = default);

    /// <summary>
    /// EVERY reel currently sitting at a line — issued to it (StockOuts) within <paramref name="daysBack"/>,
    /// still carrying stock, whatever lot or side it was drawn against.
    /// <para>
    /// This exists because a reel is at the line physically, not logically. Lots change several times a day
    /// while the rack does not, so scoping the staged view by lot number hides material that is a few steps
    /// from the machine. Observed live: feeder F120 on Line 1 was an hour from empty and showed "nothing
    /// staged", while two full 10,000-piece reels of that exact part sat on the rack, issued under the
    /// previous lot.
    /// </para>
    /// <para>
    /// The <paramref name="daysBack"/> window is what makes this safe. StockOuts keeps a row per reel
    /// forever, and reels consumed before PVS began writing balances back still show their original issued
    /// quantity — without a bound, months-old empty reels would be reported as available.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<IssuedReel>> GetReelsAtLineAsync(int line, int daysBack, CancellationToken ct = default);

    /// <summary>
    /// The next lot to run for a model+side+line — the earliest Planned (un-delivered) DeliveryDocuments
    /// order whose delivery date is LATER than the latest lot already produced for this model/side/line
    /// (i.e. the next in the production sequence, skipping lots already produced or behind the current one).
    /// Returns its PONumber (the lot number), target qty (boards) and delivery date, or null if none scheduled.
    /// </summary>
    Task<LotOrder?> GetNextDeliveryLotAsync(string model, string side, int line, CancellationToken ct = default);

    /// <summary>
    /// Candidate lots for the manual selection dropdown — the Planned (un-delivered) DeliveryDocuments orders
    /// for a model, oldest delivery date first (recent + upcoming). Each carries PONumber, target and date.
    /// </summary>
    Task<IReadOnlyList<LotOrder>> GetLotOptionsAsync(string model, IReadOnlyList<string>? reopenLots = null, CancellationToken ct = default);

    /// <summary>
    /// Appends one production-count row to DailyProductionCount (Quantity = child boards). Used when PVS is the
    /// production-count writer for a line. Requires the login to have INSERT on DailyProductionCount. Returns rows written.
    /// </summary>
    Task<int> InsertProductionCountAsync(ProductionCountEntry row, CancellationToken ct = default);

    /// <summary>Production runs logged for a line on a date (DailyProductionCount; date as 'yyyy-MM-dd'), oldest first.</summary>
    Task<IReadOnlyList<ProductionRun>> GetDailyProductionAsync(string line, string date, CancellationToken ct = default);

    /// <summary>
    /// Total boards already recorded in DailyProductionCount for a lot/side/line (SUM of Quantity) — used when
    /// PVS takes over as the writer mid-lot, to seed its bucket without double-counting whatever is already logged.
    /// Side matches on first letter ("A"/"B"); empty side sums all sides of the lot.
    /// </summary>
    Task<int> GetProducedBoardsForLotAsync(string lotNo, string side, int line, CancellationToken ct = default);

    /// <summary>
    /// Feeder placement counts (ProductBOM) for a model name + running side ("A"/"B") + line — the daily
    /// feeder-usage reconciliation multiplies these by boards produced. Side resolves to 'A Side'/'B Side'
    /// (plus 'Full', which applies to both). Returns machine + supply position + part + per-board count.
    /// </summary>
    Task<IReadOnlyList<BomFeederUse>> GetBomUsageAsync(string model, string side, int line, CancellationToken ct = default);

    /// <summary>
    /// Upcoming production lots inside <paramref name="horizonDays"/>: the un-delivered DeliveryDocuments
    /// orders for board models, each with its resolved size and the boards already built for it.
    /// Ordered by delivery date. Used by the forward-looking shortage monitor.
    /// </summary>
    Task<IReadOnlyList<UpcomingLot>> GetUpcomingLotsAsync(int horizonDays, CancellationToken ct = default);

    /// <summary>
    /// Every ProductBOM row for the named models, RAW — one row per model+side+line+part, uninterpreted.
    /// Reconciling the disagreeing per-line copies and the both-sides usage is
    /// <see cref="Pvs.Core.Inventory.BomUsageResolver"/>'s job, so those rules are testable.
    /// </summary>
    Task<IReadOnlyList<BomUsageRow>> GetBomUsageForModelsAsync(IReadOnlyCollection<string> models, CancellationToken ct = default);

    /// <summary>
    /// What is on hand for each named part: un-issued store stock plus the live balance of reels already at
    /// the lines, with the actively-tracked share and a unit price when one is known.
    /// <paramref name="reelDaysBack"/> bounds how old an issue may be to still count as at the line;
    /// <paramref name="freshHours"/> is how recently a balance must have been written to count as tracked.
    /// </summary>
    Task<IReadOnlyList<PartStock>> GetPartStockAsync(
        IReadOnlyCollection<string> parts, int reelDaysBack, int freshHours, CancellationToken ct = default);

    /// <summary>Per-line issued-reel totals and how much of each is actively tracked — the report's own confidence.</summary>
    Task<IReadOnlyList<LineReelTracking>> GetReelTrackingByLineAsync(int reelDaysBack, int freshHours, CancellationToken ct = default);

    /// <summary>
    /// Sets a reel's quantity in StockOuts (the most-recent stock-out row for the UID) — the ONLY write
    /// in this repo. Used by the Model-Change supervisor qty correction, matching the source that
    /// <see cref="FindStockOutQtyAsync"/> reads. Returns rows affected (0 if the reel isn't found).
    /// Requires the connection login to have UPDATE on StockOuts.
    /// </summary>
    Task<int> UpdateReelQtyAsync(string partUid, string partNumber, int quantity, CancellationToken ct = default);

    /// <summary>The A/B/C stock rank for a part (PartRanks table), upper-cased, or null if the part isn't ranked.</summary>
    Task<string?> GetPartRankAsync(string partNumber, CancellationToken ct = default);

    /// <summary>Records a reel as retired in ConsumedReels (skips if an active record for the UID already exists).
    /// Returns rows written. Requires INSERT on ConsumedReels.</summary>
    Task<int> RecordConsumedReelAsync(ConsumedReel reel, CancellationToken ct = default);

    /// <summary>The still-active (not-yet-restored) consumed record for a UID — for bringing a reel back. Null if none.</summary>
    Task<ConsumedReel?> GetActiveConsumedReelAsync(string uid, CancellationToken ct = default);

    /// <summary>Marks a UID's active consumed record as restored (reversibility). Returns rows affected.</summary>
    Task<int> MarkConsumedRestoredAsync(string uid, CancellationToken ct = default);

    /// <summary>Accumulates written-off (attrition) pieces for a part in PartAttrition — adds <paramref name="pcsDelta"/>
    /// pieces and <paramref name="reelDelta"/> reels to the running total (pass negatives to reverse a restore).
    /// Upserts the part's row. Returns rows affected.</summary>
    Task<int> AddPartAttritionAsync(string partNumber, int pcsDelta, int reelDelta, CancellationToken ct = default);
}
