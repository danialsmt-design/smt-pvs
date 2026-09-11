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
    /// <summary>Starting quantity of the currently-loaded reel (for the exhaust-accuracy signal).</summary>
    public int StartQty { get; internal set; }
    /// <summary>The machine's board tally when the current reel was loaded — so boards-run-this-reel =
    /// BoardsApplied − LoadBoards, the denominator for measuring predicted-vs-actual exhaust.</summary>
    public int LoadBoards { get; internal set; }
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
    private int _boardsApplied;              // boards this machine's feeders have been decremented for (per R0)

    public MachineInventory(int machine) => Machine = machine;

    public int Machine { get; }

    /// <summary>How many completed boards this machine's feeders have been drawn down for since the last
    /// model-change/clear. This is PVS's OWN per-machine tally (one per R0), so it drifts below the machine's
    /// true output whenever PVS missed board-completes — which is exactly what <see cref="SyncToBoardCount"/>
    /// corrects against the machine's HMI panel count.</summary>
    public int BoardsApplied { get { lock (_lock) return _boardsApplied; } }

    /// <summary>Set the boards-applied tally WITHOUT touching any feeder — used to re-anchor the per-machine
    /// count to the lot's board count after a restart / re-baseline. The feeders' remaining is restored from the
    /// DB (it already reflects those boards), so the tally must be seeded to match; otherwise a later HMI sync
    /// would subtract the same boards a SECOND time (double-decrement). Distinct from <see cref="SyncToBoardCount"/>,
    /// which DOES adjust feeders.</summary>
    public void SeedBoardsApplied(int boards) { lock (_lock) _boardsApplied = Math.Max(0, boards); }

    /// <summary>Snapshot of the feeders (copied under the lock, so callers can enumerate freely).</summary>
    public IReadOnlyCollection<FeederState> Feeders { get { lock (_lock) return _feeders.Values.ToList(); } }

    public FeederState? Get(int feeder) { lock (_lock) return _feeders.TryGetValue(feeder, out var f) ? f : null; }

    /// <summary>Clears every feeder (used to re-baseline the whole machine on a model change).</summary>
    public void Clear() { lock (_lock) { _feeders.Clear(); _boardsApplied = 0; } }

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
            f.StartQty = Math.Max(0, startingQty);
            f.LoadBoards = _boardsApplied;   // anchor for boards-run-this-reel (exhaust-accuracy signal)
            f.IsTracked = true;
        }
    }

    /// <summary>The exhaust-accuracy sample for a feeder right now: how many boards this reel has run, the pieces
    /// PVS still THINKS remain (the error at a genuine parts-out — ideally ~0), its start qty and per-board rate.
    /// Read at a parts-out to learn whether the reel emptied earlier/later than predicted. Null if not tracked.</summary>
    /// <summary>Take the reel OFF a configured feeder: no UID, no balance, not tracked — the feeder stays on the
    /// list (it is still expected) but decrements nothing until a reel is scanned on. Used when a scan proves the
    /// reel is physically elsewhere (the same UID was just scanned onto another feeder). No-op if not configured.</summary>
    public void UnloadReel(int feeder)
    {
        lock (_lock)
        {
            if (!_feeders.TryGetValue(feeder, out var f)) return;
            f.ReelUid = null; f.Remaining = 0; f.StartQty = 0; f.LoadBoards = _boardsApplied; f.IsTracked = false;
        }
    }

    public (int BoardsThisReel, int Remaining, int StartQty, int MountedPerBoard, string Part, string? ReelUid)? ReelUsage(int feeder)
    {
        lock (_lock)
        {
            if (!_feeders.TryGetValue(feeder, out var f) || !f.IsTracked) return null;
            return (Math.Max(0, _boardsApplied - f.LoadBoards), f.Remaining, f.StartQty, f.MountedPerBoard, f.PartNumber, f.ReelUid);
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
        {
            _boardsApplied++;
            foreach (var f in _feeders.Values)
            {
                if (!f.IsTracked) continue;
                f.Remaining = Math.Max(0, f.Remaining - f.MountedPerBoard);
            }
        }
    }

    /// <summary>
    /// Correct every tracked feeder to a KNOWN board count (the operator's manual HMI reading, in the same board
    /// unit the feeders decrement in — one per completed PWB). PVS can't read the machine counter over serial
    /// during production (Appendix F: C1M/C1Z reply A4E00 while AUTO-producing), so the operator keeps the count
    /// honest by hand; this applies the difference as a one-off draw-down. delta = trueBoards - boardsApplied:
    /// a POSITIVE delta (PVS missed boards) draws the feeders down; a NEGATIVE delta (PVS double-counted) hands
    /// pieces back. Clamped at zero. Returns the applied delta (boards).
    /// </summary>
    public int SyncToBoardCount(int trueBoards)
    {
        lock (_lock)
        {
            trueBoards = Math.Max(0, trueBoards);
            int delta = trueBoards - _boardsApplied;
            int applied = _boardsApplied;
            foreach (var f in _feeders.Values)
            {
                if (!f.IsTracked) continue;
                // The missed (or double-counted) boards happened at unknown moments in this run, so a reel takes
                // the SHARE of the correction proportional to the boards it was on the machine for: a reel loaded
                // at the start of the run takes all of it, a reel loaded just now takes none. Applying the whole
                // difference to every reel deducted boards made before a reel was loaded (audit 2026-09-11).
                // A reel loaded before this run's tally was seeded (LoadBoards > applied) has run the whole run.
                int since = f.LoadBoards <= applied ? applied - f.LoadBoards : applied;
                double share = applied > 0 ? since / (double)applied : 1.0;
                int pieces = (int)Math.Round(f.MountedPerBoard * delta * share);
                f.Remaining = Math.Max(0, f.Remaining - pieces);
            }
            _boardsApplied = trueBoards;
            return delta;
        }
    }

    // Callers hold _lock.
    private FeederState Require(int feeder) =>
        _feeders.TryGetValue(feeder, out var f)
            ? f
            : throw new InvalidOperationException($"Feeder {feeder} is not configured on machine {Machine}.");
}
