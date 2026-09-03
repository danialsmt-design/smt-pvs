using System;
using System.Linq;
using Pvs.Core.Boards;
using Xunit;

namespace Pvs.Core.Tests;

public class MagazineSlipParserTests
{
    [Fact]
    public void Parses_the_MCS_qr_format()
    {
        var s = MagazineSlipParser.TryParse("PO: HC20792053000 | QTY: 1800 | MAG: 3/10 | DATE: 18/08/2026");
        Assert.NotNull(s);
        Assert.Equal("HC20792053000", s!.Po);
        Assert.Equal(1800, s.QtyTotal);
        Assert.Equal(3, s.MagNo);
        Assert.Equal(10, s.MagTotal);
        Assert.Equal("18/08/2026", s.Date);
    }

    [Fact]
    public void Per_magazine_is_total_divided_by_card_count()
    {
        var s = MagazineSlipParser.TryParse("PO: X | QTY: 1800 | MAG: 3/10 | DATE: 1/1/2026");
        Assert.Equal(180, s!.PerMagazine);           // 1800 / 10
        Assert.Equal("X|3", s.Key);
    }

    [Fact]
    public void Tolerates_extra_fields_reorder_and_commas()
    {
        var s = MagazineSlipParser.TryParse("MAG: 2/6 | PO: P1 | SIDE: A | QTY: 1,200 PCS | DATE: 9/9/2026 | EXTRA: z");
        Assert.NotNull(s);
        Assert.Equal("P1", s!.Po);
        Assert.Equal(1200, s.QtyTotal);
        Assert.Equal(2, s.MagNo);
        Assert.Equal(6, s.MagTotal);
        Assert.Equal(200, s.PerMagazine);
    }

    [Theory]
    [InlineData("VW5-6454-104")]                         // a part number
    [InlineData("970106D248%")]                          // a reel UID
    [InlineData("PO: X | QTY: 5")]                        // no MAG
    [InlineData("PO: X | QTY: 5 | MAG: 3/0")]            // bad card count
    [InlineData("")]
    [InlineData(null)]
    public void Non_magazine_scans_return_null(string? scan)
    {
        Assert.Null(MagazineSlipParser.TryParse(scan));
    }
}

public class BoardInputLogTests
{
    private static DateTime T(int m) => new(2026, 8, 19, 8, m, 0);

    [Fact]
    public void Dedupes_by_key_and_sums_pcs()
    {
        var log = new BoardInputLog();
        Assert.True(log.Add(new BoardToken("magazine", "PO1|1", "MAG 1/10", 180, T(1)), 10));
        Assert.True(log.Add(new BoardToken("magazine", "PO1|2", "MAG 2/10", 180, T(2)), 10));
        Assert.False(log.Add(new BoardToken("magazine", "PO1|1", "MAG 1/10", 180, T(3)), 10)); // repeat magazine
        Assert.Equal(2, log.Count);
        Assert.Equal(360, log.TotalPcs);
        Assert.Equal(2, log.MagazinesScanned);
        Assert.Equal(10, log.ExpectedMagazines);
    }

    [Fact]
    public void Packs_and_magazines_coexist()
    {
        var log = new BoardInputLog();
        log.Add(new BoardToken("pack", "UID123", "YH4-3225-008", 50, T(1)));
        log.Add(new BoardToken("magazine", "PO1|1", "MAG 1/4", 75, T(2)), 4);
        Assert.Equal(125, log.TotalPcs);
        Assert.Equal(1, log.MagazinesScanned);
        Assert.Equal(4, log.ExpectedMagazines);
    }

    [Fact]
    public void Reset_and_restore_round_trip()
    {
        var log = new BoardInputLog();
        log.Add(new BoardToken("magazine", "PO1|1", "MAG 1/3", 100, T(1)), 3);
        log.Reset();
        Assert.Equal(0, log.Count);
        Assert.Equal(0, log.ExpectedMagazines);

        var r = new BoardInputLog();
        r.Restore(new[] { new BoardToken("magazine", "PO1|1", "MAG 1/3", 100, T(1)) }, 3);
        Assert.Equal(1, r.Count);
        Assert.Equal(100, r.TotalPcs);
        Assert.Equal(3, r.ExpectedMagazines);
        Assert.True(r.Has("PO1|1"));
    }
}
