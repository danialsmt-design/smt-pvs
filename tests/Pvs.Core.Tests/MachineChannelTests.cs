using Pvs.Core.Events;
using Pvs.Core.Runtime;
using Pvs.Core.Serial;
using Xunit;

namespace Pvs.Core.Tests;

public class MachineChannelTests
{
    private static readonly DateTime T0 = new(2026, 7, 22, 19, 27, 0);

    // Builds a channel that records every frame it sends.
    private static (MachineChannel ch, List<string> sent) Make(int machine = 2)
    {
        var sent = new List<string>();
        var ch = new MachineChannel(machine, f => sent.Add(f.Trim(SonyFrame.STX, SonyFrame.ETX)));
        return (ch, sent);
    }

    private static string Frame(string payload) => SonyFrame.Build(payload);

    [Fact]
    public void Start_enables_real_time_with_C5RO()
    {
        var (ch, sent) = Make();
        ch.Start();
        Assert.Contains("04C5RO83", sent);
    }

    [Fact]
    public void Coming_online_re_enables_real_time()
    {
        var (ch, sent) = Make();
        ch.Feed(Frame("R1OL"), T0);
        Assert.True(ch.IsOnline);
        Assert.Contains("04C5RO83", sent);   // C5RO re-sent on R1OL
    }

    [Fact]
    public void Real_time_messages_are_auto_acknowledged()
    {
        var (ch, sent) = Make();
        ch.Feed(Frame("R0CT"), T0);
        Assert.Contains("02A22B", sent);     // A2 ack
    }

    [Fact]
    public void Board_complete_raises_event_and_feeds_rate()
    {
        var (ch, _) = Make();
        int boards = 0;
        ch.BoardCompleted += _ => boards++;

        ch.Feed(Frame("R1ST"), T0);
        ch.Feed(Frame("R0CT"), T0.AddSeconds(60));
        ch.Feed(Frame("R0CT"), T0.AddSeconds(120));

        Assert.Equal(2, boards);
        Assert.Equal(60.0, ch.BoardRate.BoardsPerHour!.Value, 1);
    }

    [Fact]
    public void Real_parts_out_burst_raises_exactly_one_event()
    {
        var (ch, _) = Make();
        var events = new List<PartsOutEvent>();
        ch.PartsOutDetected += e => events.Add(e);

        // The actual Cell-2 burst: E02 -> E02 -> E04 -> E03, feeder 124.
        ch.Feed(Frame("R2E02S000N0080Z124M000T000"), T0);
        ch.Feed(Frame("R2E02S000N0080Z124M000T000"), T0.AddSeconds(2));
        ch.Feed(Frame("R2E04S000N0080Z124M000T000"), T0.AddSeconds(2.1));
        ch.Feed(Frame("R2E03S000N0080Z124M000T000"), T0.AddSeconds(2.2));

        Assert.Single(events);
        Assert.Equal(124, events[0].Feeder);
        Assert.Equal(2, events[0].Machine);
    }

    [Fact]
    public void Frames_split_across_reads_are_reassembled()
    {
        var (ch, _) = Make();
        var seen = new List<SonyMessage>();
        ch.MessageReceived += (m, _) => seen.Add(m);

        string whole = Frame("R0CT");
        // Deliver the frame in two awkward halves.
        ch.Feed(whole.Substring(0, 3), T0);
        ch.Feed(whole.Substring(3), T0);

        Assert.Single(seen);
        Assert.Equal(MessageKind.BoardComplete, seen[0].Kind);
    }

    [Fact]
    public void Bad_checksum_frame_is_dropped()
    {
        var (ch, sent) = Make();
        var seen = new List<SonyMessage>();
        ch.MessageReceived += (m, _) => seen.Add(m);

        ch.Feed(SonyFrame.STX + "04R0CT00" + SonyFrame.ETX, T0);   // wrong checksum
        Assert.Empty(seen);
        Assert.Empty(sent);   // nothing acked
    }

