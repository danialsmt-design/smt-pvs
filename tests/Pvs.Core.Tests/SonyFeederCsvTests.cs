using Pvs.Core.Feeders;
using Xunit;

namespace Pvs.Core.Tests;

public class SonyFeederCsvTests
{
    // Sony export (row 2 has a stray comma in the cassette column).
    const string Sony =
        ";Document Main Title,Feeder List,,,,,,,\n" +
        ";Comment,L264 - A SIDE MC2,,,,,,,\n" +
        ";Cell #,Supply Pos.,Part Code,Polarity,Mount Step,Feed Type,Parts Pitch,Cassette Type,Description\n" +
        "2,[F]108 (F),VR8-1300-123,[+],18,Tape ( 8mm),2,Paper,1005R (CHIP RESISTER)\n" +
        "2,[F]109 (F),WA2-2419-000,[+],18,Tape ( 8mm),4,Emboss, (TRANSISTOR)\n" +
        "2,[F]121 (F),VR8-2860-391,[+],12,Tape ( 8mm),2,Paper,1005R (CHIP RESISTER)\n";

    // JUKI RS-1 export — different layout, no Cell# column, "F11" feeders, blank trailing rows.
    const string Juki =
        "FEEDER LIST  JUKI RS-1,,,,\n" +
        "PROGRAM NAME : L264 - A SIDE,,,,\n" +
        ",,,,\n" +
        "FEEDER NO,PARTS NAME,QTY,FEEDER TYPE,\n" +
        "F11,VE3-1480-104,84,8mm,\n" +
        "F12,VR8-1300-513,24,8mm,\n" +
        "F13,WA1-8717-000,6,8mm,\n" +
        ",,,,\n" +
        ",,,,\n";

    [Fact]
    public void Sony_parses_data_rows_and_skips_header()
    {
        var e = SonyFeederCsv.Parse(Sony, 2);
        Assert.Equal(3, e.Count);
        Assert.All(e, x => Assert.Equal(2, x.Machine));
        Assert.Equal(108, e[0].Feeder); Assert.Equal("VR8-1300-123", e[0].Part);
        Assert.Equal("WA2-2419-000", e[1].Part);   // stray comma in a later column doesn't shift the first fields
        Assert.Equal("L264 - A SIDE MC2", SonyFeederCsv.Comment(Sony));
        Assert.Equal(2, SonyFeederCsv.DeclaredCell(Sony));
    }

    [Fact]
    public void Juki_parses_its_own_layout_and_takes_the_cell_from_the_caller()
    {
        var e = SonyFeederCsv.Parse(Juki, 1);   // JUKI has no Cell# column -> machine comes from the button
        Assert.Equal(3, e.Count);
        Assert.All(e, x => Assert.Equal(1, x.Machine));
        Assert.Equal(11, e[0].Feeder); Assert.Equal("VE3-1480-104", e[0].Part);
        Assert.Equal(13, e[2].Feeder); Assert.Equal("WA1-8717-000", e[2].Part);
        Assert.Null(SonyFeederCsv.DeclaredCell(Juki));         // no cell to validate
        Assert.Equal("L264 - A SIDE", SonyFeederCsv.Comment(Juki));
    }

    // The "FEEDER LIST CANON" JUKI export (Line 3 L254): leading comma, "Z"-feeders, part padded out to column 3,
    // QTY/DESCRIPTION/FEEDER TYPE columns after it, and a MODEL NAME on the PROGRAM line.
    const string JukiCanon =
        ",FEEDER LIST  JUKI RS-1,,,,,,,,,,,,,,,,,,\n" +
        "\n" +
        "PROGRAM NAME : L254,,,,,,,,,,,,,,,,MODEL NAME : YG2 - 3290 - 009,,,\n" +
        "\n" +
        "FEEDER NO,,,PARTS NAME,,,,,,QTY,,,,DESCRIPTION,,,,,,FEEDER TYPE\n" +
        "Z8,,,VW5-6454-104,,,,,,4,,,,,,,,,,8x4\n" +
        "Z10,,,WA6-2564-000,,,,,,4,,,,,,,,,,8x2\n" +
        "Z25,,,WA6-3110-000,,,,,,124,,,,,,,,,,8x2\n" +
        ",,,,,,,,,,,,,,,,,,,RoHS/REACH Compliance,\n";

    [Fact]
    public void Juki_canon_export_reads_padded_columns_and_Z_feeders()
    {
        var e = SonyFeederCsv.Parse(JukiCanon, 1);
        Assert.Equal(3, e.Count);                               // 3 data rows; title/header/footer skipped
        Assert.All(e, x => Assert.Equal(1, x.Machine));
        Assert.Equal(8, e[0].Feeder);  Assert.Equal("VW5-6454-104", e[0].Part);   // part is in column 3, not 1
        Assert.Equal(10, e[1].Feeder); Assert.Equal("WA6-2564-000", e[1].Part);
        Assert.Equal(25, e[2].Feeder); Assert.Equal("WA6-3110-000", e[2].Part);
        Assert.Null(SonyFeederCsv.DeclaredCell(JukiCanon));    // no numeric cell -> no CELL-MISMATCH
        Assert.Equal("L254", SonyFeederCsv.Comment(JukiCanon)); // label trimmed at the first column
    }

    [Fact]
    public void Empty_or_header_only_is_empty()
    {
        Assert.Empty(SonyFeederCsv.Parse("", 1));
        Assert.Empty(SonyFeederCsv.Parse("FEEDER NO,PARTS NAME,QTY,FEEDER TYPE,", 1));
        Assert.Null(SonyFeederCsv.DeclaredCell(null));
    }
}
