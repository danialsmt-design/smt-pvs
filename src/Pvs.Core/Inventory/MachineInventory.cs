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
    /// <summary>Pieces remaining on the loaded reel — DERIVED: StartQty − MountedPerBoard × boards run since the
    /// reel's anchor, clamped at zero for display. The unclamped consumption is kept in the anchor fields, so a
    /// deduction that overshot zero reverses exactly. Only meaningful while IsTracked.</summary>
    public int Remaining { get; internal set; }
    /// <summary>True once a reel with a known quantity is loaded, so it decrements per board.</summary>
    public bool IsTracked { get; internal set; }
    /// <summary>The reel's quantity at its ANCHOR (load, physical recount, or the run's re-baseline).</summary>
    public int StartQty { get; internal set; }
    /// <summary>The machine's OBSERVED board count at the anchor — boards run since = observed − LoadBoards
    /// (+ CorrectionBoards). Denominator for the predicted-vs-actual exhaust signal.</summary>
    public int LoadBoards { get; internal set; }
    /// <summary>Boards a tally correction attributed to THIS reel since its anchor (missed board-completes it was on
    /// the machine for; negative when a correction handed boards back). Stored so a correction and its reversal
    /// cancel exactly, and a later recount / re-anchor starts clean.</summary>
    public int CorrectionBoards { get; internal set; }
}

/// <summary>
/// Tracks the remaining piece-count of every feeder on ONE machine and draws each down as boards complete.
///
/// ACCOUNTING MODEL (rebuilt 2026-09-11 after an external audit found the running-balance version lost information):
///   - The machine keeps TWO counters: <c>_observed</c> = board-completes PVS itself saw this run (never edited by a
///     correction), and <c>_correction</c> = the sum of tally corrections applied (HMI key-in, machine mount count,
///     lot-end recalc). <see cref="BoardsApplied"/> = observed + correction = the tally consumers compare with the lot.
///   - Every reel is ANCHORED: StartQty (its quantity at the anchor), LoadBoards (observed count at the anchor) and
///     CorrectionBoards (correction boards attributed to it since). Remaining is always DERIVED from those:
///         remaining = StartQty − MountedPerBoard × (observed − LoadBoards + CorrectionBoards), clamped ≥ 0 for display.
///     Loading a reel, a physical recount and the run re-baseline each set a fresh anchor (StartQty = the known
///     quantity NOW, LoadBoards = observed NOW, CorrectionBoards = 0), so earlier history can never be charged again.
///   - A correction of delta boards is shared out by the boards each reel was on the machine for since its anchor
///     (share = (observed − LoadBoards) / observed): a reel present from the run's start takes it all, a reel just
///     loaded or just recounted takes none. The share is computed from OBSERVED boards only, so the same correction
///     reversed (120 → 100 after 100 → 120) attributes exactly the opposite and every reel returns to its old count.
///   - Starting qty comes from the reel (StockOut by UID, operator can override), never ProductBOM.Quantity.
/// One instance per machine.
/// </summary>
public sealed class MachineInventory
{
    private readonly Dictionary<int, FeederState> _feeders = new();
    private readonly object _lock = new();   // board-complete (serial thread) vs configure/read (coordinator/API thread)
    private int _observed;                   // board-completes PVS saw this run (per R0) — never edited by a correction
    private int _correction;                 // sum of tally corrections applied this run

    public MachineInventory(int machine) => Machine = machine;

    public int Machine { get; }

    /// <summary>This machine's board tally for the run: the board-completes PVS saw plus every correction applied.
    /// Drifts below the machine's true output whenever PVS missed board-completes — which is exactly what
    /// <see cref="SyncToBoardCount"/> corrects against the machine's HMI panel count.</summary>
    public int BoardsApplied { get { lock (_lock) return _observed + _correction; } }

    /// <summary>Board-completes PVS itself observed this run (no corrections).</summary>
    public int ObservedBoards { get { lock (_lock) return _observed; } }