    [Fact]
    public void Acks_are_not_themselves_acked()
    {
        var (ch, sent) = Make();
        ch.Feed(Frame("A2"), T0);   // an incoming A2 must not trigger an outgoing ack
        Assert.Empty(sent);
    }

    // ---- C1M production-report (machine's own completed-PWB counter) ----

    // SI-F 6.2.2: "C1Mmmm" ALONE = Entire Machine Status. The trailing P belongs to the
    // "C1MmmmP<data name>" form only — a bare "C1M000P" asks for a PWB file with an empty
    // name and the real machines reject it with A4E00 (observed live on Line 1, 2026-07-31).
    [Fact]
    public void RequestProductionCount_sends_bare_C1M000_for_entire_machine_status()
    {
        var (ch, sent) = Make();
        ch.RequestProductionCount(T0);
        Assert.Contains(sent, s => s.Contains("C1M000") && !s.Contains("C1M000P"));
        Assert.Equal("C1M000", ch.LastReportCommand);
    }

    [Fact]
    public void RequestProductionCount_with_a_pwb_name_uses_the_P_form()
    {
        var (ch, sent) = Make();
        ch.RequestProductionCount(T0, "L307 - B SIDE _Cell4");
        Assert.Contains(sent, s => s.Contains("C1M000PL307 - B SIDE _Cell4"));
        Assert.Equal("C1M000PL307 - B SIDE _Cell4", ch.LastReportCommand);
    }

    [Fact]
    public void A_refused_C1M_records_the_reject_code_and_ends_the_transfer()
    {
        var (ch, _) = Make();
        ch.RequestProductionCount(T0);
        Assert.Null(ch.LastReportError);

        ch.Feed(Frame("A4E00"), T0);          // machine refuses
        Assert.Equal("A4E00", ch.LastReportError);
        Assert.Null(ch.CompletedPwbs);

        // transfer is over: a later D0 is a normal data message again (e.g. a C3P program reply),
        // not report content
        ch.Feed(Frame("D0L307 - B SIDE _Cell4.PW4"), T0);
        Assert.Equal("L307 - B SIDE _Cell4.PW4", ch.ProgramName);
    }

    [Fact]
    public void A_new_request_clears_the_previous_reject_code()
    {
        var (ch, _) = Make();
        ch.RequestProductionCount(T0);
        ch.Feed(Frame("A4E00"), T0);
        Assert.Equal("A4E00", ch.LastReportError);

        ch.RequestProductionCount(T0, "L307 - B SIDE _Cell4");
        Assert.Null(ch.LastReportError);
    }

    [Fact]
    public void C1M_report_is_collected_acked_with_A0_and_PC_is_parsed()
    {
        var (ch, sent) = Make();
        int? read = null; ch.ProductionCountRead += v => read = v;

        ch.RequestProductionCount(T0);
        sent.Clear();   // ignore the C1M send; focus on the reply acking
        // machine streams the report as D0 lines...
        ch.Feed(Frame("D0SD2607311230"), T0);
        ch.Feed(Frame("D0ED2607311300"), T0);
        ch.Feed(Frame("D0PC00000300VC00000312TC00000310"), T0);
        ch.Feed(Frame("D0"), T0);   // terminator

        Assert.Equal(300, ch.CompletedPwbs);
        Assert.Equal(300, read);
        // each report line acked with A0 (not A2); the transfer closed with A2
        Assert.True(sent.Count(s => s.Contains("A0")) >= 3);
        Assert.Contains(sent, s => s.Contains("A2"));
    }

