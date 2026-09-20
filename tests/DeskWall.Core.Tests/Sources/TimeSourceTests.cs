using DeskWall.Core.Sources;
using DeskWall.Core.Values;
using Xunit;

file sealed class FakeClock(DateTimeOffset now) : IClock { public DateTimeOffset Now { get; set; } = now; }

public class TimeSourceTests
{
    [Fact]
    public async Task Publishes_Now_Date_Weekday()
    {
        var t = new DateTimeOffset(2026, 9, 20, 14, 32, 17, TimeSpan.FromHours(1));
        var src = new TimeSource("time", new FakeClock(t));
        var v = await src.RefreshAsync(default);
        Assert.Equal(t, ((TimeValue)v.Get("now")!).Time);
        Assert.Equal("2026-09-20", ((TextValue)v.Get("date")!).Text);
        Assert.Equal("Sunday", ((TextValue)v.Get("weekday")!).Text);
    }

    [Fact]
    public void NextDue_IsNextWholeMinute()
    {
        var t = new DateTimeOffset(2026, 9, 20, 14, 32, 17, TimeSpan.Zero);
        var src = new TimeSource("time", new FakeClock(t));
        Assert.Equal(t, src.NextDue(null, t));
        Assert.Equal(new DateTimeOffset(2026, 9, 20, 14, 33, 0, TimeSpan.Zero), src.NextDue(t, t));
    }
}
