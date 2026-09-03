namespace Pvs.Core.Runtime;

/// <summary>Why the counting machine changed, for the log and the audit trail.</summary>
public enum CountingSourceChange
{
    /// <summary>No change this evaluation.</summary>
    None,
    /// <summary>First selection — nothing was counting before.</summary>
    Initial,
    /// <summary>The machine that was counting went offline; fell back down the line.</summary>
    FellBack,
    /// <summary>A machine further down the line came back; counting moved to it.</summary>
    Recovered,
}

/// <summary>The outcome of evaluating which machine should be counting boards.</summary>
public sealed record CountingSourceResult(
    int? Machine,
    CountingSourceChange Change,
    int? PreviousMachine,
    long LotBoards,
    string Message);

/// <summary>
/// Decides which machine's board-complete events drive the lot count, and keeps that count continuous when
/// the choice changes.
/// <para>
/// The line normally counts at the LAST machine, because a board that completes there has been through every
/// station. But a line gets reconfigured: when a machine has a problem its feeders are moved to the next one
/// and the job runs on three machines, or two. If the machine that was counting drops out, counting has to
/// fall back to the last one still available, and later move forward again when it returns.
/// </para>
/// <para>
/// The hard part is the handover, not the choice. Every machine keeps its own monotonic total, and those
/// totals are unrelated — M3 may have seen 40,000 boards in its life and M4 only 2,600. Switching source
/// naively would make the lot count leap or collapse. So on every switch the new machine's anchor is set to
/// <c>itsTotal − lotBoardsSoFar</c>, which leaves the lot count exactly where it was and lets it carry on.
/// </para>
/// <para>
/// Pure: it is told each machine's total and whether it is online, and returns a decision. It never reads a
/// port or a clock.
/// </para>
/// </summary>
public sealed class CountingMachineTracker
{
    private readonly Dictionary<int, long> _anchor = new();
    private long _lotBoards;

    /// <summary>The machine currently driving the count, or null if none is available.</summary>
    public int? Machine { get; private set; }

    /// <summary>Boards counted for the current lot, continuous across source changes.</summary>
    public long LotBoards => _lotBoards;

    /// <summary>
    /// Pick the counting machine and update the lot count.
    /// </summary>
    /// <param name="totals">Each machine's own monotonic board total, keyed by machine number.</param>
    /// <param name="online">Machines currently online and reporting. A machine absent from this set is not
    /// eligible, however high its number.</param>
    public CountingSourceResult Evaluate(IReadOnlyDictionary<int, long> totals, IReadOnlySet<int> online)
    {
        // The last AVAILABLE machine: highest number that is both configured and online. Not simply the
        // highest configured, because that is exactly the one dropped when the line is reconfigured.
        int? pick = totals.Keys.Where(online.Contains).Select(m => (int?)m).DefaultIfEmpty(null).Max();

        if (pick is null)
        {
            var prevNone = Machine;
            Machine = null;
            return new CountingSourceResult(null, CountingSourceChange.None, prevNone, _lotBoards,
                "no machine online — the lot count is held, not reset");
        }

        int m2 = pick.Value;
        long total = totals[m2];

        if (Machine == m2)
        {
            // Same source: advance the count. Never let it go backwards — a machine whose own counter was
            // reset (operators clear them at lot end) must not drag the lot count down with it.
            long fresh = total - _anchor[m2];
            if (fresh > _lotBoards)
                _lotBoards = fresh;
            else if (fresh < 0)
                _anchor[m2] = total - _lotBoards;   // its counter was reset under us; re-anchor, keep the count
            return new CountingSourceResult(m2, CountingSourceChange.None, m2, _lotBoards, $"counting at M{m2}");
        }

        // Source is changing. Anchor the new machine so the lot count continues unbroken.
        var prev = Machine;
        _anchor[m2] = total - _lotBoards;
        Machine = m2;

        var change = prev is null ? CountingSourceChange.Initial
                   : m2 < prev    ? CountingSourceChange.FellBack
                                  : CountingSourceChange.Recovered;

        string msg = change switch
        {
            CountingSourceChange.Initial   => $"counting at M{m2} (last available machine)",
            CountingSourceChange.FellBack  => $"M{prev} went offline — counting fell back to M{m2}, lot count held at {_lotBoards}",
            _                              => $"M{m2} is back — counting moved forward from M{prev}, lot count held at {_lotBoards}",
        };
        return new CountingSourceResult(m2, change, prev, _lotBoards, msg);
    }

    /// <summary>A new lot has started: the count restarts from zero, anchored to whatever is counting now.</summary>
    public void StartLot(IReadOnlyDictionary<int, long> totals)
    {
        _lotBoards = 0;
        if (Machine is int m && totals.TryGetValue(m, out long t)) _anchor[m] = t;
    }

    /// <summary>Set the lot count to a known figure (a supervisor correction, or adopting a machine's own
    /// per-lot counter). The anchor moves with it so subsequent counting continues from the corrected value.</summary>
    public void SetLotBoards(long boards, IReadOnlyDictionary<int, long> totals)
    {
        if (boards < 0) throw new ArgumentOutOfRangeException(nameof(boards));
        _lotBoards = boards;
        if (Machine is int m && totals.TryGetValue(m, out long t)) _anchor[m] = t - boards;
    }
}
