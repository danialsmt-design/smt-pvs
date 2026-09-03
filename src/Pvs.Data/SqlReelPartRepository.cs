using Microsoft.Data.SqlClient;
using Pvs.Core.Data;
using Pvs.Core.Feeders;
using Pvs.Core.People;

namespace Pvs.Data;

/// <summary>
/// Read-only SQL Server implementation of <see cref="IReelPartRepository"/> over ReelPart-New.
/// Every query is parameterised. All SQL here is SELECT-only — this type never writes.
/// Queries validated against live data (2026-07-23).
/// </summary>
public sealed partial class SqlReelPartRepository : IReelPartRepository
{
    private readonly string _connectionString;          // primary DB link
    private readonly string? _fallback;                  // second route to the SAME DB (e.g. Tailscale IP); null = none
    private volatile bool _preferFallback;               // sticky: after a fail-over, try the working link first

    public SqlReelPartRepository(string connectionString, string? fallbackConnectionString = null)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _fallback = string.IsNullOrWhiteSpace(fallbackConnectionString) ? null : fallbackConnectionString;
    }

    /// <summary>Opens a connection over the preferred link; on a connect failure, fails over to the second link
    /// (if configured) and sticks to whichever answered. Both down → the error propagates (the caller retries; a
    /// production write just stays queued on the line). Single-link config behaves exactly as before.</summary>
    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        string first = _preferFallback && _fallback is not null ? _fallback : _connectionString;
        string? second = _fallback is null ? null : (_preferFallback ? _connectionString : _fallback);
        try
        {
            var cn = new SqlConnection(first);
            await cn.OpenAsync(ct);
            return cn;
        }
        catch when (second is not null && !ct.IsCancellationRequested)
        {
            var cn = new SqlConnection(second);
            await cn.OpenAsync(ct);          // if this also throws, both links are down — propagate
            _preferFallback = !_preferFallback;   // stick to the link that just worked
            return cn;
        }
    }

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand("SELECT 1", cn);
        var r = await cmd.ExecuteScalarAsync(ct);
        return r is not null;
    }

    public async Task<IReadOnlyList<Product>> GetProductsAsync(CancellationToken ct = default)
    {
        const string sql = "SELECT ProductID, LTRIM(RTRIM(ISNULL(ProductName,''))) AS ProductName FROM Products ORDER BY ProductName";
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, cn);
        var list = new List<Product>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new Product(r.GetInt32(0), r.GetString(1)));
        return list;
    }

    public async Task<IReadOnlyList<FeederAssignment>> GetFeederMapAsync(int productId, string side, int line, CancellationToken ct = default)
    {
        // ProductBOM.Side uses the literal 'A Side' / 'B Side' / 'Full' strings.
        // Resolve the running side ("A"/"B") to the set of BOM side values it should include.
        // The IN-list is a fixed whitelist chosen by a switch — never interpolated user input.
        string sideList = side?.Trim().ToUpperInvariant() switch
        {
            "A" => "'A Side','Full'",
            "B" => "'B Side','Full'",
            _   => "'A Side','B Side','Full'"
        };

        // A model's BOM holds every line's feeders, distinguished by ProductBOM.Line. Filter to this
        // line's layout so we never mix another line's feeders in. line <= 0 means "all lines".
        string lineClause = line > 0 ? " AND Line = @line" : "";

        // Machine and Quantity are NULL on the unassigned/hand-placed rows, so coalesce everything.
        string sql =
            $@"SELECT ISNULL(Machine,0) AS Machine, ISNULL(SupplyPosition,'') AS SupplyPosition,
                      ISNULL(PartNumber,'') AS PartNumber, ISNULL(Quantity,0) AS Quantity
               FROM ProductBOM
               WHERE ProductID = @pid AND Side IN ({sideList}){lineClause}
               ORDER BY Machine, SupplyPosition";

        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.AddWithValue("@pid", productId);
        if (line > 0) cmd.Parameters.AddWithValue("@line", line);

        var list = new List<FeederAssignment>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            int machine = r.GetInt32(0);
            var pos = SupplyPosition.Parse(r.GetString(1));
            string part = r.GetString(2).Trim();
            int qty = r.GetInt32(3);
            list.Add(new FeederAssignment(machine, pos, part, qty));
        }
        return list;
    }

    public async Task<Badge?> FindBadgeAsync(string badgeUid, CancellationToken ct = default)
    {
        EnsureBadgeCacheLoaded();
        const string sql =
            @"SELECT LTRIM(RTRIM(ISNULL(UserID,''))) AS UserID, LTRIM(RTRIM(ISNULL(UserName,''))) AS UserName, ISNULL(AccessLevel,'') AS AccessLevel
              FROM Users WHERE LTRIM(RTRIM(UserUID)) = LTRIM(RTRIM(@uid))";
        try
        {
            await using var cn = await OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, cn);
            cmd.Parameters.AddWithValue("@uid", badgeUid ?? string.Empty);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;   // DB is up, badge genuinely unknown — do NOT fall back
            var badge = new Badge(r.GetString(0), r.GetString(1), r.GetString(2));
            CacheBadge(badgeUid, badge);               // remember it so auth survives a later DB outage
            return badge;
        }
        catch (Exception)
        {
            // Both DB routes are down. Fall back to the LOCAL badge cache so operator/supervisor auth — and the
            // parts-exchange it gates — is never blocked by a DB outage. Null only if this badge was never seen
            // while the DB was up (the periodic PreloadBadgesAsync keeps the cache complete).
            return CachedBadge(badgeUid);
        }
    }

    public async Task<ReelInfo?> FindReelAsync(string partUid, string partNumber, CancellationToken ct = default)
    {
        // Read RemainingQty (per-reel), never Qty. Most-recent stock-in row for this UID+part.
        const string sql =
            @"SELECT TOP 1 LTRIM(RTRIM(PartUID)) AS PartUID, LTRIM(RTRIM(PartNumber)) AS PartNumber,
                     ISNULL(RemainingQty, 0) AS RemainingQty, ISNULL(Status, 0) AS Status
              FROM StockIns
              WHERE LTRIM(RTRIM(PartUID)) = LTRIM(RTRIM(@uid))
                AND LTRIM(RTRIM(PartNumber)) = LTRIM(RTRIM(@pn))
              ORDER BY Date DESC, Time DESC";
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.AddWithValue("@uid", partUid ?? string.Empty);
        cmd.Parameters.AddWithValue("@pn", partNumber ?? string.Empty);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new ReelInfo(r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetBoolean(3));
    }

    public async Task<int?> FindStockOutQtyAsync(string partUid, string partNumber, CancellationToken ct = default)
    {
        // Most-recent stock-out quantity for this reel UID. StockOuts.ID is an identity, so ID DESC = latest.
        const string sql =
            @"SELECT TOP 1 Quantity FROM StockOuts
              WHERE LTRIM(RTRIM(PartUID)) = LTRIM(RTRIM(@uid))
              ORDER BY ID DESC";
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.AddWithValue("@uid", partUid ?? string.Empty);
        var v = await cmd.ExecuteScalarAsync(ct);
        return v is null or DBNull ? (int?)null : Convert.ToInt32(v);
    }

    public async Task<ReelInfo?> FindStockOutReelAsync(string partUid, CancellationToken ct = default)
    {
        const string sql =
            @"SELECT TOP 1 LTRIM(RTRIM(PartUID)) AS PartUID, LTRIM(RTRIM(PartNumber)) AS PartNumber,
                     ISNULL(Quantity,0) AS Quantity
              FROM StockOuts
              WHERE LTRIM(RTRIM(PartUID)) = LTRIM(RTRIM(@uid))
              ORDER BY ID DESC";
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.AddWithValue("@uid", partUid ?? string.Empty);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new ReelInfo(r.GetString(0), r.GetString(1), r.GetInt32(2), true);
    }

    public async Task<CurrentLot?> GetCurrentLotAsync(int line, CancellationToken ct = default)
    {
        // Most-recent production entry for the line = what's running now (model + side).
        const string sql =
            @"SELECT TOP 1 LTRIM(RTRIM(ISNULL(Model,''))) AS Model, LTRIM(RTRIM(ISNULL(Side,''))) AS Side,
                     LTRIM(RTRIM(ISNULL(LotNo,''))) AS LotNo
              FROM DailyProductionCount
              WHERE LTRIM(RTRIM(Line)) = CAST(@line AS nvarchar(10))
              ORDER BY ID DESC";
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.AddWithValue("@line", line);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        string model = r.GetString(0);
        return string.IsNullOrWhiteSpace(model) ? null : new CurrentLot(model, r.GetString(1), r.GetString(2));
    }

    public async Task<int> GetProducedBoardsForLotAsync(string lotNo, string side, int line, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(lotNo)) return 0;
        string s = (side ?? "").Trim();
        // DailyProductionCount stores the lot in LotNo and Side as 'A'/'B'. Match side by first letter (empty = all).
        string sql =
            @"SELECT ISNULL(SUM(ISNULL(Quantity,0)),0)
              FROM DailyProductionCount
              WHERE LTRIM(RTRIM(ISNULL(LotNo,''))) = LTRIM(RTRIM(@lot))
                AND LTRIM(RTRIM(Line)) = CAST(@line AS nvarchar(10))" +
            (s.Length == 0 ? "" : " AND UPPER(LEFT(LTRIM(ISNULL(Side,'')),1)) = UPPER(@side)");
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.AddWithValue("@lot", lotNo.Trim());
        cmd.Parameters.AddWithValue("@line", line);
        if (s.Length > 0) cmd.Parameters.AddWithValue("@side", s.Substring(0, 1));
        var v = await cmd.ExecuteScalarAsync(ct);
        return v is null || v is DBNull ? 0 : Convert.ToInt32(v);
    }

    public async Task<IReadOnlyList<ProductionRun>> GetDailyProductionAsync(string line, string date, CancellationToken ct = default)
    {
        // DailyProductionCount stores Date as 'yyyy-MM-dd' and Line as a plain string ("1"). StartTime =
        // when the run began, Time = when the count was logged (run end). Oldest first.
        const string sql =
            @"SELECT LTRIM(RTRIM(ISNULL(Model,''))) AS Model, LTRIM(RTRIM(ISNULL(Side,''))) AS Side,
                     ISNULL(Quantity,0) AS Quantity, ISNULL(ExcessQuantity,0) AS ExcessQuantity,
                     LTRIM(RTRIM(ISNULL(LotNo,''))) AS LotNo, LTRIM(RTRIM(ISNULL(Shift,''))) AS Shift,
                     LTRIM(RTRIM(ISNULL(StartTime,''))) AS StartTime, LTRIM(RTRIM(ISNULL(Time,''))) AS EndTime
              FROM DailyProductionCount
              WHERE LTRIM(RTRIM(Line)) = LTRIM(RTRIM(@line)) AND LTRIM(RTRIM(Date)) = LTRIM(RTRIM(@date))
              ORDER BY ID";
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.AddWithValue("@line", line ?? string.Empty);
        cmd.Parameters.AddWithValue("@date", date ?? string.Empty);
        var list = new List<ProductionRun>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new ProductionRun(r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3),
                r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7)));
        return list;
    }

    public async Task<IReadOnlyList<BomFeederUse>> GetBomUsageAsync(string model, string side, int line, CancellationToken ct = default)
    {
        // Same 'A Side'/'B Side'/'Full' whitelist as GetFeederMapAsync — fixed, never interpolated input.
        string sideList = side?.Trim().ToUpperInvariant() switch
        {
            "A" => "'A Side','Full'",
            "B" => "'B Side','Full'",
            _   => "'A Side','B Side','Full'"
        };
        string lineClause = line > 0 ? " AND b.Line = @line" : "";
        string sql =
            $@"SELECT ISNULL(b.Machine,0) AS Machine, ISNULL(b.SupplyPosition,'') AS SupplyPosition,
                      ISNULL(b.PartNumber,'') AS PartNumber, ISNULL(b.Quantity,0) AS PerBoard
               FROM ProductBOM b
               JOIN Products p ON p.ProductID = b.ProductID
               WHERE LTRIM(RTRIM(p.ProductName)) = LTRIM(RTRIM(@model)) AND b.Side IN ({sideList}){lineClause}
               ORDER BY b.Machine, b.SupplyPosition";
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.AddWithValue("@model", model ?? string.Empty);
        if (line > 0) cmd.Parameters.AddWithValue("@line", line);
        var list = new List<BomFeederUse>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new BomFeederUse(r.GetInt32(0), r.GetString(1).Trim(), r.GetString(2).Trim(), r.GetInt32(3)));
        return list;
    }

    public async Task<int?> GetLotTargetAsync(string lotNo, CancellationToken ct = default)
    {
        // Lot/PO target from DeliveryDocuments (LotNo == PONumber). Prefer RevisedQty when set, else Quantity.
        const string sql =
            @"SELECT TOP 1 CASE WHEN ISNULL(RevisedQty,0) > 0 THEN RevisedQty ELSE ISNULL(Quantity,0) END
              FROM DeliveryDocuments
              WHERE LTRIM(RTRIM(PONumber)) = LTRIM(RTRIM(@lot))
              ORDER BY DocID DESC";
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.AddWithValue("@lot", lotNo ?? string.Empty);
        var v = await cmd.ExecuteScalarAsync(ct);
        return v is null or DBNull ? (int?)null : Convert.ToInt32(v);
    }

    public async Task<LotSizeRow?> GetLotSizeAsync(string lotNo, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(lotNo)) return null;
        // RAW columns only — which one is the real lot size is LotSizeResolver's decision, not SQL's, because a
        // DELIVERED order carries its real figure in DeliveredQty with RevisedQty zeroed. Same row selection as
        // GetLotTargetAsync (latest DocID for the PONumber) so the two can never disagree about WHICH row.
        // DeliveredQty is queried by name and, if this database doesn't have the column, the query is retried
        // without it (SQL error 207 = invalid column name) rather than taking the whole check down.
        const string withDelivered =
            @"SELECT TOP 1 LTRIM(RTRIM(ISNULL(PONumber,''))), LTRIM(RTRIM(ISNULL(Status,''))),
                     Quantity, RevisedQty, DeliveredQty
              FROM DeliveryDocuments
              WHERE LTRIM(RTRIM(PONumber)) = LTRIM(RTRIM(@lot))
              ORDER BY DocID DESC";
        const string withoutDelivered =
            @"SELECT TOP 1 LTRIM(RTRIM(ISNULL(PONumber,''))), LTRIM(RTRIM(ISNULL(Status,''))),
                     Quantity, RevisedQty, NULL
              FROM DeliveryDocuments
              WHERE LTRIM(RTRIM(PONumber)) = LTRIM(RTRIM(@lot))
              ORDER BY DocID DESC";
        try { return await ReadLotSizeAsync(withDelivered, lotNo, ct); }
        catch (SqlException ex) when (ex.Number == 207) { return await ReadLotSizeAsync(withoutDelivered, lotNo, ct); }
    }

    private async Task<LotSizeRow?> ReadLotSizeAsync(string sql, string lotNo, CancellationToken ct)
    {
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.AddWithValue("@lot", lotNo.Trim());
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new LotSizeRow(r.GetString(0), r.GetString(1), NullableInt(r, 2), NullableInt(r, 3), NullableInt(r, 4));
    }

    private static int? NullableInt(SqlDataReader r, int i) => r.IsDBNull(i) ? null : Convert.ToInt32(r.GetValue(i));

    public async Task<LotBoardTally> GetLotBoardTallyAsync(string lotNo, string side, int line, CancellationToken ct = default)
    {
        var empty = new LotBoardTally(0, 0, 0, null, null);
        if (string.IsNullOrWhiteSpace(lotNo)) return empty;
        string s = (side ?? "").Trim();
        // Same lot/side/line matching as GetProducedBoardsForLotAsync, but split by WHO wrote each row: PVS's own
        // rows carry SenderIp='PVS'; the operator's application sends a real IP (e.g. 192.168.0.122) and a person's
        // name. Anything that isn't literally 'PVS' is counted as the operator's — an unknown writer must not be
        // silently folded into PVS's side of the comparison.
        string sql =
            @"SELECT ISNULL(SUM(CASE WHEN IsPvs = 1 THEN Qty ELSE 0 END),0)  AS PvsBoards,
                     ISNULL(SUM(CASE WHEN IsPvs = 0 THEN Qty ELSE 0 END),0)  AS OperatorBoards,
                     ISNULL(SUM(CASE WHEN IsPvs = 0 THEN 1   ELSE 0 END),0)  AS OperatorRows,
                     MAX(CASE WHEN IsPvs = 0 THEN Op ELSE NULL END)          AS LastOperator,
                     MAX(CASE WHEN IsPvs = 0 THEN Ip ELSE NULL END)          AS LastSenderIp
              FROM (
                SELECT ISNULL(Quantity,0) AS Qty,
                       CASE WHEN UPPER(LTRIM(RTRIM(ISNULL(SenderIp,'')))) = 'PVS' THEN 1 ELSE 0 END AS IsPvs,
                       LTRIM(RTRIM(ISNULL(OperatorName,''))) AS Op,
                       LTRIM(RTRIM(ISNULL(SenderIp,'')))     AS Ip
                FROM DailyProductionCount
                WHERE LTRIM(RTRIM(ISNULL(LotNo,''))) = LTRIM(RTRIM(@lot))
                  AND LTRIM(RTRIM(Line)) = CAST(@line AS nvarchar(10))" +
            (s.Length == 0 ? "" : " AND UPPER(LEFT(LTRIM(ISNULL(Side,'')),1)) = UPPER(@side)") + ") t";
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.AddWithValue("@lot", lotNo.Trim());
        cmd.Parameters.AddWithValue("@line", line);
        if (s.Length > 0) cmd.Parameters.AddWithValue("@side", s.Substring(0, 1));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return empty;
        return new LotBoardTally(
            Convert.ToInt32(r.GetValue(1)), Convert.ToInt32(r.GetValue(0)), Convert.ToInt32(r.GetValue(2)),
            r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4));
    }

    public async Task<IReadOnlyDictionary<string, int>> GetIssuedForLotAsync(string lotNo, string side, int line, CancellationToken ct = default)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(lotNo)) return result;
        // StockOuts.Model holds the lot/PO number (some rows join two lots with " | ", so match with LIKE).
        // Side resolves like the BOM: a single-sided 'Full' reel counts for either running side.
        // The IN-list is a fixed whitelist chosen by a switch — never interpolated user input.
        string sideList = side?.Trim().ToUpperInvariant() switch
        {
            "A" => "'A Side','Full'",
            "B" => "'B Side','Full'",
            _   => "'A Side','B Side','Full'"
        };
        string sql =
            $@"SELECT LTRIM(RTRIM(ISNULL(PartNumber,''))) AS Part, SUM(ISNULL(Quantity,0)) AS Issued
               FROM StockOuts
               WHERE Model LIKE @lot
                 AND LTRIM(RTRIM(ISNULL(Line,''))) = CAST(@line AS nvarchar(10))
                 AND LTRIM(RTRIM(ISNULL(Side,''))) IN ({sideList})
               GROUP BY LTRIM(RTRIM(ISNULL(PartNumber,'')))";
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.AddWithValue("@lot", "%" + (lotNo ?? string.Empty).Trim() + "%");
        cmd.Parameters.AddWithValue("@line", line);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            string part = r.GetString(0).Trim();
            int issued = r.IsDBNull(1) ? 0 : Convert.ToInt32(r.GetValue(1));
            if (part.Length > 0) result[part] = issued;
        }
        return result;
    }

    public async Task<IReadOnlyList<IssuedReel>> GetIssuedReelsForLotAsync(string lotNo, string side, int line, CancellationToken ct = default)
    {
        var list = new List<IssuedReel>();
        if (string.IsNullOrWhiteSpace(lotNo)) return list;
        string sideList = side?.Trim().ToUpperInvariant() switch
        {
            "A" => "'A Side','Full'",
            "B" => "'B Side','Full'",
            _   => "'A Side','B Side','Full'"
        };
        // One row per issued reel (PartUID) for this lot/side/line. StockOuts.Model holds the lot no. (LIKE handles
        // the occasional " | "-joined two-lot rows). Take the latest qty per UID (ID DESC) in case a reel re-issued.
        string sql =
            $@"SELECT Part, Uid, Qty FROM (
                 SELECT LTRIM(RTRIM(ISNULL(PartNumber,''))) AS Part,
                        LTRIM(RTRIM(ISNULL(PartUID,'')))    AS Uid,
                        ISNULL(Quantity,0)                  AS Qty,
                        ROW_NUMBER() OVER (PARTITION BY LTRIM(RTRIM(ISNULL(PartUID,''))) ORDER BY ID DESC) AS rn
                 FROM StockOuts
                 WHERE Model LIKE @lot
                   AND LTRIM(RTRIM(ISNULL(Line,''))) = CAST(@line AS nvarchar(10))
                   AND LTRIM(RTRIM(ISNULL(Side,''))) IN ({sideList})
               ) t WHERE rn = 1 AND Uid <> ''";
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.AddWithValue("@lot", "%" + (lotNo ?? string.Empty).Trim() + "%");
        cmd.Parameters.AddWithValue("@line", line);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new IssuedReel(r.GetString(0).Trim(), r.GetString(1).Trim(), r.IsDBNull(2) ? 0 : Convert.ToInt32(r.GetValue(2))));
        return list;
    }

    public async Task<IReadOnlyList<IssuedReel>> GetReelsAtLineAsync(int line, int daysBack, CancellationToken ct = default)
    {
        var list = new List<IssuedReel>();
        if (daysBack <= 0) daysBack = 30;
        // Every reel issued to this line recently that still carries stock — ANY lot, ANY side. A reel belongs
        // to the line it was issued to; the lot it was drawn against is bookkeeping, and lots turn over far
        // faster than the physical rack does.
        // StockOuts.Date is nvarchar 'dd-MM-yyyy' (style 105); TRY_CONVERT yields NULL on the malformed rows
        // rather than failing the query, and those are simply excluded.
        // Latest row per UID (ID DESC) because a reel is re-written each time its balance syncs.
        const string sql =
            @"SELECT Part, Uid, Qty FROM (
                 SELECT LTRIM(RTRIM(ISNULL(PartNumber,''))) AS Part,
                        LTRIM(RTRIM(ISNULL(PartUID,'')))    AS Uid,
                        ISNULL(Quantity,0)                  AS Qty,
                        ROW_NUMBER() OVER (PARTITION BY LTRIM(RTRIM(ISNULL(PartUID,''))) ORDER BY ID DESC) AS rn
                 FROM StockOuts
                 WHERE LTRIM(RTRIM(ISNULL(Line,''))) = CAST(@line AS nvarchar(10))
                   AND TRY_CONVERT(date, Date, 105) >= DATEADD(day, -@days, CAST(GETDATE() AS date))
               ) t WHERE rn = 1 AND Uid <> '' AND Qty > 0";
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.AddWithValue("@line", line);
        cmd.Parameters.AddWithValue("@days", daysBack);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new IssuedReel(r.GetString(0).Trim(), r.GetString(1).Trim(), r.IsDBNull(2) ? 0 : Convert.ToInt32(r.GetValue(2))));
        return list;
    }

    public async Task<LotOrder?> GetNextDeliveryLotAsync(string model, string side, int line, CancellationToken ct = default)
    {
        // Next lot to run = the earliest Planned (un-delivered) DeliveryDocuments order (PONumber == LotNo)
        // whose delivery date is LATER than the latest lot already produced for this model/side/line.
        // This skips lots already produced AND lots dated behind the current production point, so it returns
        // the genuine "next" in the production sequence rather than the earliest un-delivered order overall.
        // The production frontier comes from DailyProductionCount (which lots this side/line has run);
        // if nothing is produced yet the frontier is '1900-01-01', giving the earliest upcoming order.
        const string sql =
            @"SELECT TOP 1 LTRIM(RTRIM(ISNULL(dd.PONumber,''))) AS PONumber,
                     CASE WHEN ISNULL(dd.RevisedQty,0) > 0 THEN dd.RevisedQty ELSE ISNULL(dd.Quantity,0) END AS Target,
                     dd.DeliveryDate
              FROM DeliveryDocuments dd
              WHERE LTRIM(RTRIM(ISNULL(dd.ProductName,''))) = LTRIM(RTRIM(@model))
                AND (dd.Status IS NULL OR LTRIM(RTRIM(dd.Status)) <> 'Delivered')
                AND dd.DeliveryDate > ISNULL((
                    SELECT MAX(d2.DeliveryDate) FROM DeliveryDocuments d2
                    WHERE d2.PONumber IN (
                        SELECT DISTINCT LTRIM(RTRIM(LotNo)) FROM DailyProductionCount
                        WHERE LTRIM(RTRIM(ISNULL(Model,''))) = LTRIM(RTRIM(@model))
                          AND LTRIM(RTRIM(ISNULL(Side,''))) = LTRIM(RTRIM(@side))
                          AND LTRIM(RTRIM(ISNULL(Line,''))) = CAST(@line AS nvarchar(10)))
                ), '1900-01-01')
              ORDER BY dd.DeliveryDate ASC, dd.DocID ASC";
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.AddWithValue("@model", model ?? string.Empty);
        cmd.Parameters.AddWithValue("@side", string.IsNullOrWhiteSpace(side) ? string.Empty : side.Trim());
        cmd.Parameters.AddWithValue("@line", line);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        string po = r.GetString(0);
        if (string.IsNullOrWhiteSpace(po)) return null;
        int target = r.GetInt32(1);
        DateTime? dd = await r.IsDBNullAsync(2, ct) ? null : r.GetDateTime(2);
        return new LotOrder(po, target, dd);
    }

    public async Task<IReadOnlyList<LotOrder>> GetLotOptionsAsync(string model, IReadOnlyList<string>? reopenLots = null, CancellationToken ct = default)
    {
        // Candidate lots for the manual dropdown: Planned (un-delivered) delivery orders for the model,
        // from a week back through the future, oldest first. Covers the currently-running lot + the ones ahead.
        // reopenLots FORCE specific carry-over POs in past that 7-day window (e.g. a July lot whose B-side was
        // never run) — still gated to the selected model + a non-Delivered status, so it can't surface anything else.
        var reopen = (reopenLots ?? Array.Empty<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct().ToList();
        string reopenClause = reopen.Count == 0 ? ""
            : " OR LTRIM(RTRIM(ISNULL(PONumber,''))) IN (" + string.Join(",", reopen.Select((_, i) => "@r" + i)) + ")";
        string sql =
            @"SELECT LTRIM(RTRIM(ISNULL(PONumber,''))) AS PONumber,
                     CASE WHEN ISNULL(RevisedQty,0) > 0 THEN RevisedQty ELSE ISNULL(Quantity,0) END AS Target,
                     DeliveryDate
              FROM DeliveryDocuments
              WHERE LTRIM(RTRIM(ISNULL(ProductName,''))) = LTRIM(RTRIM(@model))
                AND (Status IS NULL OR LTRIM(RTRIM(Status)) <> 'Delivered')
                AND (DeliveryDate >= DATEADD(day, -7, CAST(GETDATE() AS date))" + reopenClause + @")
              ORDER BY DeliveryDate ASC, DocID ASC";
        var list = new List<LotOrder>();
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.AddWithValue("@model", model ?? string.Empty);
        for (int i = 0; i < reopen.Count; i++) cmd.Parameters.AddWithValue("@r" + i, reopen[i]);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            string po = r.GetString(0);
            if (string.IsNullOrWhiteSpace(po)) continue;
            int target = r.GetInt32(1);
            DateTime? dd = await r.IsDBNullAsync(2, ct) ? null : r.GetDateTime(2);
            list.Add(new LotOrder(po, target, dd));
        }
        return list;
    }

    public async Task<int> InsertProductionCountAsync(ProductionCountEntry row, CancellationToken ct = default)
    {
        // Column list matches DailyProductionCount exactly; Quantity is a child-board count. ID is identity,
        // ExcessQuantity fixed 0, EmployeeId blank. OperatorName/SenderIp/SessionID tag the row as PVS-written.
        const string sql =
            @"INSERT INTO DailyProductionCount
                (Date, StartTime, Time, Model, Side, Quantity, Line, OperatorName, LotNo, Shift, EmployeeId, SenderIp, SessionID, ExcessQuantity)
              VALUES (@date, @start, @end, @model, @side, @qty, @line, @op, @lot, @shift, '', @ip, @sid, 0)";
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.AddWithValue("@date", row.Date ?? "");
        cmd.Parameters.AddWithValue("@start", row.StartTime ?? "");
        cmd.Parameters.AddWithValue("@end", row.EndTime ?? "");
        cmd.Parameters.AddWithValue("@model", row.Model ?? "");
        cmd.Parameters.AddWithValue("@side", row.Side ?? "");
        cmd.Parameters.AddWithValue("@qty", row.Quantity);
        cmd.Parameters.AddWithValue("@line", row.Line ?? "");
        cmd.Parameters.AddWithValue("@op", row.OperatorName ?? "");
        cmd.Parameters.AddWithValue("@lot", row.LotNo ?? "");
        cmd.Parameters.AddWithValue("@shift", row.Shift ?? "");
        cmd.Parameters.AddWithValue("@ip", row.SenderIp ?? "");
        cmd.Parameters.AddWithValue("@sid", row.SessionId ?? "");
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> UpdateReelQtyAsync(string partUid, string partNumber, int quantity, CancellationToken ct = default)
    {
        // Update the most-recent stock-out row for this reel UID. StockOuts.ID is an identity, so the
        // TOP-1 ID DESC subquery pins exactly the row FindStockOutQtyAsync reads. One row (0 if none).
        const string sql =
            @"UPDATE StockOuts SET Quantity = @q
              WHERE ID = (SELECT TOP 1 ID FROM StockOuts
                          WHERE LTRIM(RTRIM(PartUID)) = LTRIM(RTRIM(@uid))
                          ORDER BY ID DESC);";
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.AddWithValue("@uid", partUid ?? string.Empty);
        cmd.Parameters.AddWithValue("@q", quantity);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<string?> GetPartRankAsync(string partNumber, CancellationToken ct = default)
    {
        const string sql = "SELECT TOP 1 LTRIM(RTRIM(Rank)) FROM PartRanks WHERE LTRIM(RTRIM(PartNumber)) = LTRIM(RTRIM(@p))";
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.AddWithValue("@p", partNumber ?? string.Empty);
        var r = await cmd.ExecuteScalarAsync(ct);
        var s = r as string;
        return string.IsNullOrWhiteSpace(s) ? null : s.Trim().ToUpperInvariant();
    }

    public async Task<int> RecordConsumedReelAsync(ConsumedReel reel, CancellationToken ct = default)
    {
        // Idempotent: only insert if there is no OPEN (Restored=0) record for this UID already.
        const string sql =
            @"IF NOT EXISTS (SELECT 1 FROM ConsumedReels WHERE LTRIM(RTRIM(Uid)) = LTRIM(RTRIM(@uid)) AND Restored = 0)
              INSERT INTO ConsumedReels (Uid, PartNumber, Rank, RemainingAtRetire, Line, LotNo, RetiredAt, Restored)
              VALUES (@uid, @part, @rank, @rem, @line, @lot, GETDATE(), 0);";
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.AddWithValue("@uid", reel.Uid ?? string.Empty);
        cmd.Parameters.AddWithValue("@part", reel.PartNumber ?? string.Empty);
        cmd.Parameters.AddWithValue("@rank", reel.Rank ?? string.Empty);
        cmd.Parameters.AddWithValue("@rem", reel.RemainingAtRetire);
        cmd.Parameters.AddWithValue("@line", reel.Line ?? string.Empty);
        cmd.Parameters.AddWithValue("@lot", reel.LotNo ?? string.Empty);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<ConsumedReel?> GetActiveConsumedReelAsync(string uid, CancellationToken ct = default)
    {
        const string sql =
            @"SELECT TOP 1 LTRIM(RTRIM(Uid)), LTRIM(RTRIM(PartNumber)), LTRIM(RTRIM(ISNULL(Rank,''))),
                     ISNULL(RemainingAtRetire,0), ISNULL(Line,''), ISNULL(LotNo,'')
              FROM ConsumedReels WHERE LTRIM(RTRIM(Uid)) = LTRIM(RTRIM(@uid)) AND Restored = 0 ORDER BY ID DESC;";
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.AddWithValue("@uid", uid ?? string.Empty);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new ConsumedReel(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetString(4), r.GetString(5));
    }

    public async Task<int> MarkConsumedRestoredAsync(string uid, CancellationToken ct = default)
    {
        const string sql = @"UPDATE ConsumedReels SET Restored = 1, RestoredAt = GETDATE()
                             WHERE LTRIM(RTRIM(Uid)) = LTRIM(RTRIM(@uid)) AND Restored = 0;";
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.AddWithValue("@uid", uid ?? string.Empty);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> AddPartAttritionAsync(string partNumber, int pcsDelta, int reelDelta, CancellationToken ct = default)
    {
        const string sql =
            @"MERGE dbo.PartAttrition AS t
              USING (SELECT @p AS PartNumber) AS s ON LTRIM(RTRIM(t.PartNumber)) = LTRIM(RTRIM(s.PartNumber))
              WHEN MATCHED THEN UPDATE SET AttritionPcs = AttritionPcs + @pcs, Reels = Reels + @reels, LastAt = GETDATE()
              WHEN NOT MATCHED THEN INSERT (PartNumber, AttritionPcs, Reels, LastAt) VALUES (@p, @pcs, @reels, GETDATE());";
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.AddWithValue("@p", partNumber ?? string.Empty);
        cmd.Parameters.AddWithValue("@pcs", pcsDelta);
        cmd.Parameters.AddWithValue("@reels", reelDelta);
        return await cmd.ExecuteNonQueryAsync(ct);
    }
}
