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

    private static double Num(RecordValue v, string field) => ((NumberValue)v.Get(field)!).Number;

    /// <summary>A "day progress" widget needed a PowerShell script spawned every minute for three
    /// numbers the clock already knows. Both forms ship because a bar wants the fraction and a text
    /// wants the percent, and a format string cannot multiply by 100.</summary>
    [Theory]
    [InlineData(2026, 9, 20, 0, 0, 0.0, 0)]
    [InlineData(2026, 9, 20, 18, 0, 0.75, 75)]
    [InlineData(2026, 9, 20, 6, 0, 0.25, 25)]
    public async Task Day_Progress_Is_The_Fraction_Of_Local_Midnight_To_Midnight(int y, int mo, int d, int h, int mi, double fraction, double percent)
    {
        var v = await new TimeSource("time", new FakeClock(new DateTimeOffset(y, mo, d, h, mi, 0, TimeSpan.FromHours(1)))).RefreshAsync(default);
        Assert.Equal(fraction, Num(v, "dayFraction"), 4);
        Assert.Equal(percent, Num(v, "dayPercent"));
    }

    /// <summary>The week starts Monday, not Sunday: ((int)DayOfWeek + 6) % 7.</summary>
    [Fact]
    public async Task Week_Progress_Starts_On_Monday()
    {
        // 2026-09-21 is a Monday.
        var monday = await new TimeSource("time", new FakeClock(new DateTimeOffset(2026, 9, 21, 0, 0, 0, TimeSpan.Zero))).RefreshAsync(default);
        Assert.Equal(0.0, Num(monday, "weekFraction"), 4);
        Assert.Equal(0, Num(monday, "weekPercent"));

        // Thursday noon is 3.5 days into a 7 day week.
        var thursday = await new TimeSource("time", new FakeClock(new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero))).RefreshAsync(default);
        Assert.Equal(0.5, Num(thursday, "weekFraction"), 4);
        Assert.Equal(50, Num(thursday, "weekPercent"));

        var sunday = await new TimeSource("time", new FakeClock(new DateTimeOffset(2026, 9, 27, 23, 59, 0, TimeSpan.Zero))).RefreshAsync(default);
        Assert.InRange(Num(sunday, "weekFraction"), 0.999, 1.0);
        Assert.Equal(100, Num(sunday, "weekPercent"));
    }

    [Fact]
    public async Task Year_Progress_Divides_By_366_In_A_Leap_Year()
    {
        var newYear = await new TimeSource("time", new FakeClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero))).RefreshAsync(default);
        Assert.Equal(0.0, Num(newYear, "yearFraction"), 4);
        Assert.Equal(0, Num(newYear, "yearPercent"));

        // 2028 is a leap year: 31 December 23:59 is just short of the whole 366 days.
        var lastMinute = await new TimeSource("time", new FakeClock(new DateTimeOffset(2028, 12, 31, 23, 59, 0, TimeSpan.Zero))).RefreshAsync(default);
        Assert.InRange(Num(lastMinute, "yearFraction"), 0.999, 1.0);
        Assert.Equal(100, Num(lastMinute, "yearPercent"));

        // Day 184 of 366 (2 July) is the leap-year halfway mark; in a common year the same date is past it.
        var leapHalf = await new TimeSource("time", new FakeClock(new DateTimeOffset(2028, 7, 2, 0, 0, 0, TimeSpan.Zero))).RefreshAsync(default);
        Assert.Equal(0.5, Num(leapHalf, "yearFraction"), 4);
    }

    [Fact]
    public async Task Fractions_Are_Rounded_To_Four_Decimals_So_A_Content_Key_Is_Stable()
    {
        var v = await new TimeSource("time", new FakeClock(new DateTimeOffset(2026, 9, 20, 7, 41, 33, TimeSpan.Zero))).RefreshAsync(default);
        foreach (var field in new[] { "dayFraction", "weekFraction", "yearFraction" })
            Assert.Equal(Num(v, field), Math.Round(Num(v, field), 4));
        foreach (var field in new[] { "dayPercent", "weekPercent", "yearPercent" })
            Assert.Equal(Num(v, field), Math.Round(Num(v, field)));
    }
}
