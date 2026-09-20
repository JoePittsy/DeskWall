using DeskWall.Core.Sources;
using DeskWall.Core.Values;
using Xunit;

file sealed class FixedClock(DateTimeOffset now) : IClock { public DateTimeOffset Now => now; }

public class SystemSourceTests
{
    [Fact]
    public async Task Publishes_Uptime_Crash_Days_And_Reboot_From_Probes()
    {
        var now = new DateTimeOffset(2026, 9, 20, 14, 0, 0, TimeSpan.Zero);
        var src = new SystemSource("sys", TimeSpan.FromMinutes(15), new FixedClock(now), crashProbe: () => now.AddDays(-12.4), rebootProbe: () => true);
        var v = await src.RefreshAsync(default);
        Assert.Equal(12, ((NumberValue)v.Get("daysSinceCrash")!).Number);
        Assert.IsType<TimeValue>(v.Get("lastCrashAt"));
        Assert.True(((BoolValue)v.Get("pendingReboot")!).Flag);
        Assert.True(((NumberValue)v.Get("uptime")!).Number > 0);
        Assert.Matches(@"^\d+d \d+h$|^\d+h \d+m$", ((TextValue)v.Get("uptimeText")!).Text);
        Assert.Equal(Environment.MachineName, ((TextValue)v.Get("machine")!).Text);
    }

    [Fact]
    public async Task No_Crash_Means_Minus_One_And_No_LastCrashAt()
    {
        var src = new SystemSource("sys", TimeSpan.FromMinutes(15), new FixedClock(DateTimeOffset.UnixEpoch), crashProbe: () => null, rebootProbe: () => false);
        var v = await src.RefreshAsync(default);
        Assert.Equal(-1, ((NumberValue)v.Get("daysSinceCrash")!).Number);
        Assert.Null(v.Get("lastCrashAt"));
    }

    [Fact]
    public void Real_Probes_Do_Not_Throw()
    {
        _ = SystemSource.NewestCrashEvent();
        _ = SystemSource.RebootPending();
    }
}
