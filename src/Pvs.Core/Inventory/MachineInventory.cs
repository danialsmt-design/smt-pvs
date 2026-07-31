namespace Pvs.Core.Inventory;

/// <summary>Live state of one feeder on a machine.</summary>
public sealed class FeederState
{
    public required int Feeder { get; init; }
    public required string PartNumber { get; init; }
    /// <summary>Placements of this part on ONE board (the machine feeder-list "Mount Step").</summary>
    public required int MountedPerBoard { get; init; }

    /// <summary>UID of the reel currently on this feeder, or null if nothing is loaded/known.</summary>
    public string? ReelUid { get; internal set; }
    /// <summary>Pieces remaining on the loaded reel. Only meaningful while IsTracked.</summary>
    public int Remaining { get; internal set; }
    /// <summary>True once a reel with a known quantity is loaded, so it decrements per board.</summary>
    public bool IsTracked { get; internal set; }
}

/// <summary>
/// Tracks the remaining piece-count of every feeder on ONE machine and draws each down as boards
/// complete. Grounded in the agreed model:
///   - remaining = starting qty - (mounted-per-board x boards completed).
///   - Starting qty comes from the new reel (pre-filled from StockIns.RemainingQty, operator can
///     override); it is NOT taken from ProductBOM.Quantity, which is per-unit and 6x too small.
///   - On a genuine parts-out swap: the OLD reel is set to 0 (exhausted); the NEW reel is loaded
///     with its keyed-in quantity.
///   - Mode D (inventory re-count) sets an exact counted remaining at any time.
/// One instance per machine.
/// </summary>
public sealed class MachineInventory
{
    private readonly Dictionary<int, FeederState> _feeders = new();
    private readonly object _lock = new();   // board-complete (serial thread) vs configure/read (coordinator/API thread)

    public MachineInventory(int machine) => Machine = machine;

    public int Machine { get; }

    /// <summary>Snapshot of the feeders (copied under the lock, so callers can enumerate freely).</summary>
    public IReadOnlyCollection<FeederState> Feeders { get { lock (_lock) return _feeders.Values.ToList(); } }

    public FeederState? Get(int feeder) { lock (_lock) return _feeders.TryGetValue(feeder, out var f) ? f : null; }

    /// <summary>Clears every feeder (used to re-baseline the whole machine on a model change).</summary>
    public void Clear() { lock (_lock) _feeders.Clear(); }

    /// <summary>Defines a feeder's part + placements-per-board (from the model's feeder list). No reel yet.</summary>
    public FeederState Configure(int feeder, string partNumber, int mountedPerBoard)
    {
        var state = new FeederState
        {
            Feeder = feeder,
            PartNumber = partNumber,
            MountedPerBoard = mountedPerBoard
        };
        lock (_lock) _feeders[feeder] = state;
        return state;
    }

    /// <summary>Loads a new reel with its starting quantity; the feeder now decrements per board.</summary>
    public void LoadReel(int feeder, string reelUid, int startingQty)
    {
        lock (_lock)
        {
            var f = Require(feeder);
            f.ReelUid = reelUid;
            f.Remaining = Math.Max(0, startingQty);
            f.IsTracked = true;
        }
    }

    /// <summary>Marks the loaded reel exhausted (remaining = 0) — the old reel at a genuine parts-out.</summary>
    public void MarkExhausted(int feeder)
    {
        lock (_lock) Require(feeder).Remaining = 0;
    }

    /// <summary>Sets an exact remaining count (Mode D re-count). Keeps the feeder tracked.</summary>
    public void SetRemaining(int feeder, int countedQty)
    {
        lock (_lock)
        {
            var f = Require(feeder);
            f.Remaining = Math.Max(0, countedQty);
            f.IsTracked = true;
        }
    }

    /// <summary>
    /// Applies one completed board: every tracked feeder drops by its mounted-per-board count,
    /// clamped at zero. Call once per R0 (board-complete) from this machine.
    /// </summary>
    public void OnBoardComplete()
    {
        lock (_lock)
            foreach (var f in _feeders.Values)
            {
                if (!f.IsTracked) continue;
                f.Remaining = Math.Max(0, f.Remaining - f.MountedPerBoard);
            }
    }

    // Callers hold _lock.
    private FeederState Require(int feeder) =>
        _feeders.TryGetValue(feeder, out var f)
            ? f
            : throw new InvalidOperationException($"Feeder {feeder} is not configured on machine {Machine}.");
}
