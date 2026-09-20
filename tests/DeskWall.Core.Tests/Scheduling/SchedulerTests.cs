using DeskWall.Core.Scheduling;
using DeskWall.Core.Sources;
using DeskWall.Core.Values;
using Xunit;

file sealed class FixedSource(string name, TimeSpan every) : PeriodicSource(name, every)
{
    public override ValueTask<RecordValue> RefreshAsync(CancellationToken ct) => new(ValueTree.Empty);
}

public class SchedulerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 20, 14, 32, 5, TimeSpan.Zero);

    [Fact]
    public void Never_Run_Sources_Are_Due_Now_And_NextWake_Is_MinDelay()
    {
        var reg = new SourceRegistry();
        var s = new Scheduler([new FixedSource("a", TimeSpan.FromMinutes(5))], reg);
        Assert.Single(s.Due(T0));
        Assert.Equal(T0 + Scheduler.MinDelay, s.NextWake(T0));
    }

    [Fact]
    public void NextWake_Is_Earliest_Due()
    {
        var reg = new SourceRegistry();
        reg.Set(SourceSnapshot.Initial("a").Succeeded(ValueTree.Empty, T0));
        reg.Set(SourceSnapshot.Initial("b").Succeeded(ValueTree.Empty, T0));
        var s = new Scheduler([new FixedSource("a", TimeSpan.FromMinutes(5)), new FixedSource("b", TimeSpan.FromSeconds(90))], reg);
        Assert.Equal(T0.AddSeconds(90), s.NextWake(T0));
        Assert.Empty(s.Due(T0.AddSeconds(89)));
        Assert.Equal("b", Assert.Single(s.Due(T0.AddSeconds(90))).Name);
    }

    [Fact]
    public void NextWake_Is_Clamped_To_MaxDelay_And_Empty_List()
    {
        var reg = new SourceRegistry();
        reg.Set(SourceSnapshot.Initial("a").Succeeded(ValueTree.Empty, T0));
        var s = new Scheduler([new FixedSource("a", TimeSpan.FromHours(3))], reg);
        Assert.Equal(T0 + Scheduler.MaxDelay, s.NextWake(T0));
        Assert.Equal(T0 + Scheduler.MaxDelay, new Scheduler([], reg).NextWake(T0));
    }

    [Fact]
    public void Time_Source_Wakes_On_The_Minute()
    {
        var reg = new SourceRegistry();
        reg.Set(SourceSnapshot.Initial("time").Succeeded(ValueTree.Empty, T0));
        var s = new Scheduler([new TimeSource("time", SystemClock.Instance)], reg);
        Assert.Equal(new DateTimeOffset(2026, 9, 20, 14, 33, 0, TimeSpan.Zero), s.NextWake(T0));
    }
}
