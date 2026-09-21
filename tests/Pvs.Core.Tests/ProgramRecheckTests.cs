using Pvs.Core.Runtime;
using Xunit;

namespace Pvs.Core.Tests;

public class ProgramRecheckTests
{
    private static readonly DateTime T0 = new(2026, 9, 21, 17, 46, 0);

    [Theory]
    [InlineData("L307 - B SIDE _Cell4.PW4", "L307")]
    [InlineData("  l313 - B SIDE _Cell4.PW4", "L313")]
    [InlineData("D-CPU-IO-FTU B SIDE.PW4", "D")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void ModelOf_takes_the_leading_token(string? program, string expected) =>
        Assert.Equal(expected, ProgramNames.ModelOf(program));

    [Fact]
    public void Agreeing_machines_are_never_re_asked()
    {
        var set = ProgramRecheck.Disagreeing(new[]
        {
            (1, (string?)"L307 - B SIDE_Cell1.PW4", true, false), (2, "L307 - B SIDE_Cell2.PW4", true, false),
            (3, "L307 - B SIDE _Cell3.PW4", true, false), (4, "L307 - B SIDE _Cell4.PW4", true, false),
        });
        Assert.Empty(set);
    }

    [Fact]
    public void The_L1_M4_case_re_asks_every_machine_in_the_comparison()
    {
        var set = ProgramRecheck.Disagreeing(new[]
        {
            (1, (string?)"L307 - B SIDE_Cell1.PW4", true, false), (2, "L307 - B SIDE_Cell2.PW4", true, false),
            (3, "L307 - B SIDE _Cell3.PW4", true, false), (4, "L313 - B SIDE _Cell4.PW4", true, false),
        });
        Assert.Equal(new[] { 1, 2, 3, 4 }, set);
    }

    [Fact]
    public void Skipped_offline_and_nameless_machines_neither_raise_nor_join_a_recheck()
    {
        Assert.Empty(ProgramRecheck.Disagreeing(new[]
        {
            (1, (string?)"L307 - B.PW4", true, false), (2, "L307 - B.PW4", true, false),
            (3, "L313 - B.PW4", true, true),      // skipped by the supervisor
            (4, "L313 - B.PW4", false, false),    // off-line
        }));
        var set = ProgramRecheck.Disagreeing(new[]
        {
            (1, (string?)"L307 - B.PW4", true, false), (2, null, true, false), (3, "L313 - B.PW4", true, false),
        });
        Assert.Equal(new[] { 1, 3 }, set);
    }

    [Fact]
    public void Throttle_is_60_s_stopped_and_5_min_running_and_only_counts_a_sent_request()
    {
        var r = new ProgramRecheck();
        Assert.True(r.Due(4, T0, running: false));
        Assert.True(r.Due(4, T0.AddSeconds(1), running: false));    // nothing was SENT yet (held by a report read)
        r.MarkAsked(4, T0);
        Assert.False(r.Due(4, T0.AddSeconds(59), running: false));
        Assert.True(r.Due(4, T0.AddSeconds(60), running: false));
        Assert.False(r.Due(4, T0.AddMinutes(4), running: true));
        Assert.True(r.Due(4, T0.AddMinutes(5), running: true));
        Assert.True(r.Due(1, T0, running: true));                   // per machine
    }
}
