using Pvs.Core.Serial;
using Xunit;

namespace Pvs.Core.Tests;

public class SonyMessageTests
{
    [Fact]
    public void Genuine_parts_out_decodes_feeder_and_step()
    {
        // Real frame from Cell 2, 2026-07-22 19:27.
        var m = SonyMessage.Parse("R2E03S000N0080Z124M000T000");
        Assert.Equal(MessageKind.PartsOut, m.Kind);
        Assert.Equal(124, m.Feeder);   // Z field
        Assert.Equal(80, m.Step);      // N field
    }

    [Fact]
    public void Alternation_and_pickup_are_distinct_kinds()
    {
        Assert.Equal(MessageKind.PartsOutAlternation, SonyMessage.Parse("R2E04S000N0080Z124M000T000").Kind);
        Assert.Equal(MessageKind.PickupError, SonyMessage.Parse("R2E02S000N0080Z124M000T000").Kind);
    }

    [Fact]
    public void Error_stop_has_no_feeder_when_z_is_zero()
    {
        // Real tray fault from Cell 3: R2E08 ... Z000 -> no specific feeder.
        var m = SonyMessage.Parse("R2E08S000N0004Z000M000T000");
        Assert.Equal(MessageKind.ErrorStop, m.Kind);
        Assert.Null(m.Feeder);
    }

    [Fact]
    public void Tray_feeder_position_in_5xx_range_is_read()
    {
        var m = SonyMessage.Parse("R2E02S000N0002Z504M000T000");
        Assert.Equal(504, m.Feeder);
    }

    [Theory]
    [InlineData("R0CT")]
    [InlineData("R0EP")]
    public void Board_complete_both_variants(string payload)
    {
        Assert.Equal(MessageKind.BoardComplete, SonyMessage.Parse(payload).Kind);
    }

    [Theory]
    [InlineData("R1OL", "OL")]
    [InlineData("R1ST", "ST")]
    [InlineData("R1SP", "SP")]
    [InlineData("R1RO", "RO")]
    [InlineData("R1SEP", "SEP")]   // 3-letter status code
    [InlineData("R1ER", "ER")]     // undocumented but real
    public void Status_messages_expose_their_code(string payload, string code)
    {
        var m = SonyMessage.Parse(payload);
        Assert.Equal(MessageKind.Status, m.Kind);
        Assert.Equal(code, m.StatusCode);
    }

    [Fact]
    public void Recovery_success_and_failure()
    {
        Assert.True(SonyMessage.Parse("R3GD").RecoveryOk);
        Assert.False(SonyMessage.Parse("R3NG").RecoveryOk);
    }

    [Theory]
    [InlineData("A4E01", MessageKind.Rejected, 1)]   // real: C5R0(zero) got this
    [InlineData("A4E02", MessageKind.Rejected, 2)]
    [InlineData("A1E02", MessageKind.Nak, 2)]
    [InlineData("A5E01", MessageKind.Busy, 1)]        // SI-F busy/retry
    public void Ack_error_codes(string payload, MessageKind kind, int code)
    {
        var m = SonyMessage.Parse(payload);
        Assert.Equal(kind, m.Kind);
        Assert.Equal(code, m.ErrorCode);
    }

    [Theory]
    [InlineData("A0")]
    [InlineData("A2")]
    [InlineData("A3")]
    public void Plain_acks(string payload)
    {
        Assert.Equal(MessageKind.Ack, SonyMessage.Parse(payload).Kind);
    }

    [Fact]
    public void Parts_out_burst_members_are_flagged_for_debounce()
    {
        // A real exhaust emits E02/E02/E04/E03 within ~4s, same feeder+step -> one event.
        Assert.True(SonyMessage.Parse("R2E02S000N0080Z124M000T000").IsPartsOutBurstMember);
        Assert.True(SonyMessage.Parse("R2E04S000N0080Z124M000T000").IsPartsOutBurstMember);
        Assert.True(SonyMessage.Parse("R2E03S000N0080Z124M000T000").IsPartsOutBurstMember);
        // ...but a plain status message is not part of the burst.
        Assert.False(SonyMessage.Parse("R1SP").IsPartsOutBurstMember);
    }

    // ---- SI-F transaction ID (trailing "…H<n>TI<digits>", §8.1 Table 8-2) ----

    [Fact]
    public void Board_complete_carries_transaction_id_and_head()
    {
        // R0CT + H1 (head, fixed) + TI + 11-digit right-aligned count.
        var m = SonyMessage.Parse("R0CTH1TI00000000042");
        Assert.Equal(MessageKind.BoardComplete, m.Kind);
        Assert.Equal("CT", m.StatusCode);
        Assert.Equal(42L, m.TxnId);
        Assert.Equal(1, m.Head);
    }

    [Fact]
    public void Transaction_id_parses_without_a_head_field()
    {
        var m = SonyMessage.Parse("R0EPTI00000000007");
        Assert.Equal(MessageKind.BoardComplete, m.Kind);
        Assert.Equal(7L, m.TxnId);
        Assert.Null(m.Head);
    }

    [Fact]
    public void Transaction_id_is_null_when_the_machine_sends_none()
    {
        // Older / non-SI-F frames have no trailing TI — the detector must stay inert, not guess.
        Assert.Null(SonyMessage.Parse("R0CT").TxnId);
        Assert.Null(SonyMessage.Parse("R1OL").TxnId);
    }

    [Fact]
    public void R2_time_field_is_not_mistaken_for_a_transaction_id()
    {
        // The R2 family ends in a "T<ttt>" TIME field. A ttt that starts with 1 ("T199") must NOT be read as a
        // txn — only the literal "TI" marker counts. This is the whole reason the regex matches "TI", not "T1".
        var m = SonyMessage.Parse("R2E03S000N0080Z124M000T199");
        Assert.Equal(MessageKind.PartsOut, m.Kind);
        Assert.Equal(124, m.Feeder);
        Assert.Null(m.TxnId);
    }

    [Fact]
    public void R2_error_still_decodes_with_a_real_transaction_id_appended()
    {
        var m = SonyMessage.Parse("R2E03S000N0080Z124M000T000H1TI00000000123");
        Assert.Equal(MessageKind.PartsOut, m.Kind);
        Assert.Equal(124, m.Feeder);     // fields before the txn still decode
        Assert.Equal(80, m.Step);
        Assert.Equal(123L, m.TxnId);
        Assert.Equal(1, m.Head);
    }
}
