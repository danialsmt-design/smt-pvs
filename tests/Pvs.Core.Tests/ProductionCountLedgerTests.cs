using Pvs.Core.Runtime;
using Xunit;

namespace Pvs.Core.Tests;

/// <summary>
/// Scenarios are the real ones from the 2026-08-05 Line 5 incident: the first flush after WriteProductionCount
/// was switched on wrote 120 boards (the LOT TOTAL, 30 panels × 4-up L307) stamped into a 5-minute window, and
/// an older flush of a backlog produced rows whose StartTime was later than their EndTime. See the ledger's
/// remarks for why it is an increment ledger and forward-only.
/// </summary>
public class ProductionCountLedgerTests
{
    private static readonly ProductionKey L307 = new("PO-55012", "L307", "A");
    private const int PerPanel = 4;              // L307 is a 4-up panel

    private static ProductionCountLedger Filled(ProductionKey key, int panels, DateTime from)
    {
        var l = new ProductionCountLedger();
        for (int i = 0; i < panels; i++) l.AddPanel(key, from.AddSeconds(48 * i));   // ~48s machine cycle
        return l;
    }

    // ---- normal incremental write ----

    [Fact]
    public void Each_window_writes_only_the_boards_produced_since_the_last_write()
    {
        var t0 = new DateTime(2026, 8, 5, 12, 37, 49);
        var l = Filled(L307, 6, t0);                       // 6 panels in the first window

        var first = Assert.Single(l.Due(t0.AddMinutes(5)));
        Assert.Equal(6, first.Panels);
        l.Commit(first.Key, first.Panels, first.End);

        for (int i = 0; i < 3; i++) l.AddPanel(L307, t0.AddMinutes(6).AddSeconds(48 * i));

        var second = Assert.Single(l.Due(t0.AddMinutes(10)));
        Assert.Equal(3, second.Panels);                    // the INCREMENT, not the 9 produced so far
        Assert.Equal(12, second.Panels * PerPanel);
    }

    [Fact]
    public void A_written_window_leaves_nothing_behind()
    {
        var t0 = new DateTime(2026, 8, 5, 12, 37, 49);
        var l = Filled(L307, 6, t0);
        var w = Assert.Single(l.Due(t0.AddMinutes(5)));

        l.Commit(w.Key, w.Panels, w.End);

        Assert.Empty(l.Due(t0.AddMinutes(10)));
        Assert.Equal(0, l.TotalPanels);
    }

    [Fact]
    public void The_next_window_starts_where_the_last_one_ended()
    {
        var t0 = new DateTime(2026, 8, 5, 12, 37, 49);
        var l = Filled(L307, 10, t0);
        var first = Assert.Single(l.Due(t0.AddMinutes(5)));

        l.Commit(first.Key, 4, first.End);                 // partial write (the caller's cap held some back)
        var second = Assert.Single(l.Due(t0.AddMinutes(10)));

        Assert.Equal(6, second.Panels);                    // the held-back remainder, still owed
        Assert.Equal(first.End, second.Start);             // no overlap and no gap between the two rows
    }

    [Fact]
    public void A_failed_write_is_not_committed_so_the_boards_retry_next_window()
    {
        var t0 = new DateTime(2026, 8, 5, 12, 37, 49);
        var l = Filled(L307, 6, t0);
        l.Due(t0.AddMinutes(5));                           // row built, DB insert threw -> no Commit

        var retry = Assert.Single(l.Due(t0.AddMinutes(10)));
        Assert.Equal(6, retry.Panels);                     // no loss
    }

    [Fact]
    public void Boards_without_a_lot_or_model_are_not_attributable_and_are_ignored()
    {
        var l = new ProductionCountLedger();
        var now = new DateTime(2026, 8, 5, 12, 0, 0);

        l.AddPanel(new ProductionKey("", "L307", "A"), now);
        l.AddPanel(new ProductionKey("PO-55012", "", "A"), now);

        Assert.Equal(0, l.TotalPanels);
        Assert.Empty(l.Due(now.AddMinutes(5)));
    }

    // ---- restart mid-window ----

    [Fact]
    public void Restart_mid_window_loses_nothing()
    {
        var t0 = new DateTime(2026, 8, 5, 12, 37, 49);
        var before = Filled(L307, 7, t0);                  // 7 panels produced, none written yet

        var after = new ProductionCountLedger();           // app restarts, dpc-state.json reloaded
        Assert.Equal(0, after.Restore(before.Snapshot(), t0.AddMinutes(2)));

        var w = Assert.Single(after.Due(t0.AddMinutes(5)));
        Assert.Equal(7, w.Panels);
        Assert.Equal(t0, w.Start);                         // the window still opens at the first unwritten board
    }

