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

    /// <summary>The probes swallow every exception internally, so "does not throw" asserted nothing.
    /// What is worth pinning is the shape of the answer and, for the crash probe, that the 2000-entry
    /// event-log walk is paid once per process and then remembered (finding 7: it runs inline on the
    /// tick thread, which in the daemon is also the message pump).</summary>
    [Fact]
    public void Real_Probes_Answer_In_Range_And_The_Crash_Scan_Is_Cached()
    {
        var first = SystemSource.CachedCrashEvent();
        Assert.True(first is null || first <= DateTimeOffset.Now, "a crash cannot be in the future");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var second = SystemSource.CachedCrashEvent();
        sw.Stop();
        Assert.Equal(first, second);
        Assert.True(sw.ElapsedMilliseconds < 50, $"the second probe re-scanned the event log ({sw.ElapsedMilliseconds} ms)");

        _ = SystemSource.RebootPending();   // a registry read; either answer is legitimate on this machine
    }
}
