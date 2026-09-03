using System.Linq;
using Pvs.Core.Serial;
using Xunit;

namespace Pvs.Core.Tests;

public class SonySupplyReportTests
{
    // A verbatim slice of a real C1Z capture from Line 1 M1 (2026-08-27 13:18→16:42), including F114
    // (WA2-2419-000) and the trailing "*<pwb name>" echo the machine appends.
    private const string Real =
        "SD2608271318ED2608271642" +
        "Z110,VC00001893,TC00001893,MC00000000,DC00000000,RC00000005,SC00000000,NC00000000,BC00000000,EP00000000,PR00009973" +
        "Z111,VC00002364,TC00002360,MC00000004,DC00000000,RC00000000,SC00000000,NC00000000,BC00000000,EP00000000,PR00009983" +
        "Z114,VC00001903,TC00001891,MC00000007,DC00000005,RC00000003,SC00000000,NC00000000,BC00000000,EP00000000,PR00009921" +
        "Z199,VC00000000,TC00000000,MC00000000,DC00000000,RC00000000,SC00000000,NC00000000,BC00000000,EP00000000,PR00000000" +
        "*L307 - B SIDE_Cell1.PW4";

    [Fact]
    public void Parses_tabulation_window()
    {
        var r = SonySupplyReport.Parse(Real);
        Assert.Equal(new System.DateTime(2026, 8, 27, 13, 18, 0), r.Start);
        Assert.Equal(new System.DateTime(2026, 8, 27, 16, 42, 0), r.End);
    }

    [Fact]
    public void Parses_every_feeder_record_and_ignores_the_trailing_pwb_name()
    {
        var r = SonySupplyReport.Parse(Real);
        Assert.Equal(4, r.Feeders.Count);
        Assert.Equal(new[] { 110, 111, 114, 199 }, r.Feeders.Select(f => f.SupplyLocation).ToArray());
    }

    [Fact]
    public void F114_counters_match_the_capture()
    {
        var f = SonySupplyReport.Parse(Real).Feeders.Single(x => x.SupplyLocation == 114);
        Assert.Equal(1903, f.Attempted);
        Assert.Equal(1891, f.Successful);
        Assert.Equal(7, f.Missed);
        Assert.Equal(5, f.Abnormal);
        Assert.Equal(3, f.Recognition);
        Assert.Equal(0, f.PartsOut);
        Assert.Equal(9921, f.RateHundredthsPct);   // 99.21 %
        Assert.Equal(15, f.Errors);                 // 7 + 5 + 3
        Assert.True(f.HasActivity);
    }

    [Fact]
    public void Tag_relationships_hold_on_every_active_feeder()
    {
        // The two invariants that identify the tags: TC = VC−MC−DC, and PR = (VC−MC−DC−RC)/VC in 1/100 %.
        foreach (var f in SonySupplyReport.Parse(Real).Feeders.Where(x => x.Attempted > 0))
        {
            Assert.Equal(f.Attempted - f.Missed - f.Abnormal, f.Successful);
            // The machine TRUNCATES the rate (Z110: 1888/1893 = 9973.58 reported as 9973), so floor here too.
            int expectRate = (int)(
                (f.Attempted - f.Missed - f.Abnormal - f.Recognition) * 10000.0 / f.Attempted);
            Assert.Equal(expectRate, f.RateHundredthsPct);
        }
    }

    [Fact]
    public void Idle_feeder_parses_as_no_activity()
    {
        var f = SonySupplyReport.Parse(Real).Feeders.Single(x => x.SupplyLocation == 199);
        Assert.False(f.HasActivity);
        Assert.Equal(0, f.Attempted);
    }

    [Fact]
    public void Empty_or_garbage_input_is_safe()
    {
        Assert.Empty(SonySupplyReport.Parse(null).Feeders);
        Assert.Empty(SonySupplyReport.Parse("").Feeders);
        Assert.Empty(SonySupplyReport.Parse("A4E00").Feeders);
    }
}
