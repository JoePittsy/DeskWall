using DeskWall.Core;
using DeskWall.Core.Scheduling;
using DeskWall.Core.Sources;
using DeskWall.Core.Values;
using Xunit;

file sealed class FixedClock(DateTimeOffset now) : IClock { public DateTimeOffset Now => now; }

public class BatterySourceTests
{
    private static readonly TimeSpan Bst = TimeSpan.FromHours(1);

    [Fact]
    public async Task Publishes_The_Power_Flags_On_Any_Machine()
    {
        var v = await new BatterySource("battery", TimeSpan.FromSeconds(60)).RefreshAsync(default);
        Assert.IsType<BoolValue>(v.Get("charging"));
        Assert.IsType<BoolValue>(v.Get("onBattery"));
        Assert.IsType<NumberValue>(v.Get("minutesLeft"));
    }

    /// <summary>Harden review finding 2: the battery source took PeriodicSource's lastRefresh + every,
    /// so a daemon started at 09:28:30 refreshed it at :30 every minute for as long as it ran - a
    /// second wake and repaint beside the clock's :00, the bug HardwareSource.NextDue documents.</summary>
    [Fact]
    public void NextDue_Is_The_Next_Whole_Multiple_Of_Every_Not_LastRefresh_Plus_Every()
    {
        var s = new BatterySource("battery", TimeSpan.FromSeconds(60));
        var last = new DateTimeOffset(2026, 9, 30, 9, 28, 30, 258, Bst);
        Assert.Equal(new DateTimeOffset(2026, 9, 30, 9, 29, 0, Bst), s.NextDue(last, last));
        Assert.Equal(last, s.NextDue(null, last));

        var fiveMin = new BatterySource("battery", TimeSpan.FromMinutes(5));
        Assert.Equal(new DateTimeOffset(2026, 9, 30, 9, 30, 0, Bst), fiveMin.NextDue(last, last));

        var zero = new BatterySource("battery", TimeSpan.Zero);   // "every": 0 must not divide by zero
        Assert.Equal(new DateTimeOffset(2026, 9, 30, 9, 29, 0, Bst), zero.NextDue(last, last));
    }

    [Fact]
    public void A_Daemon_Started_Mid_Minute_Wakes_Once_A_Minute_For_The_Clock_And_The_Battery()
    {
        var start = new DateTimeOffset(2026, 9, 30, 9, 28, 30, 258, Bst);
        var reg = new SourceRegistry();
        var time = new TimeSource("time", new FixedClock(start));
        var battery = new BatterySource("battery", TimeSpan.FromSeconds(60));
        var scheduler = new Scheduler([time, battery], reg);
        reg.Set(SourceSnapshot.Initial("time").Succeeded(ValueTree.Empty, start));
        reg.Set(SourceSnapshot.Initial("battery").Succeeded(ValueTree.Empty, start));

        var wake = scheduler.NextWake(start);
        Assert.Equal(new DateTimeOffset(2026, 9, 30, 9, 29, 0, Bst), wake);
        Assert.Equal(2, scheduler.Due(wake).Count);   // one wake serves both
    }
}
