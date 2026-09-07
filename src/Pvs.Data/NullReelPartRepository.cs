using Pvs.Core.Data;
using Pvs.Core.People;

namespace Pvs.Data;

/// <summary>
/// A do-nothing repository used when no SQL login is configured yet. Lets the app run as the
/// live monitor without a database; verification simply can't resolve data until credentials are set.
/// </summary>
public sealed class NullReelPartRepository : IReelPartRepository
{
    public Task<bool> PingAsync(CancellationToken ct = default) => Task.FromResult(false);

    public Task<IReadOnlyList<Product>> GetProductsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Product>>(Array.Empty<Product>());

    public Task<IReadOnlyList<FeederAssignment>> GetFeederMapAsync(int productId, string side, int line, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<FeederAssignment>>(Array.Empty<FeederAssignment>());

    public Task<Badge?> FindBadgeAsync(string badgeUid, CancellationToken ct = default) =>
        Task.FromResult<Badge?>(null);

    public Task<int> PreloadBadgesAsync(CancellationToken ct = default) => Task.FromResult(0);

    public Task<ReelInfo?> FindReelAsync(string partUid, string partNumber, CancellationToken ct = default) =>
        Task.FromResult<ReelInfo?>(null);

    public Task<int?> FindStockOutQtyAsync(string partUid, string partNumber, CancellationToken ct = default) =>
        Task.FromResult<int?>(null);

    public Task<ReelInfo?> FindStockOutReelAsync(string partUid, CancellationToken ct = default) =>
        Task.FromResult<ReelInfo?>(null);

    public Task<CurrentLot?> GetCurrentLotAsync(int line, CancellationToken ct = default) =>
        Task.FromResult<CurrentLot?>(null);

    public Task<int?> GetLotTargetAsync(string lotNo, CancellationToken ct = default) =>
        Task.FromResult<int?>(null);

    public Task<string?> GetLotModelAsync(string lotNo, CancellationToken ct = default) =>
        Task.FromResult<string?>(null);

    public Task<LotSizeRow?> GetLotSizeAsync(string lotNo, CancellationToken ct = default) =>
        Task.FromResult<LotSizeRow?>(null);

    public Task<LotBoardTally> GetLotBoardTallyAsync(string lotNo, string side, int line, CancellationToken ct = default) =>
        Task.FromResult(new LotBoardTally(0, 0, 0, null, null));

    public Task<IReadOnlyDictionary<string, int>> GetIssuedForLotAsync(string lotNo, string side, int line, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<string, int>>(new Dictionary<string, int>());

    public Task<IReadOnlyList<IssuedReel>> GetIssuedReelsForLotAsync(string lotNo, string side, int line, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<IssuedReel>>(Array.Empty<IssuedReel>());

    public Task<IReadOnlyList<IssuedReel>> GetReelsAtLineAsync(int line, int daysBack, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<IssuedReel>>(Array.Empty<IssuedReel>());

    public Task<LotOrder?> GetNextDeliveryLotAsync(string model, string side, int line, CancellationToken ct = default) =>
        Task.FromResult<LotOrder?>(null);

    public Task<IReadOnlyList<LotOrder>> GetLotOptionsAsync(string model, IReadOnlyList<string>? reopenLots = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<LotOrder>>(Array.Empty<LotOrder>());

    public Task<int> InsertProductionCountAsync(ProductionCountEntry row, CancellationToken ct = default) =>
        Task.FromResult(0);

    public Task<IReadOnlyList<ProductionRun>> GetDailyProductionAsync(string line, string date, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ProductionRun>>(Array.Empty<ProductionRun>());

    public Task<int> GetProducedBoardsForLotAsync(string lotNo, string side, int line, CancellationToken ct = default) =>
        Task.FromResult(0);

    public Task<IReadOnlyList<BomFeederUse>> GetBomUsageAsync(string model, string side, int line, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<BomFeederUse>>(Array.Empty<BomFeederUse>());

    public Task<int> UpdateReelQtyAsync(string partUid, string partNumber, int quantity, CancellationToken ct = default) =>
        Task.FromResult(0);

    public Task<IReadOnlyList<Pvs.Core.Inventory.UpcomingLot>> GetUpcomingLotsAsync(int horizonDays, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Pvs.Core.Inventory.UpcomingLot>>(Array.Empty<Pvs.Core.Inventory.UpcomingLot>());

    public Task<IReadOnlyList<Pvs.Core.Inventory.BomUsageRow>> GetBomUsageForModelsAsync(IReadOnlyCollection<string> models, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Pvs.Core.Inventory.BomUsageRow>>(Array.Empty<Pvs.Core.Inventory.BomUsageRow>());

    public Task<IReadOnlyList<Pvs.Core.Inventory.PartStock>> GetPartStockAsync(IReadOnlyCollection<string> parts, int reelDaysBack, int freshHours, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Pvs.Core.Inventory.PartStock>>(Array.Empty<Pvs.Core.Inventory.PartStock>());

    public Task<IReadOnlyList<LineReelTracking>> GetReelTrackingByLineAsync(int reelDaysBack, int freshHours, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<LineReelTracking>>(Array.Empty<LineReelTracking>());

    public Task<string?> GetPartRankAsync(string partNumber, CancellationToken ct = default) => Task.FromResult<string?>(null);
    public Task<int> RecordConsumedReelAsync(ConsumedReel reel, CancellationToken ct = default) => Task.FromResult(0);
    public Task<ConsumedReel?> GetActiveConsumedReelAsync(string uid, CancellationToken ct = default) => Task.FromResult<ConsumedReel?>(null);
    public Task<int> MarkConsumedRestoredAsync(string uid, CancellationToken ct = default) => Task.FromResult(0);
    public Task<int> AddPartAttritionAsync(string partNumber, int pcsDelta, int reelDelta, CancellationToken ct = default) => Task.FromResult(0);
}
