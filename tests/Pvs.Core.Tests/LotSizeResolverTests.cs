using Pvs.Core.Data;
using Pvs.Core.Runtime;
using Xunit;

namespace Pvs.Core.Tests;

/// <summary>
/// The lot size is the figure everything else is judged against, and DeliveryDocuments changes which column
/// holds it when the order is closed out as Delivered. Getting that wrong makes a delivered lot read as zero,
/// which turns every board it produced into "over-production".
/// </summary>
public class LotSizeResolverTests
{
    [Fact]
    public void A_plain_order_uses_Quantity()
    {
        var s = LotSizeResolver.Resolve(new LotSizeRow("HC20789020000", "Planned", Quantity: 3000, RevisedQty: null, DeliveredQty: null));
        Assert.Equal(3000, s.Boards);
        Assert.Equal(LotSizeSource.Quantity, s.Source);
        Assert.True(s.IsKnown);
        Assert.False(s.Delivered);
    }

    [Fact]
    public void A_revised_order_prefers_RevisedQty_over_Quantity()
    {
        var s = LotSizeResolver.Resolve(new LotSizeRow("HC20789020000", "Planned", Quantity: 3000, RevisedQty: 2500, DeliveredQty: null));
        Assert.Equal(2500, s.Boards);
        Assert.Equal(LotSizeSource.RevisedQty, s.Source);
    }

    [Fact]
    public void A_zero_RevisedQty_falls_back_to_Quantity()
    {
        // 0 means "not revised", not "revised down to nothing".
        var s = LotSizeResolver.Resolve(new LotSizeRow("HC20789020000", "Planned", Quantity: 3000, RevisedQty: 0, DeliveredQty: null));
        Assert.Equal(3000, s.Boards);
        Assert.Equal(LotSizeSource.Quantity, s.Source);
    }

    [Fact]
    public void A_delivered_lot_uses_DeliveredQty_even_though_RevisedQty_was_zeroed()
    {
        // The case that reads as ZERO under the live rule when Quantity has been cleared out too.
        var s = LotSizeResolver.Resolve(new LotSizeRow("HC20789020000", "Delivered", Quantity: 0, RevisedQty: 0, DeliveredQty: 2480));
        Assert.Equal(2480, s.Boards);
        Assert.Equal(LotSizeSource.DeliveredQty, s.Source);
        Assert.True(s.Delivered);
        Assert.True(s.IsKnown);
    }

    [Fact]
    public void A_delivered_lot_prefers_DeliveredQty_over_a_stale_Quantity()
    {
        var s = LotSizeResolver.Resolve(new LotSizeRow("HC20789020000", " delivered ", Quantity: 3000, RevisedQty: 0, DeliveredQty: 2480));
        Assert.Equal(2480, s.Boards);   // status match is trimmed + case-insensitive
        Assert.Equal(LotSizeSource.DeliveredQty, s.Source);
    }

    [Fact]
    public void A_delivered_lot_without_DeliveredQty_falls_back_and_says_so()
    {
        // The column may not exist on every deployment; degrade to the live rule rather than to zero.
        var s = LotSizeResolver.Resolve(new LotSizeRow("HC20789020000", "Delivered", Quantity: 3000, RevisedQty: null, DeliveredQty: null));
        Assert.Equal(3000, s.Boards);
        Assert.Equal(LotSizeSource.Quantity, s.Source);
        Assert.Contains("verify", s.Note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_row_with_nothing_positive_is_unknown_not_zero()
    {
        // A zero target would make every comparison read as over-production AND would cap every adoption at 0.
        var s = LotSizeResolver.Resolve(new LotSizeRow("HC20789020000", "Delivered", Quantity: 0, RevisedQty: 0, DeliveredQty: 0));
        Assert.False(s.IsKnown);
        Assert.Equal(LotSizeSource.Unknown, s.Source);
        Assert.Null(s.Boards);
    }

    [Fact]
    public void A_lot_that_is_not_in_DeliveryDocuments_is_unknown()
    {
        var s = LotSizeResolver.Resolve(null);
        Assert.False(s.IsKnown);
        Assert.Equal(LotSizeSource.Unknown, s.Source);
    }

    [Theory]
    [InlineData("Delivered", true)]
    [InlineData("delivered", true)]
    [InlineData("  Delivered  ", true)]
    [InlineData("Planned", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Delivered_status_is_matched_loosely(string? status, bool expected) =>
        Assert.Equal(expected, LotSizeResolver.IsDelivered(status));
}
