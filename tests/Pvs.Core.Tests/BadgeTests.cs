using Pvs.Core.People;
using Xunit;

namespace Pvs.Core.Tests;

public class BadgeTests
{
    [Theory]
    // Real AccessLevel strings from the Users table.
    [InlineData("Supervisor (L2)", 2, BadgeRole.Supervisor)]
    [InlineData("Manager (L3)", 3, BadgeRole.Manager)]
    [InlineData("Operator (L1)", 1, BadgeRole.Operator)]   // to be added later
    public void Parses_level_and_role(string accessLevel, int level, BadgeRole role)
    {
        var b = new Badge("14031", "Noraziah", accessLevel);
        Assert.Equal(level, b.Level);
        Assert.Equal(role, b.Role);
    }

    [Fact]
    public void Operator_can_operate_but_cannot_release_interlock()
    {
        var op = new Badge("00001", "Op", "Operator (L1)");
        Assert.True(op.CanOperate);
        Assert.False(op.CanReleaseInterlock);   // the whole point of the interlock
    }

    [Theory]
    [InlineData("Supervisor (L2)")]
    [InlineData("Manager (L3)")]
    public void Supervisor_and_above_can_release_interlock(string accessLevel)
    {
        Assert.True(new Badge("x", "y", accessLevel).CanReleaseInterlock);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Contractor")]        // no level marker
    [InlineData("guest")]
    public void Unrecognised_level_grants_nothing(string accessLevel)
    {
        var b = new Badge("x", "y", accessLevel);
        Assert.Equal(0, b.Level);
        Assert.Equal(BadgeRole.Unknown, b.Role);
        Assert.False(b.CanOperate);
        Assert.False(b.CanReleaseInterlock);
    }

    [Fact]
    public void Level_check_is_label_independent()
    {
        // A future/renamed L2 label must still grant release (level drives it, not the word).
        Assert.True(new Badge("x", "y", "Team Lead (L2)").CanReleaseInterlock);
    }
}
