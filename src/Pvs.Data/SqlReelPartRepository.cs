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
public sealed class SqlReelPartRepository : IReelPartRepository
{
    private readonly string _connectionString;

    public SqlReelPartRepository(string connectionString)
        => _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(ct);
        return cn;
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
        const string sql =
            @"SELECT LTRIM(RTRIM(ISNULL(UserID,''))) AS UserID, LTRIM(RTRIM(ISNULL(UserName,''))) AS UserName, ISNULL(AccessLevel,'') AS AccessLevel
              FROM Users WHERE LTRIM(RTRIM(UserUID)) = LTRIM(RTRIM(@uid))";
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.AddWithValue("@uid", badgeUid ?? string.Empty);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new Badge(r.GetString(0), r.GetString(1), r.GetString(2));
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

    public async Task<IReadOnlyList<LotOrder>> GetLotOptionsAsync(string model, CancellationToken ct = default)
    {
        // Candidate lots for the manual dropdown: Planned (un-delivered) delivery orders for the model,
        // from a week back through the future, oldest first. Covers the currently-running lot + the ones ahead.
        const string sql =
            @"SELECT LTRIM(RTRIM(ISNULL(PONumber,''))) AS PONumber,
                     CASE WHEN ISNULL(RevisedQty,0) > 0 THEN RevisedQty ELSE ISNULL(Quantity,0) END AS Target,
                     DeliveryDate
              FROM DeliveryDocuments
              WHERE LTRIM(RTRIM(ISNULL(ProductName,''))) = LTRIM(RTRIM(@model))
                AND (Status IS NULL OR LTRIM(RTRIM(Status)) <> 'Delivered')
                AND DeliveryDate >= DATEADD(day, -7, CAST(GETDATE() AS date))
              ORDER BY DeliveryDate ASC, DocID ASC";
        var list = new List<LotOrder>();
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.AddWithValue("@model", model ?? string.Empty);
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
}