    /// <summary>Re-anchor the machine to a KNOWN tally without changing any reel's count — a restart / re-baseline
    /// (the feeders' remaining was just restored from the DB and already reflects those boards) or a lot change
    /// (tally restarts at 0). Every tracked reel is re-anchored at its CURRENT remaining, so a reel carried into
    /// the next run counts as present for the whole run and a later correction can never re-charge boards from
    /// before this point. Distinct from <see cref="SyncToBoardCount"/>, which DOES adjust feeders.</summary>
    public void SeedBoardsApplied(int boards)
    {
        lock (_lock)
        {
            boards = Math.Max(0, boards);
            _observed = boards;
            _correction = 0;
            // Every reel on the machine is anchored at the RUN'S START (LoadBoards = 0) with the quantity it must have
            // had then (remaining now + what this run's boards took), so its remaining is unchanged and it counts as
            // present for the whole run: a later correction attributes the run's missed boards to it in full. (After
            // a restart the mid-run load point is not known — the reel is taken as present all run, the safe default
            // for the exhaust signal; the next scan or recount sets an exact anchor again.)
            foreach (var f in _feeders.Values)
                if (f.IsTracked) Anchor(f, (int)Math.Min(int.MaxValue, f.Remaining + (long)f.MountedPerBoard * boards), 0);
        }
    }

    /// <summary>Snapshot of the feeders (copied under the lock, so callers can enumerate freely).</summary>
    public IReadOnlyCollection<FeederState> Feeders { get { lock (_lock) return _feeders.Values.ToList(); } }

    public FeederState? Get(int feeder) { lock (_lock) return _feeders.TryGetValue(feeder, out var f) ? f : null; }

