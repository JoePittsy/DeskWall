using DeskWall.Core.Scheduling;
using Xunit;

namespace DeskWall.Core.Tests.Scheduling;

public class TickPlanTests
{
    private static TickPlan.Decision From(params WakeKind[] kinds)
        => TickPlan.From(kinds.Select(k => new WakeReason(k)).ToList());

    [Fact]
    public void No_Reasons_Is_Not_A_Tick()
    {
        // WaitAndPump returns an empty list for any unrelated window message.
        var d = TickPlan.From([]);
        Assert.False(d.Tick);
        Assert.False(d.Force);
        Assert.False(d.DelayForExplorer);
        Assert.False(d.Reactivate);
        Assert.False(d.Shutdown);
    }

    [Fact]
    public void Timer_Ticks_Without_Forcing()
    {
        var d = From(WakeKind.Timer);
        Assert.True(d.Tick);
        Assert.False(d.Force);
        Assert.False(d.DelayForExplorer);
        Assert.False(d.Reactivate);
    }

    [Fact]
    public void DisplayChange_Forces_Delays_And_Reactivates()
    {
        var d = From(WakeKind.DisplayChange);
        Assert.True(d.Tick);
        Assert.True(d.Force);
        Assert.True(d.DelayForExplorer);
        Assert.True(d.Reactivate);
    }

    [Fact]
    public void LayoutChanged_Forces_And_Reactivates_But_Does_Not_Delay()
    {
        var d = From(WakeKind.LayoutChanged);
        Assert.True(d.Tick);
        Assert.True(d.Force);
        Assert.False(d.DelayForExplorer);
        Assert.True(d.Reactivate);
    }

    [Fact]
    public void Manual_Forces_But_Does_Not_Reactivate()
    {
        var d = From(WakeKind.Manual);
        Assert.True(d.Tick);
        Assert.True(d.Force);
        Assert.False(d.DelayForExplorer);
        Assert.False(d.Reactivate);
    }

    [Fact]
    public void SourceCompleted_And_SessionUnlock_Tick_Without_Forcing()
    {
        var d = From(WakeKind.SourceCompleted, WakeKind.SessionUnlock);
        Assert.True(d.Tick);
        Assert.False(d.Force);
        Assert.False(d.Reactivate);
    }

    [Fact]
    public void Shutdown_Wins_Over_Everything()
    {
        var d = From(WakeKind.DisplayChange, WakeKind.Manual, WakeKind.Shutdown, WakeKind.Timer);
        Assert.True(d.Shutdown);
        Assert.False(d.Tick);
        Assert.False(d.Force);
        Assert.False(d.DelayForExplorer);
        Assert.False(d.Reactivate);
    }

    [Fact]
    public void Reasons_Combine()
    {
        var d = From(WakeKind.Timer, WakeKind.DisplayChange);
        Assert.True(d.Tick);
        Assert.True(d.Force);
        Assert.True(d.DelayForExplorer);
        Assert.True(d.Reactivate);
    }
}
