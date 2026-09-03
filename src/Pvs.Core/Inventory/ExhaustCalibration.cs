namespace Pvs.Core.Inventory;

/// <summary>One part's learned exhaust-accuracy: how the REAL reel consumption compares to the modelled
/// mount-points-per-board, smoothed over many reels.</summary>
public sealed record PartCalibration(
    string Part,
    double PerBoardErrorEwma,   // smoothed pieces/board the model is OFF by (+ = under-counting, reel empties early)
    int Samples,
    double LastErrorPerBoard,
    DateTime UpdatedAt)
{
    /// <summary>Learned real per-board consumption = modelled + the smoothed error (never below zero).</summary>
    public double CorrectedPerBoard(int mountedPerBoard) => Math.Max(0, mountedPerBoard + PerBoardErrorEwma);

    /// <summary>How far the model drifts from reality, as a % of the modelled per-board (+ = runs out early).</summary>
    public double DriftPercent(int mountedPerBoard) =>
        mountedPerBoard > 0 ? PerBoardErrorEwma / mountedPerBoard * 100.0 : 0.0;
}

/// <summary>
/// Learns, per part, how far real reel exhaust drifts from PVS's prediction — the tell-tale that the per-board
/// count is inaccurate. At each genuine parts-out the reel is EMPTY, so PVS's tracked remaining at that instant
/// is the accumulated error over the boards the reel ran:
/// <code>errorPerBoard = remainingAtExhaust / boardsThisReel</code>
/// POSITIVE means PVS under-counted (the reel emptied EARLIER than predicted — real attrition/waste the model
/// misses); NEGATIVE means it over-counted. An EWMA per part smooths the noisy per-reel samples into a stable
/// correction that predicts the NEXT reel's exhaust better.
/// <para>
/// SHADOW by design: this MEASURES and SUGGESTS a corrected per-board rate. It never rewrites a reel count — the
/// reel's balance stays ground-truth (operator/HMI/parts-out), honouring "never lose a reel's count". Applying
/// the learned rate to the live decrement is a separate, opt-in step once the drift is proven stable.
/// </para>
/// </summary>
public sealed class ExhaustCalibration
{
    private readonly double _alpha;   // EWMA weight on the newest sample (0<alpha<=1); higher = adapts faster
    private readonly int _minBoards;  // ignore reels that ran too few boards to give a meaningful rate
    private readonly Dictionary<string, PartCalibration> _byPart = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public ExhaustCalibration(double alpha = 0.3, int minBoards = 20, IEnumerable<PartCalibration>? seed = null)
    {
        _alpha = alpha <= 0 || alpha > 1 ? 0.3 : alpha;
        _minBoards = Math.Max(1, minBoards);
        if (seed is not null) foreach (var p in seed) if (!string.IsNullOrWhiteSpace(p.Part)) _byPart[p.Part] = p;
    }

    /// <summary>
    /// Record one genuine parts-out sample for a part. <paramref name="remainingAtExhaust"/> is what PVS still
    /// thought remained at the instant the reel emptied (the error). Returns the updated calibration, or null if
    /// the sample is unusable (no part name, or too few boards to be meaningful).
    /// </summary>
    public PartCalibration? Record(string part, int boardsThisReel, int remainingAtExhaust, int mountedPerBoard, DateTime at)
    {
        if (string.IsNullOrWhiteSpace(part) || boardsThisReel < _minBoards) return null;
        double errPerBoard = (double)remainingAtExhaust / boardsThisReel;
        lock (_lock)
        {
            if (_byPart.TryGetValue(part, out var prev))
            {
                double ewma = _alpha * errPerBoard + (1 - _alpha) * prev.PerBoardErrorEwma;
                var upd = prev with
                {
                    PerBoardErrorEwma = ewma,
                    Samples = prev.Samples + 1,
                    LastErrorPerBoard = errPerBoard,
                    UpdatedAt = at
                };
                _byPart[part] = upd;
                return upd;
            }
            var first = new PartCalibration(part, errPerBoard, 1, errPerBoard, at);
            _byPart[part] = first;
            return first;
        }
    }

    public PartCalibration? Get(string part)
    {
        if (string.IsNullOrWhiteSpace(part)) return null;
        lock (_lock) return _byPart.TryGetValue(part, out var p) ? p : null;
    }

    /// <summary>All learned parts, worst drift first.</summary>
    public IReadOnlyList<PartCalibration> All()
    {
        lock (_lock) return _byPart.Values.OrderByDescending(p => Math.Abs(p.PerBoardErrorEwma)).ToList();
    }
}
