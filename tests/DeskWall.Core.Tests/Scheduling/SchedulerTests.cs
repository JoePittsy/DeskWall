using DeskWall.Core.Scheduling;
using DeskWall.Core.Sources;
using DeskWall.Core.Values;
using Xunit;

file sealed class FixedSource(string name, TimeSpan every) : PeriodicSource(name, every)
{
    public override ValueTask<RecordValue> RefreshAsync(CancellationToken ct) => new(ValueTree.Empty);
}

/// <summary>Shaped like the Phase 4 http source: periodic, with a per-refresh timeout.</summary>
file sealed class HttpLikeSource(string name, TimeSpan every, TimeSpan timeout) : AsyncSource(name, every, timeout)
{
    protected override Task<RecordValue> FetchAsync(CancellationToken ct) => throw new TimeoutException("down");
}

public class SchedulerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 20, 14, 32, 5, TimeSpan.Zero);

    /// <summary>A source nobody has *tried* yet is due: the first tick must run it. Contrast with the
    /// back-off tests below - before finding 1 this was also the answer for a source that had been
    /// tried and failed, which pinned the daemon at four ticks a second forever.</summary>
    [Fact]
    public void Never_Attempted_Sources_Are_Due_Now_And_NextWake_Is_MinDelay()
    {
        var reg = new SourceRegistry();
        var s = new Scheduler([new FixedSource("a", TimeSpan.FromMinutes(5))], reg);
        Assert.Single(s.Due(T0));
        Assert.Equal(T0 + Scheduler.MinDelay, s.NextWake(T0));
    }

    /// <summary>Finding 1: a source that has failed records the attempt, so the next wake is its own
    /// interval away, not MinDelay. An http source with the network down must not turn the daemon
    /// into a 250 ms block-fail-sleep loop.</summary>
    [Fact]
    public void A_Failing_Source_Waits_Its_Own_Interval_Not_MinDelay()
    {
        var every = TimeSpan.FromMinutes(1);
        var reg = new SourceRegistry();
        var src = new HttpLikeSource("http", every, TimeSpan.FromSeconds(10));
        var s = new Scheduler([src], reg);
        reg.Set(SourceSnapshot.Initial("http").Failed("timed out", T0));   // failed, never succeeded

        Assert.True(s.NextWake(T0) >= T0 + every);
        Assert.Equal(T0 + every, s.NextWake(T0));
        Assert.Empty(s.Due(T0));
        Assert.Empty(s.Due(T0 + every - TimeSpan.FromSeconds(1)));
        Assert.Equal("http", Assert.Single(s.Due(T0 + every)).Name);
    }

    /// <summary>...and the retry backs off exponentially, capped at MaxDelay.</summary>
    [Fact]
    public void Repeated_Failures_Back_Off_Exponentially_And_Cap_At_MaxDelay()
    {
        var every = TimeSpan.FromMinutes(1);
        var reg = new SourceRegistry();
        var s = new Scheduler([new HttpLikeSource("http", every, TimeSpan.FromSeconds(10))], reg);

        var snap = SourceSnapshot.Initial("http");
        for (var i = 0; i < 3; i++) snap = snap.Failed("timed out", T0);
        Assert.Equal(3, snap.ConsecutiveFailures);
        reg.Set(snap);
        Assert.True(s.NextWake(T0) >= T0 + 4 * every);        // 2^(3-1)
        Assert.Equal(T0 + 4 * every, s.NextWake(T0));

        for (var i = 3; i < 12; i++) snap = snap.Failed("timed out", T0);
        reg.Set(snap);
        Assert.Equal(T0 + Scheduler.MaxDelay, s.NextWake(T0));

        // A source whose own interval is longer than the cap is still only due after its interval;
        // NextWake itself stays clamped to MaxDelay, so the daemon keeps its 15-minute heartbeat.
        var slowSrc = new HttpLikeSource("http", TimeSpan.FromHours(1), TimeSpan.FromSeconds(10));
        var slowSnap = SourceSnapshot.Initial("http").Failed("timed out", T0);
        Assert.Equal(TimeSpan.FromHours(1), Scheduler.BackOff(TimeSpan.FromHours(1), 4));
        Assert.Equal(T0 + TimeSpan.FromHours(1), Scheduler.DueAt(slowSrc, slowSnap, T0));
        Assert.False(Scheduler.IsDue(slowSrc, slowSnap, T0 + Scheduler.MaxDelay));
    }

    /// <summary>One success clears the back-off; the source is back on its own schedule.</summary>
    [Fact]
    public void A_Recovered_Source_Returns_To_Its_Normal_Schedule()
    {
        var every = TimeSpan.FromMinutes(1);
        var reg = new SourceRegistry();
        var s = new Scheduler([new HttpLikeSource("http", every, TimeSpan.FromSeconds(10))], reg);
        reg.Set(SourceSnapshot.Initial("http").Failed("timed out", T0).Failed("timed out", T0)
                                              .Succeeded(ValueTree.Empty, T0));
        Assert.Equal(T0 + every, s.NextWake(T0));
    }

    /// <summary>The wake maths and the tick's own due gate are one rule (Scheduler.IsDue), so the
    /// daemon can never wake for a source that TickRunner then declines to refresh.</summary>
    [Fact]
    public void IsDue_Agrees_With_NextWake_For_A_Failing_Source()
    {
        var every = TimeSpan.FromMinutes(1);
        var src = new HttpLikeSource("http", every, TimeSpan.FromSeconds(10));
        var snap = SourceSnapshot.Initial("http").Failed("timed out", T0);
        var wake = new Scheduler([src], Registry(snap)).NextWake(T0);
        Assert.False(Scheduler.IsDue(src, snap, wake - TimeSpan.FromMilliseconds(1)));
        Assert.True(Scheduler.IsDue(src, snap, wake));
    }

    private static SourceRegistry Registry(SourceSnapshot snap)
    {
        var reg = new SourceRegistry();
        reg.Set(snap);
        return reg;
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
