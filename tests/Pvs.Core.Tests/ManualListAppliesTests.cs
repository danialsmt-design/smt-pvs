using Pvs.Core.Runtime;
using Xunit;

namespace Pvs.Core.Tests;

public class ManualListAppliesTests
{
    [Theory]
    [InlineData("L307 - B SIDE _Cell1.PW4", "L307", "B")]
    [InlineData("L307 B SIDE MC1", "L307", "B")]
    [InlineData("L311MBu - A SIDE _Cell3", "L311MBU", "A")]
    [InlineData("L311 A SIDE", "L311", "A")]
    [InlineData("L347 - B SIDE", "L347", "B")]
    [InlineData("L313 - A SIDE_Cell1.PW4", "L313", "A")]
    public void Model_and_side_are_read_from_program_names_and_file_labels(string text, string model, string side)
    {
        var r = ProgramNames.ModelSideOf(text);
        Assert.NotNull(r);
        Assert.Equal(model, r!.Value.Model);
        Assert.Equal(side, r.Value.Side);
    }

    [Fact]
    public void No_model_token_means_no_answer()
    {
        Assert.Null(ProgramNames.ModelSideOf("D-CPU-IO-FTU B SIDE.PW4"));
        Assert.Null(ProgramNames.ModelSideOf(""));
        Assert.Null(ProgramNames.ModelSideOf(null));
    }

    [Fact]
    public void Pen_drive_list_applies_only_while_the_machine_runs_that_model_and_side()
    {
        Assert.True(ProgramNames.ManualListApplies("L307 - B SIDE _Cell1.PW4", "L307 B SIDE MC1"));       // L1 today
        Assert.True(ProgramNames.ManualListApplies("L311MBU - A SIDE _Cell3.PW4", "L311MBu - A SIDE _Cell3"));
        Assert.False(ProgramNames.ManualListApplies("L313 - B SIDE _Cell1.PW4", "L307 B SIDE MC1"));      // model changed → DB
        Assert.False(ProgramNames.ManualListApplies("L307 - A SIDE _Cell1.PW4", "L307 B SIDE MC1"));      // side changed → DB
        Assert.False(ProgramNames.ManualListApplies("L311 - B SIDE _Cell2.PW4", "L311MBu - A SIDE _Cell2"));  // variant + side differ
    }

    [Fact]
    public void Unknown_program_or_unlabelled_file_keeps_the_list_until_it_can_be_judged()
    {
        Assert.True(ProgramNames.ManualListApplies(null, "L307 B SIDE MC1"));
        Assert.True(ProgramNames.ManualListApplies("", "L307 B SIDE MC1"));
        Assert.True(ProgramNames.ManualListApplies("L307 - B SIDE _Cell1.PW4", "file (12)"));
    }
}
