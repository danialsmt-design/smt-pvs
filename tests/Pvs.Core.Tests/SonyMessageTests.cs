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
}