    [Fact]
    public void Restart_after_a_write_does_not_re_write_the_same_boards()
    {
        var t0 = new DateTime(2026, 8, 5, 12, 37, 49);
        var before = Filled(L307, 7, t0);
        var w = Assert.Single(before.Due(t0.AddMinutes(5)));
        before.Commit(w.Key, w.Panels, w.End);             // row confirmed written, then the app restarts

        var after = new ProductionCountLedger();
        after.Restore(before.Snapshot(), t0.AddMinutes(6));

        Assert.Empty(after.Due(t0.AddMinutes(10)));
        Assert.Equal(0, after.TotalPanels);
    }

    [Fact]
    public void Restart_mid_window_carries_only_the_unwritten_remainder()
    {
        var t0 = new DateTime(2026, 8, 5, 12, 37, 49);
        var before = Filled(L307, 10, t0);
        var w = Assert.Single(before.Due(t0.AddMinutes(5)));
        before.Commit(w.Key, 4, w.End);                    // 4 written, 6 still owed, then restart

        var after = new ProductionCountLedger();
        after.Restore(before.Snapshot(), t0.AddMinutes(6));

        var next = Assert.Single(after.Due(t0.AddMinutes(10)));
        Assert.Equal(6, next.Panels);
    }

    [Fact]
    public void An_overnight_restart_cannot_produce_a_row_that_starts_after_it_ends()
    {
        // The bucket opened last night at 20:14; the first flush of the new day lands at 08:12. The row carries
        // ONE date plus two time-of-day strings, so an unclamped start would write 20:14:03 -> 08:12:41.
        var opened = new DateTime(2026, 8, 4, 20, 14, 3);
        var before = Filled(L307, 5, opened);

        var after = new ProductionCountLedger { StaleAfter = TimeSpan.FromDays(2) };   // keep it, to test clamping
        var winEnd = new DateTime(2026, 8, 5, 8, 12, 41);
        after.Restore(before.Snapshot(), winEnd);

        var w = Assert.Single(after.Due(winEnd));
        Assert.True(w.Start <= w.End, $"row starts after it ends: {w.Start:HH:mm:ss} > {w.End:HH:mm:ss}");
        Assert.Equal(opened.Date, w.Start.Date);           // stamped on its OWN production day (08-04), not the flush day
        Assert.Equal(opened, w.Start);                     // starts at the first held board's time
        Assert.Equal(opened.Date, w.End.Date);             // end capped to that same day — never spills to the flush day
    }

    [Fact]
    public void A_clock_step_cannot_push_a_windows_start_past_its_end()
    {
        var winEnd = new DateTime(2026, 8, 5, 12, 42, 39);
        var l = new ProductionCountLedger();
        l.AddPanel(L307, winEnd.AddMinutes(3));            // stamped in the future (clock corrected backwards)

        var w = Assert.Single(l.Due(winEnd));
        Assert.True(w.Start <= w.End);
        Assert.Equal(w.Start, w.End);   // a future-stamped board collapses to a zero-length window, never backwards
    }

    // ---- held backlog after a DB outage: never lost, written back dated to its own day ----

    [Fact]
    public void A_backlog_from_a_previous_day_is_kept_and_dated_to_its_own_day()
    {
        // A DB outage held these boards overnight. They must NOT be dropped — the line never loses a count it made;
        // they are written back dated to the day they were produced (08-04), never the flush day.
        var opened = new DateTime(2026, 8, 4, 14, 0, 0);
        var old = Filled(L307, 30, opened);
        var now = new DateTime(2026, 8, 5, 12, 37, 49);    // ~22h later — well within the 30-day safety bound

        var l = new ProductionCountLedger();
        long dropped = l.Restore(old.Snapshot(), now);

        Assert.Equal(0, dropped);                          // kept, not discarded
        Assert.Equal(30, l.TotalPanels);
        var w = Assert.Single(l.Due(now));
        Assert.Equal(opened.Date, w.Start.Date);           // written back on the real production day
    }

    [Fact]
    public void Only_a_truly_ancient_backlog_is_dropped()
    {
        // The 30-day bound is a last resort against abandoned/corrupt state, not a normal path.
        var opened = new DateTime(2026, 7, 1, 14, 0, 0);
        var old = Filled(L307, 30, opened);
        var now = new DateTime(2026, 8, 5, 12, 0, 0);      // ~35 days later — beyond the bound

        var l = new ProductionCountLedger();
        long dropped = l.Restore(old.Snapshot(), now);

        Assert.Equal(30, dropped);                         // reported to the caller (logged to file), never silent
        Assert.Empty(l.Due(now));
        Assert.Equal(0, l.TotalPanels);
    }

