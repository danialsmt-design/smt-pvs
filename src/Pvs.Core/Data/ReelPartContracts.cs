using Pvs.Core.Feeders;
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

/// <summary>One reel issued (StockOuts) for a lot: part number, reel UID, quantity.</summary>
public sealed record IssuedReel(string PartNumber, string Uid, int Qty);

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
/// Access to the material-control database (ReelPart-New). All methods are read-only queries EXCEPT
/// <see cref="UpdateRemainingQtyAsync"/>, the single write (a supervisor-authorised qty correction
/// during a Model-Change check). Implemented by Pvs.Data against SQL Server.
/// </summary>
public interface IReelPartRepository
{
    /// <summary>All runnable models (Products).</summary>
    Task<IReadOnlyList<Product>> GetProductsAsync(CancellationToken ct = default);

    /// <summary>
    /// The feeder assignments for a model, running side ("A" or "B"; anything else = both), and line.
    /// Side is resolved to ProductBOM's 'A Side'/'B Side'/'Full' values internally.
    /// A model's BOM holds every line's feeders, tagged by ProductBOM.Line, so <paramref name="line"/>
    /// (the LineId from config) filters to this line's layout. Pass 0 to include all lines.
    /// </summary>
    Task<IReadOnlyList<FeederAssignment>> GetFeederMapAsync(int productId, string side, int line, CancellationToken ct = default);

    /// <summary>Resolves a scanned badge UID to a person + role, or null if unknown.</summary>
    Task<Badge?> FindBadgeAsync(string badgeUid, CancellationToken ct = default);

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
    Task<IReadOnlyList<LotOrder>> GetLotOptionsAsync(string model, CancellationToken ct = default);

    /// <summary>
    /// Appends one production-count row to DailyProductionCount (Quantity = child boards). Used when PVS is the
    /// production-count writer for a line. Requires the login to have INSERT on DailyProductionCount. Returns rows written.
    /// </summary>
    Task<int> InsertProductionCountAsync(ProductionCountEntry row, CancellationToken ct = default);

    /// <summary>Production runs logged for a line on a date (DailyProductionCount; date as 'yyyy-MM-dd'), oldest first.</summary>
    Task<IReadOnlyList<ProductionRun>> GetDailyProductionAsync(string line, string date, CancellationToken ct = default);

    /// <summary>
    /// Feeder placement counts (ProductBOM) for a model name + running side ("A"/"B") + line — the daily
    /// feeder-usage reconciliation multiplies these by boards produced. Side resolves to 'A Side'/'B Side'
    /// (plus 'Full', which applies to both). Returns machine + supply position + part + per-board count.
    /// </summary>
    Task<IReadOnlyList<BomFeederUse>> GetBomUsageAsync(string model, string side, int line, CancellationToken ct = default);

    /// <summary>
    /// Sets a reel's quantity in StockOuts (the most-recent stock-out row for the UID) — the ONLY write
    /// in this repo. Used by the Model-Change supervisor qty correction, matching the source that
    /// <see cref="FindStockOutQtyAsync"/> reads. Returns rows affected (0 if the reel isn't found).
    /// Requires the connection login to have UPDATE on StockOuts.
    /// </summary>
    Task<int> UpdateReelQtyAsync(string partUid, string partNumber, int quantity, CancellationToken ct = default);
}
