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

    [Fact]
    public void RequestProductionCount_sends_C1M000()
    {
        var (ch, sent) = Make();
        ch.RequestProductionCount(T0);
        Assert.Contains(sent, s => s.Contains("C1M000"));
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
}