    [Fact]
    public void A_backlog_from_the_current_shift_survives_a_restart()
    {
        var opened = new DateTime(2026, 8, 5, 11, 30, 0);
        var recent = Filled(L307, 8, opened);
        var now = new DateTime(2026, 8, 5, 12, 37, 49);    // ~1h — same shift, these boards are still owed

        var l = new ProductionCountLedger();
        Assert.Equal(0, l.Restore(recent.Snapshot(), now));
        Assert.Equal(8, Assert.Single(l.Due(now.AddMinutes(5))).Panels);
    }

    [Fact]
    public void Turning_the_writer_off_discards_the_backlog_so_a_later_enable_starts_clean()
    {
        var t0 = new DateTime(2026, 8, 5, 12, 0, 0);
        var l = Filled(L307, 12, t0);

        Assert.Equal(12, l.Clear());                       // the flag came up false at load
        Assert.Empty(l.Due(t0.AddMinutes(5)));
    }

    [Fact]
    public void The_first_flush_after_enabling_writes_the_window_not_the_lot_to_date_total()
    {
        // The incident, as the ledger now sees it. The lot was 30 panels (120 boards) deep when the flag went
        // on; the ledger only ever holds what it observed FROM THEN ON, so the 12:37:49->12:42:39 window
        // reports the 6 panels actually produced in it, not the 30 the lot counter was showing.
        var enabled = new DateTime(2026, 8, 5, 12, 37, 49);
        var l = Filled(L307, 6, enabled);

        var w = Assert.Single(l.Due(new DateTime(2026, 8, 5, 12, 42, 39)));

        Assert.Equal(24, w.Panels * PerPanel);
        Assert.NotEqual(120, w.Panels * PerPanel);
        // Sanity against the machine: ~48s/cycle over a 4m50s window cannot be more than ~7 panels.
        Assert.True(w.Panels <= (w.End - w.Start).TotalSeconds / 48 + 1,
            $"{w.Panels}p in {(w.End - w.Start).TotalMinutes:0.0} min exceeds the machine's cycle rate");
    }

    // ---- a window that spans a lot change ----

    [Fact]
    public void A_lot_change_mid_window_splits_into_one_row_per_lot()
    {
        var t0 = new DateTime(2026, 8, 5, 12, 37, 49);
        var lotA = new ProductionKey("PO-55012", "L307", "A");
        var lotB = new ProductionKey("PO-55013", "L307", "A");
        var l = new ProductionCountLedger();

        for (int i = 0; i < 4; i++) l.AddPanel(lotA, t0.AddSeconds(48 * i));
        var changeover = t0.AddMinutes(3);
        for (int i = 0; i < 2; i++) l.AddPanel(lotB, changeover.AddSeconds(48 * i));

        var rows = l.Due(t0.AddMinutes(5));

        Assert.Equal(2, rows.Count);
        var a = Assert.Single(rows, r => r.Key.Lot == "PO-55012");
        var b = Assert.Single(rows, r => r.Key.Lot == "PO-55013");
        Assert.Equal(4, a.Panels);                         // boards are never misattributed across the change
        Assert.Equal(2, b.Panels);
        Assert.Equal(t0, a.Start);
        Assert.Equal(changeover, b.Start);                 // the new lot's window opens at its first board
        Assert.All(rows, r => Assert.True(r.Start <= r.End));
    }

    [Fact]
    public void A_side_change_mid_window_is_its_own_row()
    {
        var t0 = new DateTime(2026, 8, 5, 12, 37, 49);
        var l = new ProductionCountLedger();
        l.AddPanel(new ProductionKey("PO-55012", "L307", "A"), t0);
        l.AddPanel(new ProductionKey("PO-55012", "L307", "B"), t0.AddMinutes(2));

        var rows = l.Due(t0.AddMinutes(5));

        Assert.Equal(2, rows.Count);                       // a 2-sided lot shares its number but not its count
        Assert.Equal(1, Assert.Single(rows, r => r.Key.Side == "A").Panels);
        Assert.Equal(1, Assert.Single(rows, r => r.Key.Side == "B").Panels);
    }

    [Fact]
    public void Committing_one_lots_row_leaves_the_other_lot_owed()
    {
        var t0 = new DateTime(2026, 8, 5, 12, 37, 49);
        var lotA = new ProductionKey("PO-55012", "L307", "A");
        var lotB = new ProductionKey("PO-55013", "L307", "A");
        var l = new ProductionCountLedger();
        l.AddPanel(lotA, t0);
        l.AddPanel(lotB, t0.AddMinutes(3));

        l.Commit(lotA, 1, t0.AddMinutes(5));               // lot A's insert succeeded, lot B's threw

        var left = Assert.Single(l.Due(t0.AddMinutes(10)));
        Assert.Equal(lotB, left.Key);
        Assert.Equal(1, left.Panels);
    }
}