    /// <summary>Clears every feeder (used to re-baseline the whole machine on a model change).</summary>
    public void Clear() { lock (_lock) { _feeders.Clear(); _observed = 0; _correction = 0; } }

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
            f.IsTracked = true;
            Anchor(f, startingQty, _observed);   // anchored NOW: boards before this point can never be charged to it
        }
    }

    /// <summary>Take the reel OFF a configured feeder: no UID, no balance, not tracked — the feeder stays on the
    /// list (it is still expected) but decrements nothing until a reel is scanned on. Used when a scan proves the
    /// reel is physically elsewhere (the same UID was just scanned onto another feeder). No-op if not configured.</summary>
    public void UnloadReel(int feeder)
    {
        lock (_lock)
        {
            if (!_feeders.TryGetValue(feeder, out var f)) return;
            f.ReelUid = null; f.IsTracked = false;
            Anchor(f, 0, _observed);
        }
    }

    /// <summary>Restore a reel's ANCHOR after a re-baseline from its persisted boards-run (how many boards it had
    /// been on the machine for when the local record was written). Re-expresses the same remaining with
    /// LoadBoards = observed − boardsRun, so a later correction attributes only the run this reel really saw.
    /// No-op if not tracked; boardsRun is clamped to the observed count.</summary>
    public void SetBoardsRun(int feeder, int boardsRun)
    {
        lock (_lock)
        {
            if (!_feeders.TryGetValue(feeder, out var f) || !f.IsTracked) return;
            int run = Math.Clamp(boardsRun, 0, _observed);
            int remaining = f.Remaining;
            f.StartQty = (int)Math.Min(int.MaxValue, remaining + (long)f.MountedPerBoard * run);
            f.LoadBoards = _observed - run;
            f.CorrectionBoards = 0;
            Recompute(f);
        }
    }

    /// <summary>The exhaust-accuracy sample for a feeder right now: how many boards this reel has run (since its
    /// anchor, corrections included), the pieces PVS still THINKS remain (the error at a genuine parts-out — ideally
    /// ~0), its anchor qty and per-board rate. Null if not tracked.</summary>
    public (int BoardsThisReel, int Remaining, int StartQty, int MountedPerBoard, string Part, string? ReelUid)? ReelUsage(int feeder)
    {
        lock (_lock)
        {
            if (!_feeders.TryGetValue(feeder, out var f) || !f.IsTracked) return null;
            return (Math.Max(0, BoardsRun(f)), f.Remaining, f.StartQty, f.MountedPerBoard, f.PartNumber, f.ReelUid);
        }
    }

    /// <summary>Marks the loaded reel exhausted (remaining = 0) — the old reel at a genuine parts-out. A fresh
    /// anchor at zero: boards after this point are consumption past empty (kept, clamped for display).</summary>
    public void MarkExhausted(int feeder)
    {
        lock (_lock) Anchor(Require(feeder), 0, _observed);
    }

    /// <summary>Sets an exact remaining count (Mode D physical re-count). Keeps the feeder tracked. The recount is
    /// a NEW anchor: whatever was missed or double-counted before it is already inside the physical number, so a
    /// later tally correction attributes nothing from before the recount to this reel.</summary>
    public void SetRemaining(int feeder, int countedQty)
    {
        lock (_lock)
        {
            var f = Require(feeder);
            f.IsTracked = true;
            Anchor(f, countedQty, _observed);
        }
    }

    /// <summary>
    /// Applies one completed board: every tracked feeder drops by its mounted-per-board count (display clamped at
    /// zero; the overshoot is kept so a later reversal is exact). Call once per R0 (board-complete) from this machine.
    /// </summary>
    public void OnBoardComplete()
    {
        lock (_lock)
        {
            _observed++;
            foreach (var f in _feeders.Values) if (f.IsTracked) Recompute(f);
        }
    }

    /// <summary>
    /// Correct the machine's tally to a KNOWN board count (the operator's HMI reading, the machine's own mount
    /// count, or the lot size at lot end — in panels, the unit the feeders decrement in). delta = trueBoards −
    /// BoardsApplied: POSITIVE = PVS missed boards (draw down), NEGATIVE = PVS double-counted (hand back). The delta
    /// is SHARED among the reels by the boards each was on the machine for since its anchor (observed − LoadBoards
    /// over observed): a reel present all run takes all of it, one loaded or recounted just now takes none. Computed
    /// from observed boards only, so reversing a correction returns every reel exactly. Returns the delta applied.
    /// </summary>
    public int SyncToBoardCount(int trueBoards)
    {
        lock (_lock)
        {
            trueBoards = Math.Max(0, trueBoards);
            int delta = trueBoards - (_observed + _correction);
            if (delta == 0) return 0;
            foreach (var f in _feeders.Values)
            {
                if (!f.IsTracked) continue;
                int since = Math.Max(0, _observed - f.LoadBoards);
                double share = _observed > 0 ? since / (double)_observed : 1.0;   // no boards seen yet: whoever is loaded was here all run
                int boards = (int)Math.Round(delta * share, MidpointRounding.AwayFromZero);
                f.CorrectionBoards += boards;
                Recompute(f);
            }
            _correction += delta;
            return delta;
        }
    }

    // ---- internals (callers hold _lock) ----

    private static int BoardsRun(FeederState f, int observed) => observed - f.LoadBoards + f.CorrectionBoards;
    private int BoardsRun(FeederState f) => BoardsRun(f, _observed);

    /// <summary>Fresh anchor: the reel's quantity was <paramref name="qty"/> at observed count <paramref name="loadBoards"/>
    /// (NOW for a load / recount; the run's start for a re-baseline). Corrections from before the anchor are gone.</summary>
    private void Anchor(FeederState f, int qty, int loadBoards)
    {
        f.StartQty = Math.Max(0, qty);
        f.LoadBoards = loadBoards;
        f.CorrectionBoards = 0;
        Recompute(f);
    }

    // Display value: never below 0 (consumption past empty is kept in the anchor fields) and never above the
    // anchor quantity (a hand-back can't create pieces the reel never had; audit round 3).
    private void Recompute(FeederState f) =>
        f.Remaining = (int)Math.Clamp((long)f.StartQty - (long)f.MountedPerBoard * BoardsRun(f, _observed), 0, f.StartQty);

    private FeederState Require(int feeder) =>
        _feeders.TryGetValue(feeder, out var f)
            ? f
            : throw new InvalidOperationException($"Feeder {feeder} is not configured on machine {Machine}.");
}
