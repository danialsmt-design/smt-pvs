using Pvs.Core.Serial;
using Xunit;

namespace Pvs.Core.Tests;

public class SonyProductionReportTests
{
    // The counters group of a C1M report, concatenated as the machine sends it (PC first).
    private const string Counters = "PC00000300VC00000312TC00000310MC00000002DC00000000RC00000000PR00009900";

    [Fact]
    public void Reads_completed_pwbs_from_the_PC_field()
    {
        Assert.Equal(300, SonyProductionReport.CompletedPwbs(Counters));
    }

    [Fact]
    public void Reads_completed_pwbs_from_a_full_report_stream()
    {
        // start/end time lines + counters + stops + times, all concatenated as collected across D0 messages
        string report = "SD2607311230ED2607311300" + Counters + "EP00000001MP00000000TP00000000PT00001234";
        Assert.Equal(300, SonyProductionReport.CompletedPwbs(report));
    }

    [Fact]
    public void Other_fields_are_readable_and_distinct_from_PC()
    {
        Assert.Equal(312, SonyProductionReport.Field(Counters, "VC"));   // attempted pickups
        Assert.Equal(2,   SonyProductionReport.Field(Counters, "MC"));   // missed pickup errors
    }

    [Fact]
    public void Missing_field_and_empty_input_return_null()
    {
        Assert.Null(SonyProductionReport.CompletedPwbs(""));
        Assert.Null(SonyProductionReport.CompletedPwbs(null));
        Assert.Null(SonyProductionReport.Field("VC00000312", "PC"));
    }

    [Fact]
    public void A_code_embedded_in_a_longer_letter_run_is_not_a_false_hit()
    {
        // "XPC00000009" should NOT match PC (preceded by a letter X), but a real "..0PC00000300" should.
        Assert.Null(SonyProductionReport.CompletedPwbs("XPC00000009"));
        Assert.Equal(300, SonyProductionReport.CompletedPwbs("ED0000000002PC00000300"));
    }
}
