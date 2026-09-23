namespace Pvs.Core.Inventory;

/// <summary>
/// One reel's shortage measured at a GENUINE parts-out. The machine says the feeder is empty, so whatever PVS
/// still showed on that reel is the material the model never saw leave the tape (throws, splices, short reels,
/// or a wrong reel on the feeder). Attrition % = shortage ÷ the reel's start quantity.
/// </summary>
public sealed record ReelAttrition(
    DateTime At,
    string LotNo,
    int Machine,
    int Feeder,
    string Part,
    string ReelUid,
    int StartQty,
    int BoardsThisReel,
    int MountedPerBoard,
    int Shortage,           // PVS remaining at the instant of exhaust (>= 0)
    double Percent,         // shortage / startQty × 100
    bool Over,              // percent > the line limit
    bool Escalate,          // Over AND enough boards to be a real measurement (not a short run)
    bool Alerted = false);  // included in a WhatsApp already (persisted so a restart never re-sends)

/// <summary>
/// Danial 2026-09-14: "the component shortage during parts exhaust compared with PVS creates an attrition report;
/// should be less than 2 %; anything more highlight to Mr Raja." Pure rules + a bounded in-memory list; the
/// coordinator persists it and sends the message.
/// </summary>
public sealed class AttritionLedger
{
    private readonly List<ReelAttrition> _rows = new();
    private readonly object _lock = new();

    public AttritionLedger(double limitPct = 2.0, int minBoards = 20, int keepDays = 60, IEnumerable<ReelAttrition>? seed = null)
    {
        LimitPct = limitPct > 0 ? limitPct : 2.0;
        MinBoards = Math.Max(1, minBoards);
        KeepDays = Math.Max(1, keepDays);
        if (seed is not null) _rows.AddRange(seed.Where(r => r is not null));
    }

    public double LimitPct { get; }
    public int MinBoards { get; }
    public int KeepDays { get; }

    /// <summary>Compute one sample. StartQty must be positive to give a percent; a zero start yields 0 % (nothing
    /// to compare against) and is still recorded so the report shows the reel ran without a known quantity.</summary>
    public ReelAttrition Evaluate(DateTime at, string lotNo, int machine, int feeder, string part, string reelUid,
                                  int startQty, int boardsThisReel, int mountedPerBoard, int remainingAtExhaust)
    {
        int shortage = remainingAtExhaust;   // may be NEGATIVE: PVS reached zero before the reel did (over-count)
        double pct = startQty > 0 ? shortage / (double)startQty * 100.0 : 0.0;
        bool over = startQty > 0 && Math.Abs(pct) > LimitPct;   // either direction is a count error worth seeing
        bool escalate = over && boardsThisReel >= MinBoards;
        return new ReelAttrition(at, lotNo ?? "", machine, feeder, part ?? "", reelUid ?? "", startQty,
            boardsThisReel, mountedPerBoard, shortage, Math.Round(pct, 2), over, escalate);
    }

    /// <summary>Evaluate and keep. One sample per reel (machine|feeder|uid): a repeat parts-out frame for the same
    /// reel is ignored and returns null.</summary>
    public ReelAttrition? Record(DateTime at, string lotNo, int machine, int feeder, string part, string reelUid,
                                 int startQty, int boardsThisReel, int mountedPerBoard, int remainingAtExhaust)
    {
        var row = Evaluate(at, lotNo, machine, feeder, part, reelUid, startQty, boardsThisReel, mountedPerBoard, remainingAtExhaust);
        lock (_lock)
        {
            if (!string.IsNullOrWhiteSpace(row.ReelUid) &&
                _rows.Any(r => r.Machine == row.Machine && r.Feeder == row.Feeder &&
                               string.Equals(r.ReelUid, row.ReelUid, StringComparison.OrdinalIgnoreCase)))
                return null;
            _rows.Add(row);
            Prune(at);
        }
        return row;
    }

    public IReadOnlyList<ReelAttrition> All() { lock (_lock) return _rows.OrderByDescending(r => r.At).ToList(); }

    public IReadOnlyList<ReelAttrition> ForLot(string lotNo)
    {
        if (string.IsNullOrWhiteSpace(lotNo)) return Array.Empty<ReelAttrition>();
        lock (_lock) return _rows.Where(r => string.Equals(r.LotNo, lotNo, StringComparison.OrdinalIgnoreCase)).OrderBy(r => r.At).ToList();
    }

    public IReadOnlyList<ReelAttrition> Between(DateTime from, DateTime to)
    {
        lock (_lock) return _rows.Where(r => r.At >= from && r.At < to).OrderBy(r => r.At).ToList();
    }

    /// <summary>Rows for the lot that cross the limit with a meaningful run and have NOT been sent yet. Marks them
    /// sent so the same lot never produces a second message (lot change + force-end both finalise).</summary>
    public IReadOnlyList<ReelAttrition> TakeEscalations(string lotNo)
    {
        if (string.IsNullOrWhiteSpace(lotNo)) return Array.Empty<ReelAttrition>();
        lock (_lock)
        {
            var due = new List<ReelAttrition>();
            for (int i = 0; i < _rows.Count; i++)
            {
                var r = _rows[i];
                if (!r.Escalate || r.Alerted || !string.Equals(r.LotNo, lotNo, StringComparison.OrdinalIgnoreCase)) continue;
                _rows[i] = r with { Alerted = true };
                due.Add(_rows[i]);
            }
            return due.OrderBy(r => r.Machine).ThenBy(r => r.Feeder).ToList();
        }
    }

    /// <summary>The WhatsApp text for one lot's escalations: one line per reel, worst first inside each machine.</summary>
    public static string ComposeMessage(string lineName, string lotNo, string? model, IReadOnlyList<ReelAttrition> rows, double limitPct)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("⚠️ PVS attrition over ").Append(limitPct.ToString("0.#")).Append("%\n");
        sb.Append(lineName).Append(" · Lot ").Append(lotNo);
        if (!string.IsNullOrWhiteSpace(model)) sb.Append(" · ").Append(model);
        sb.Append('\n');
        foreach (var r in rows)
            sb.Append("M").Append(r.Machine).Append(" F").Append(r.Feeder).Append(' ').Append(r.Part)
              .Append(" · reel ").Append(r.ReelUid)
              .Append(" · short ").Append(r.Shortage.ToString("N0")).Append(" of ").Append(r.StartQty.ToString("N0"))
              .Append(" (").Append(r.Percent.ToString("0.0")).Append("%) after ").Append(r.BoardsThisReel).Append(" boards\n");
        sb.Append(rows.Count).Append(" reel(s) — please check the feeder/nozzle and the reel issued.");
        return sb.ToString();
    }

    private void Prune(DateTime now)
    {
        var cut = now.AddDays(-KeepDays);
        _rows.RemoveAll(r => r.At < cut);
    }
}
