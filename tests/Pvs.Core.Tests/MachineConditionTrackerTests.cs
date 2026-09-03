using Pvs.Core.Events;
using Pvs.Core.Serial;

namespace Pvs.Core.Tests;

public class MachineConditionTrackerTests
{
    private static SonyMessage M(string payload) => SonyMessage.Parse(payload);
    private static readonly DateTime T = new(2026, 8, 21, 10, 0, 0);

    [Fact]
    public void Starts_unknown_not_stopped()
    {
        var t = new MachineConditionTracker();
        Assert.Equal(MachineCondition.Unknown, t.Condition);
        Assert.False(t.IsRunning);
    }

    [Fact]
    public void ST_mounts_SP_stops()
    {
        var t = new MachineConditionTracker();
        t.Observe(M("R1ST"), T);
        Assert.Equal(MachineCondition.Mounting, t.Condition);
        Assert.True(t.IsRunning);
        t.Observe(M("R1SP"), T.AddSeconds(60));
        Assert.Equal(MachineCondition.Stopped, t.Condition);
        Assert.False(t.IsRunning);
    }

    [Fact]
    public void PW_is_starved_but_still_running_PE_resumes_mounting()
    {
        var t = new MachineConditionTracker();
        t.Observe(M("R1ST"), T);
        t.Observe(M("R1PW"), T.AddSeconds(5));
        Assert.Equal(MachineCondition.Starved, t.Condition);
        Assert.True(t.IsRunning);   // in auto, just waiting for a board
        t.Observe(M("R1PE"), T.AddSeconds(10));
        Assert.Equal(MachineCondition.Mounting, t.Condition);
    }

    [Fact]
    public void BoardComplete_forces_mounting_even_after_stop()
    {
        var t = new MachineConditionTracker();
        t.Observe(M("R1SP"), T);
        Assert.Equal(MachineCondition.Stopped, t.Condition);
        t.Observe(M("R0CT"), T.AddSeconds(1));
        Assert.Equal(MachineCondition.Mounting, t.Condition);
        Assert.Equal(T.AddSeconds(1), t.LastBoardAt);
    }

    [Fact]
    public void Estop_manual_offline_map_correctly_and_are_not_running()
    {
        var t = new MachineConditionTracker();
        t.Observe(M("R1ES"), T);
        Assert.Equal(MachineCondition.EmergencyStop, t.Condition);
        t.Observe(M("R1MA"), T.AddSeconds(1));
        Assert.Equal(MachineCondition.NotAuto, t.Condition);
        t.Observe(M("R1FL"), T.AddSeconds(2));
        Assert.Equal(MachineCondition.Offline, t.Condition);
        Assert.False(t.IsRunning);
    }

    [Fact]
    public void AU_from_offline_becomes_idle_but_does_not_downgrade_mounting()
    {
        var t = new MachineConditionTracker();
        t.Observe(M("R1FL"), T);
        t.Observe(M("R1AU"), T.AddSeconds(1));
        Assert.Equal(MachineCondition.Idle, t.Condition);
        t.Observe(M("R1ST"), T.AddSeconds(2));
        t.Observe(M("R1AU"), T.AddSeconds(3));       // AU must not knock a running machine back to idle
        Assert.Equal(MachineCondition.Mounting, t.Condition);
    }

    [Fact]
    public void Since_advances_only_on_a_real_change()
    {
        var t = new MachineConditionTracker();
        t.Observe(M("R1ST"), T);
        var since = t.Since;
        t.Observe(M("R0CT"), T.AddSeconds(30));       // still mounting -> Since unchanged
        Assert.Equal(since, t.Since);
        t.Observe(M("R1SP"), T.AddSeconds(60));
        Assert.Equal(T.AddSeconds(60), t.Since);
    }
}