    [Fact]
    public void Board_completes_during_a_C1M_report_still_count_and_ack_normally()
    {
        var (ch, sent) = Make();
        int boards = 0; ch.BoardCompleted += _ => boards++;

        ch.RequestProductionCount(T0);
        ch.Feed(Frame("D0SD2607311230"), T0);       // report line -> A0
        ch.Feed(Frame("R0CT"), T0.AddSeconds(1));   // a real board-complete DURING the report
        ch.Feed(Frame("D0PC00000300"), T0.AddSeconds(1));
        ch.Feed(Frame("D0"), T0.AddSeconds(1));     // terminator

        Assert.Equal(1, boards);                    // the board still counted
        Assert.Contains(sent, s => s.Contains("A2"));   // the real-time R0 was A2-acked as usual
        Assert.Equal(300, ch.CompletedPwbs);
    }

    [Fact]
    public void RequestSupplyReport_sends_C1Z000_bare_and_with_pwb_name()
    {
        // Separate channels: with the "one report at a time" pipeline guard, a second RequestSupplyReport on the
        // SAME channel while the first is still collecting is intentionally a no-op — so test each command FORMAT
        // on its own fresh channel.
        var (ch1, sent1) = Make();
        ch1.RequestSupplyReport(T0);
        Assert.Contains(sent1, s => s.Contains("C1Z000") && !s.Contains("C1Z000P"));

        var (ch2, sent2) = Make();
        ch2.RequestSupplyReport(T0, "L307 - B SIDE _Cell4");
        Assert.Contains(sent2, s => s.Contains("C1Z000PL307 - B SIDE _Cell4"));
    }

    [Fact]
    public void C1Z_report_is_captured_raw_and_not_parsed_as_a_production_count()
    {
        var (ch, sent) = Make();
        int prodEvents = 0; ch.ProductionCountRead += _ => prodEvents++;
        string? raw = null; ch.SupplyReportRead += r => raw = r;

        ch.RequestSupplyReport(T0);
        // machine streams the per-feeder report as D0 lines (comma-delimited supply-location records)
        ch.Feed(Frame("D0  115,  0001200,  0001180,"), T0);
        ch.Feed(Frame("D0  0000020,  0000000,"), T0);
        ch.Feed(Frame("D0"), T0);   // terminator

        Assert.NotNull(raw);
        Assert.Contains("115,  0001200,  0001180", raw);
        Assert.Equal(raw, ch.RawSupplyReport);
        Assert.Equal(0, prodEvents);                 // a C1Z report must NOT fire the C1M production-count event
        Assert.True(sent.Count(s => s.Contains("A0")) >= 2);   // report lines acked A0
    }

    [Fact]
    public void A_stalled_C1M_report_times_out_and_reverts_to_normal_acking()
    {
        var (ch, sent) = Make();
        ch.RequestProductionCount(T0);
        ch.Feed(Frame("D0PC00000300"), T0);           // partial report (no terminator)
        sent.Clear();
        ch.Feed(Frame("R0CT"), T0.AddSeconds(9));      // >8s later: collection has timed out
        Assert.Contains(sent, s => s.Contains("A2"));  // back to normal A2 acking
        Assert.Equal(300, ch.CompletedPwbs);           // salvaged the count that had arrived
    }

    // ---- transaction-ID gap detection (serial blind-spot) ----

    [Fact]
    public void A_jump_in_the_transaction_id_flags_missed_messages()
    {
        var (ch, _) = Make();
        int gapMissed = 0;
        ch.CountGapSuspected += n => gapMissed = n;

        ch.Feed(Frame("R0CTH1TI00000000010"), T0);               // baseline
        ch.Feed(Frame("R0CTH1TI00000000013"), T0.AddSeconds(60)); // 11 and 12 never arrived

        Assert.Equal(2, gapMissed);
        Assert.Equal(2, ch.SuspectedMissedMessages);
        Assert.Equal(13L, ch.LastTxnId);
    }

    [Fact]
    public void Contiguous_transaction_ids_raise_no_gap()
    {
        var (ch, _) = Make();
        bool raised = false;
        ch.CountGapSuspected += _ => raised = true;

        ch.Feed(Frame("R0CTH1TI00000000010"), T0);
        ch.Feed(Frame("R0CTH1TI00000000011"), T0.AddSeconds(60));

        Assert.False(raised);
        Assert.Equal(0, ch.SuspectedMissedMessages);
    }

