using System.Text;
using Microsoft.Data.SqlClient;
using Pvs.Core.Data;
using Pvs.Core.Inventory;
using Pvs.Core.Runtime;

namespace Pvs.Data;

/// <summary>
/// The four SELECT-only queries behind the forward-looking parts-shortage monitor. Kept apart from the rest of
/// the repository because they are the only ones that read the ORDER BOOK rather than the shop floor, and
/// because each one carries a data quirk that took live verification to pin down (see the comments).
/// <para>
/// Budget: this whole set runs ONCE A DAY. The parts-control PC is a memory-starved 4 GB box, so every query
/// here is bounded — by date horizon, by a parameterised part/model list, or by an aggregate — and none of
/// them pulls a whole table.
/// </para>
/// </summary>
public sealed partial class SqlReelPartRepository
{
    /// <summary>Most parameters we will put in one IN-list. SQL Server's ceiling is 2100; stay well clear.</summary>
    private const int ParamChunk = 400;

    public async Task<IReadOnlyList<UpcomingLot>> GetUpcomingLotsAsync(int horizonDays, CancellationToken ct = default)
    {
        if (horizonDays <= 0) horizonDays = 21;

        // Boards already built for a lot come from DailyProductionCount, which holds ONE SET OF ROWS PER SIDE:
        // a 300-board two-sided lot logs 300 under 'A' and 300 under 'B'. Summing them says 600 built and the
        // lot vanishes from the plan. So sum WITHIN a side and take the LEAST-progressed side as the lot's
        // completed figure — a lot is not finished until every side is, and under-stating progress only ever
        // over-states demand, which is the safe direction for a warning.
        //
        // DeliveredQty is read by name and the query is retried without it on SQL error 207, exactly as
        // GetLotSizeAsync does, so a database without that column degrades instead of failing.
        const string body =
            @"SELECT LTRIM(RTRIM(ISNULL(dd.PONumber,''))) AS Lot,
                     LTRIM(RTRIM(ISNULL(dd.ProductName,''))) AS Model,
                     dd.DeliveryDate,
                     LTRIM(RTRIM(ISNULL(dd.Status,''))) AS Status,
                     dd.Quantity, dd.RevisedQty, {0} AS DeliveredQty,
                     ISNULL(b.Boards, 0) AS Built
              FROM DeliveryDocuments dd
              OUTER APPLY (
                  SELECT MIN(s.Boards) AS Boards FROM (
                      SELECT SUM(ISNULL(d.Quantity,0)) AS Boards
                      FROM DailyProductionCount d
                      WHERE LTRIM(RTRIM(ISNULL(d.LotNo,''))) = LTRIM(RTRIM(ISNULL(dd.PONumber,'')))
                      GROUP BY UPPER(LEFT(LTRIM(RTRIM(ISNULL(d.Side,'?'))),1))
                  ) s
              ) b
              WHERE dd.Status IN ('Planned','Partial')
                AND dd.ProductName LIKE 'L[0-9]%'
                AND dd.DeliveryDate IS NOT NULL
                AND dd.DeliveryDate <= DATEADD(day, @days, CAST(GETDATE() AS date))
              ORDER BY dd.DeliveryDate ASC, dd.DocID ASC";

        try { return await ReadUpcomingAsync(string.Format(body, "dd.DeliveredQty"), horizonDays, ct); }
        catch (SqlException ex) when (ex.Number == 207) { return await ReadUpcomingAsync(string.Format(body, "NULL"), horizonDays, ct); }
    }

