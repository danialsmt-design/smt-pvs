using Pvs.Core.Serial;
using Xunit;

namespace Pvs.Core.Tests;

public class SonyFrameTests
{
    [Fact]
    public void Checksum_matches_manual_worked_example()
    {
        // Manual section 5.2.5: payload "A0" -> frame "02A02D".
        Assert.Equal("02A02D", SonyFrame.Build("A0").Trim(SonyFrame.STX, SonyFrame.ETX));
    }

    [Theory]
    // Every one of these is a real payload/frame captured from live machines (2026-07-22/23).
    [InlineData("C5RO", "04C5RO83")]
    [InlineData("R1OL", "04R1OL7E")]
    [InlineData("A4E02", "05A4E027F")]
    [InlineData("R0CT", "04R0CT83")]
    [InlineData("A2", "02A22B")]
    [InlineData("R2E03S000N0080Z124M000T000", "1AR2E03S000N0080Z124M000T000B7")]
    public void Build_reproduces_real_frames(string payload, string expectedInner)
    {
        Assert.Equal(expectedInner, SonyFrame.Build(payload).Trim(SonyFrame.STX, SonyFrame.ETX));
    }

    [Theory]
    // TryParse tolerates the sentinels being absent, so we can pass the inner directly.
    [InlineData("04R0CT83", "R0CT")]
    [InlineData("1AR2E03S000N0080Z124M000T000B7", "R2E03S000N0080Z124M000T000")]
    public void TryParse_accepts_valid_frames(string raw, string expectedPayload)
    {
        Assert.True(SonyFrame.TryParse(raw, out var payload));
        Assert.Equal(expectedPayload, payload);
    }

    [Fact]
    public void TryParse_rejects_bad_checksum()
    {
        Assert.False(SonyFrame.TryParse("04R0CT00", out _)); // 00 is wrong (should be 83)
    }

    [Fact]
    public void TryParse_rejects_wrong_length()
    {
        Assert.False(SonyFrame.TryParse("99R0CT83", out _)); // count says 0x99 chars
    }

    [Fact]
    public void Extract_pulls_multiple_frames_and_keeps_partial_remainder()
    {
        // Two whole frames, then the start of a third with no ETX yet.
        string partial = SonyFrame.STX + "04R1ST";
        string buffer = SonyFrame.Build("R0CT") + SonyFrame.Build("R1SP") + partial;
        var frames = SonyFrame.Extract(buffer, out var remainder);

        Assert.Equal(2, frames.Count);
        Assert.True(SonyFrame.TryParse(frames[0], out var p0));
        Assert.Equal("R0CT", p0);
        Assert.Equal(partial, remainder); // held for the next read
    }

    [Fact]
    public void Extract_discards_leading_junk_before_stx()
    {
        var frames = SonyFrame.Extract("garbage" + SonyFrame.Build("A2"), out _);
        Assert.Single(frames);
        Assert.True(SonyFrame.TryParse(frames[0], out var p));
        Assert.Equal("A2", p);
    }
}
