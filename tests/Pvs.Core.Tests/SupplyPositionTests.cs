using Pvs.Core.Feeders;
using Xunit;

namespace Pvs.Core.Tests;

public class SupplyPositionTests
{
    // Every one of these strings is a real value observed in ProductBOM.SupplyPosition.
    [Theory]
    [InlineData("[F]116 (F)", 116, FeederSide.Front)]   // front, with the constant bracket prefix
    [InlineData("[F]114 (F)", 114, FeederSide.Front)]
    [InlineData("116 (F)", 116, FeederSide.Front)]      // front, WITHOUT the bracket prefix
    [InlineData("108 (F)", 108, FeederSide.Front)]
    [InlineData("[F]501 (R)", 501, FeederSide.Rear)]    // rear tray -- prefix is still [F], (R) wins
    [InlineData("F12", 12, FeederSide.Unspecified)]     // machine-1 style, no F/R marker
    [InlineData("F14", 14, FeederSide.Unspecified)]
    [InlineData("Z12", 12, FeederSide.Unspecified)]
    public void Parses_real_positions(string raw, int expectedNumber, FeederSide expectedSide)
    {
        var pos = SupplyPosition.Parse(raw);

        Assert.True(pos.IsAssigned);
        Assert.Equal(expectedNumber, pos.Number);
        Assert.Equal(expectedSide, pos.Side);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Blank_is_unassigned(string? raw)
    {
        var pos = SupplyPosition.Parse(raw);

        Assert.False(pos.IsAssigned);
        Assert.Null(pos.Number);
        Assert.Equal(FeederSide.Unspecified, pos.Side);
    }

    [Fact]
    // The [F] square-bracket prefix must never be read as "front" -- a rear tray row carries it too.
    public void Bracket_prefix_does_not_override_rear_marker()
    {
        var pos = SupplyPosition.Parse("[F]502 (R)");
        Assert.Equal(FeederSide.Rear, pos.Side);
        Assert.Equal(502, pos.Number);
    }

    [Fact]
    // The two spellings of the same physical feeder must resolve identically.
    public void Bracketed_and_bare_front_forms_are_equivalent()
    {
        var a = SupplyPosition.Parse("[F]116 (F)");
        var b = SupplyPosition.Parse("116 (F)");
        Assert.Equal(a.Number, b.Number);
        Assert.Equal(a.Side, b.Side);
    }
}