    [Fact]
    public void A_lower_transaction_id_rebaselines_and_is_not_a_gap()
    {
        var (ch, _) = Make();
        bool raised = false;
        ch.CountGapSuspected += _ => raised = true;

        ch.Feed(Frame("R0CTH1TI00000000100"), T0);
        ch.Feed(Frame("R0CTH1TI00000000005"), T0.AddSeconds(60)); // machine power-cycled its numbering

        Assert.False(raised);                         // a backwards step is a reset, not missed boards
        Assert.Equal(0, ch.SuspectedMissedMessages);
        Assert.Equal(5L, ch.LastTxnId);               // re-baselined to the new sequence
    }

    [Fact]
    public void Gap_detection_is_inert_when_the_machine_sends_no_transaction_id()
    {
        var (ch, _) = Make();
        bool raised = false;
        ch.CountGapSuspected += _ => raised = true;

        ch.Feed(Frame("R0CT"), T0);                   // no TI at all
        ch.Feed(Frame("R0CT"), T0.AddSeconds(60));

        Assert.False(raised);
        Assert.Equal(0, ch.SuspectedMissedMessages);
        Assert.Null(ch.LastTxnId);
    }

    // ---- serial pipeline: reports own the line; housekeeping is held off ----

    [Fact]
    public void Report_read_holds_off_C3P_housekeeping()
    {
        var (ch, sent) = Make();
        ch.RequestSupplyReport(DateTime.Now, "PROG", timeoutSec: 30);   // report in flight → owns the line
        sent.Clear();
        ch.RequestProgram();                                            // low-priority housekeeping — suppressed
        Assert.Empty(sent);
        Assert.True(ch.IsCollectingReport);
    }

    [Fact]
    public void Second_report_does_not_stomp_one_in_flight()
    {
        var (ch, sent) = Make();
        ch.RequestProductionCount(DateTime.Now, "P", timeoutSec: 8);    // C1M goes out
        Assert.Single(sent);
        sent.Clear();
        ch.RequestSupplyReport(DateTime.Now, "P", timeoutSec: 60);      // must no-op — one report at a time
        Assert.Empty(sent);
    }

    [Fact]
    public void SuppressHousekeeping_holds_off_C3P_for_the_window()
    {
        var (ch, sent) = Make();
        ch.SuppressHousekeeping(TimeSpan.FromSeconds(30));
        ch.RequestProgram();
        Assert.Empty(sent);   // within the suppression window, no C3P on the wire
    }

    [Fact]
    public void A_retransmitted_board_complete_is_not_counted_twice()
    {
        var (ch, _) = Make();
        ch.Inventory.Configure(108, "P", 4);
        ch.Inventory.LoadReel(108, "uid", 4000);
        int events = 0; ch.BoardCompleted += _ => events++;
        ch.Feed(Frame("R0CTH1TI00000000010"), T0);
        ch.Feed(Frame("R0CTH1TI00000000010"), T0.AddSeconds(1));   // same transaction id again = retransmit
        ch.Feed(Frame("R0CTH1TI00000000011"), T0.AddSeconds(40));
        Assert.Equal(2, ch.BoardsSeen);
        Assert.Equal(2, events);
        Assert.Equal(4000 - 2 * 4, ch.Inventory.Get(108)!.Remaining);
        Assert.Equal(1, ch.DuplicateMessages);
        Assert.Equal(11, ch.LastTxnId);
    }

    [Fact]
    public void A_lower_transaction_id_is_a_renumbering_not_a_duplicate()
    {
        var (ch, _) = Make();
        ch.Feed(Frame("R0CTH1TI00000000010"), T0);
        ch.Feed(Frame("R0CTH1TI00000000001"), T0.AddSeconds(40));   // machine restarted its numbering
        Assert.Equal(2, ch.BoardsSeen);
        Assert.Equal(0, ch.DuplicateMessages);
    }
}