    private async Task<IReadOnlyList<UpcomingLot>> ReadUpcomingAsync(string sql, int horizonDays, CancellationToken ct)
    {
        var list = new List<UpcomingLot>();
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.AddWithValue("@days", horizonDays);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            string lot = r.GetString(0);
            string model = r.GetString(1);
            if (lot.Length == 0 || model.Length == 0) continue;
            DateTime? due = r.IsDBNull(2) ? null : r.GetDateTime(2);

            // Which quantity column is the real size is LotSizeResolver's decision, not SQL's.
            var size = LotSizeResolver.Resolve(new LotSizeRow(lot, r.GetString(3), Nullable(r, 4), Nullable(r, 5), Nullable(r, 6)));
            list.Add(new UpcomingLot(lot, model, due, size.Boards, r.IsDBNull(7) ? 0 : Convert.ToInt32(r.GetValue(7))));
        }
        return list;
    }

    private static int? Nullable(SqlDataReader r, int i) => r.IsDBNull(i) ? null : Convert.ToInt32(r.GetValue(i));

    public async Task<IReadOnlyList<BomUsageRow>> GetBomUsageForModelsAsync(
        IReadOnlyCollection<string> models, CancellationToken ct = default)
    {
        var wanted = Clean(models);
        var list = new List<BomUsageRow>();
        if (wanted.Count == 0) return list;

        // RAW rows: one per model+side+line+part+feeder. The per-line copies disagree and a part can sit on
        // both sides — reconciling that is BomUsageResolver's job, so nothing is collapsed here.
        // ISNULL(b.Line,0) because the hand-placed rows carry no line.
        foreach (var chunk in Chunks(wanted))
        {
            string sql =
                $@"SELECT LTRIM(RTRIM(ISNULL(p.ProductName,''))) AS Model,
                          LTRIM(RTRIM(ISNULL(b.Side,'')))        AS Side,
                          ISNULL(b.Line, 0)                      AS Ln,
                          LTRIM(RTRIM(ISNULL(b.PartNumber,'')))  AS Part,
                          ISNULL(b.Quantity, 0)                  AS PerBoard
                   FROM ProductBOM b
                   JOIN Products p ON p.ProductID = b.ProductID
                   WHERE LTRIM(RTRIM(ISNULL(p.ProductName,''))) IN ({Placeholders(chunk.Count)})
                     AND ISNULL(b.Quantity,0) > 0
                     AND LTRIM(RTRIM(ISNULL(b.PartNumber,''))) <> ''";
            await using var cn = await OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, cn);
            Bind(cmd, chunk);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                list.Add(new BomUsageRow(r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetString(3), r.GetInt32(4)));
        }
        return list;
    }

    public async Task<IReadOnlyList<PartStock>> GetPartStockAsync(
        IReadOnlyCollection<string> parts, int reelDaysBack, int freshHours, CancellationToken ct = default)
    {
        var wanted = Clean(parts);
        var list = new List<PartStock>();
        if (wanted.Count == 0) return list;
        if (reelDaysBack <= 0) reelDaysBack = 30;
        if (freshHours <= 0) freshHours = 72;

        // ---- THE AVAILABILITY RULE (verified against live ReelPart-New, 2026-08-07) ----
        //
        //   available = un-issued store stock (StockIns.RemainingQty)
        //             + live balance of reels already at the lines (latest StockOuts row per PartUID)
        //
        // Both halves are needed, and each has a trap:
        //
        // STORE. StockIns.RemainingQty is ZEROED when the reel is issued, not left at its issued value. So a
        // part with thousands of pieces loaded on machines reads as ZERO from the store alone — observed live
        // on VV5-3355-103 (store 0, 247,944 pieces sitting at the lines) and VE3-6800-105 (store 0, 197,296).
        // A handful of UIDs have duplicate StockIns rows, so take one row per UID (MAX) rather than SUM: the
        // UID identifies a physical reel, and a repeated one is a scan error, not a second reel.
        //
        // LINE. StockOuts is re-written per reel UID as PVS syncs the balance, so only the LATEST row per UID
        // counts (ID is an identity, hence ID DESC). This is not a nicety: WA6-3110-000 carries nine rows
        // under the placeholder UID '0000-A830%', and summing them reads 10,135 pieces where the truth is 338.
        //
        // WINDOW. StockOuts keeps its row forever and a reel consumed before PVS started writing balances back
        // still shows its issued quantity. Unbounded, the live figure totals 30.6 MILLION pieces and no
        // shortage can ever appear. @days bounds it to reels issued recently enough to plausibly still be out
        // there. Date is nvarchar 'dd-MM-yyyy'; TRY_CONVERT yields NULL on malformed rows, which drop out.
        //
        // TRACKED. LastQuantityChangeDate is when the balance was last written. Reels on a line PVS does not
        // sync never move, so their contribution is the as-issued figure — reported separately so the email
        // can say how much of its own answer it actually stands behind.
        foreach (var chunk in Chunks(wanted))
        {
            string ph = Placeholders(chunk.Count);
            string sql =
                $@"WITH want AS (SELECT LTRIM(RTRIM(v.p)) AS Part FROM (VALUES {ValuesList(chunk.Count)}) v(p)),
                        si AS (
                            SELECT LTRIM(RTRIM(ISNULL(PartNumber,''))) AS Part,
                                   LTRIM(RTRIM(ISNULL(PartUID,'')))    AS Uid,
                                   MAX(ISNULL(RemainingQty,0))         AS Rem
                            FROM StockIns
                            WHERE LTRIM(RTRIM(ISNULL(PartNumber,''))) IN ({ph})
                            GROUP BY LTRIM(RTRIM(ISNULL(PartNumber,''))), LTRIM(RTRIM(ISNULL(PartUID,'')))
                        ),
                        store AS (SELECT Part, SUM(CAST(Rem AS bigint)) AS Qty FROM si GROUP BY Part),
                        so AS (
                            SELECT LTRIM(RTRIM(ISNULL(PartNumber,''))) AS Part,
                                   LTRIM(RTRIM(ISNULL(PartUID,'')))    AS Uid,
                                   ISNULL(Quantity,0)                  AS Qty,
                                   TRY_CONVERT(date, Date, 105)        AS IssuedOn,
                                   LastQuantityChangeDate              AS Touched,
                                   ROW_NUMBER() OVER (PARTITION BY LTRIM(RTRIM(ISNULL(PartUID,''))) ORDER BY ID DESC) AS rn
                            FROM StockOuts
                            WHERE LTRIM(RTRIM(ISNULL(PartNumber,''))) IN ({ph})
                        ),
                        live AS (
                            SELECT Part,
                                   SUM(CAST(Qty AS bigint)) AS Qty,
                                   SUM(CASE WHEN Touched >= DATEADD(hour, -@fresh, GETDATE()) THEN CAST(Qty AS bigint) ELSE 0 END) AS TrackedQty
                            FROM so
                            WHERE rn = 1 AND Uid <> '' AND Qty > 0
                              AND IssuedOn >= DATEADD(day, -@days, CAST(GETDATE() AS date))
                            GROUP BY Part
                        )
                   SELECT w.Part, ISNULL(st.Qty,0), ISNULL(lv.Qty,0), ISNULL(lv.TrackedQty,0), pp.UnitPrice
                   FROM want w
                   LEFT JOIN store st ON st.Part = w.Part
                   LEFT JOIN live  lv ON lv.Part = w.Part
                   OUTER APPLY (SELECT TOP 1 UnitPrice FROM PartPrices x
                                WHERE LTRIM(RTRIM(x.PartNumber)) = w.Part AND x.UnitPrice > 0
                                ORDER BY x.PriceID DESC) pp";

            await using var cn = await OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, cn);
            Bind(cmd, chunk);
            cmd.Parameters.AddWithValue("@days", reelDaysBack);
            cmd.Parameters.AddWithValue("@fresh", freshHours);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                list.Add(new PartStock(
                    r.GetString(0),
                    Convert.ToInt64(r.GetValue(1)), Convert.ToInt64(r.GetValue(2)), Convert.ToInt64(r.GetValue(3)),
                    r.IsDBNull(4) ? null : Convert.ToDecimal(r.GetValue(4))));
        }
        return list;
    }

    public async Task<IReadOnlyList<LineReelTracking>> GetReelTrackingByLineAsync(
        int reelDaysBack, int freshHours, CancellationToken ct = default)
    {
        if (reelDaysBack <= 0) reelDaysBack = 30;
        if (freshHours <= 0) freshHours = 72;
        const string sql =
            @"WITH so AS (
                  SELECT LTRIM(RTRIM(ISNULL(Line,'')))         AS Ln,
                         LTRIM(RTRIM(ISNULL(PartUID,'')))      AS Uid,
                         ISNULL(Quantity,0)                    AS Qty,
                         TRY_CONVERT(date, Date, 105)          AS IssuedOn,
                         LastQuantityChangeDate                AS Touched,
                         ROW_NUMBER() OVER (PARTITION BY LTRIM(RTRIM(ISNULL(PartUID,''))) ORDER BY ID DESC) AS rn
                  FROM StockOuts)
              SELECT Ln, COUNT(*), SUM(CAST(Qty AS bigint)),
                     SUM(CASE WHEN Touched >= DATEADD(hour, -@fresh, GETDATE()) THEN 1 ELSE 0 END),
                     SUM(CASE WHEN Touched >= DATEADD(hour, -@fresh, GETDATE()) THEN CAST(Qty AS bigint) ELSE 0 END)
              FROM so
              WHERE rn = 1 AND Uid <> '' AND Qty > 0
                AND IssuedOn >= DATEADD(day, -@days, CAST(GETDATE() AS date))
              GROUP BY Ln
              ORDER BY Ln";
        var list = new List<LineReelTracking>();
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.AddWithValue("@days", reelDaysBack);
        cmd.Parameters.AddWithValue("@fresh", freshHours);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new LineReelTracking(
                r.GetString(0), Convert.ToInt32(r.GetValue(1)), Convert.ToInt64(r.GetValue(2)),
                Convert.ToInt32(r.GetValue(3)), Convert.ToInt64(r.GetValue(4))));
        return list;
    }

    // ---- parameterised IN-list helpers (never string-interpolate a value; only the @p placeholders) ----

    private static List<string> Clean(IEnumerable<string>? values) =>
        (values ?? Array.Empty<string>())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static IEnumerable<List<string>> Chunks(List<string> all)
    {
        for (int i = 0; i < all.Count; i += ParamChunk)
            yield return all.GetRange(i, Math.Min(ParamChunk, all.Count - i));
    }

    private static string Placeholders(int n)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < n; i++) { if (i > 0) sb.Append(','); sb.Append("@p").Append(i); }
        return sb.ToString();
    }

    private static string ValuesList(int n)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < n; i++) { if (i > 0) sb.Append(','); sb.Append("(@p").Append(i).Append(')'); }
        return sb.ToString();
    }

    private static void Bind(SqlCommand cmd, List<string> values)
    {
        for (int i = 0; i < values.Count; i++) cmd.Parameters.AddWithValue("@p" + i, values[i]);
    }
}
