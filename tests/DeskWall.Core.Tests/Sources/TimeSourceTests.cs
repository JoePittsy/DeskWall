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

    private static string Text(RecordValue v, string field) => ((TextValue)v.Get(field)!).Text;

    private static TimeSource Sun(DateTimeOffset now, string? sunrise, string? sunset, Func<RecordValue?>? tree = null)
        => new("time", new FakeClock(now), sunrise, sunset, tree ?? (() => null));

    private static DateTimeOffset At(int h, int m) => new(2026, 9, 30, h, m, 0, TimeSpan.FromHours(1));

    /// <summary>Without settings nothing a layout already binds moves: the fixed thresholds still
    /// decide the phase, and no sunrise/sunset is claimed.</summary>
    [Fact]
    public async Task Without_Sun_Settings_Phase_Keeps_The_Fixed_Thresholds()
    {
        var v = await new TimeSource("time", new FakeClock(At(6, 30))).RefreshAsync(default);
        Assert.Equal("dawn", Text(v, "phase"));   // 0.2708 is between DawnStart and DayStart
        Assert.Null(v.Get("sunrise"));
        Assert.Null(v.Get("sunset"));
        Assert.Equal(0.0417, Num(v, "sunFraction"), 4);   // against the 06:00-18:00 default
        Assert.Equal(0.0, Num(v, "nightFraction"), 4);
    }

    /// <summary>Dawn is sunrise +-40 min and dusk sunset +-40 min, at the minute boundaries.</summary>
    [Theory]
    [InlineData(6, 19, "night")]
    [InlineData(6, 20, "dawn")]
    [InlineData(7, 39, "dawn")]
    [InlineData(7, 40, "day")]
    [InlineData(18, 19, "day")]
    [InlineData(18, 20, "dusk")]
    [InlineData(19, 39, "dusk")]
    [InlineData(19, 40, "night")]
    [InlineData(0, 0, "night")]
    public async Task Phase_Follows_The_Real_Sun(int h, int m, string phase)
    {
        var v = await Sun(At(h, m), "07:00", "19:00").RefreshAsync(default);
        Assert.Equal(phase, Text(v, "phase"));
    }

    /// <summary>A sun bound to sunFraction rises and sets when the real one does; nightFraction is
    /// the same walk across the night, and 0 all day.</summary>
    [Theory]
    [InlineData(3, 0, 0.0, 0.6667)]
    [InlineData(7, 0, 0.0, 0.0)]
    [InlineData(13, 0, 0.5, 0.0)]
    [InlineData(19, 0, 1.0, 0.0)]
    [InlineData(22, 0, 1.0, 0.25)]
    [InlineData(1, 0, 0.0, 0.5)]
    [InlineData(6, 59, 0.0, 0.9986)]
    public async Task Sun_And_Night_Fractions_Run_Between_The_Real_Times(int h, int m, double sun, double night)
    {
        var v = await Sun(At(h, m), "07:00", "19:00").RefreshAsync(default);
        Assert.Equal(sun, Num(v, "sunFraction"), 4);
        Assert.Equal(night, Num(v, "nightFraction"), 4);
    }

    /// <summary>skyFraction is the day fraction on the sun's clock: the real sunrise lands on 0.25
    /// and sunset on 0.75, midnight stays 0, and each stretch between is linear. A sky drawn
    /// against dayFraction for a 06:00 sunrise follows the real one by swapping the field.</summary>
    [Theory]
    [InlineData(0, 0, 0.0)]
    [InlineData(3, 30, 0.125)]
    [InlineData(7, 0, 0.25)]
    [InlineData(13, 0, 0.5)]
    [InlineData(19, 0, 0.75)]
    [InlineData(21, 30, 0.875)]
    [InlineData(23, 59, 0.9992)]
    public async Task Sky_Fraction_Pins_The_Real_Sun_To_Six_And_Eighteen(int h, int m, double sky)
    {
        var v = await Sun(At(h, m), "07:00", "19:00").RefreshAsync(default);
        Assert.Equal(sky, Num(v, "skyFraction"), 4);
    }

    /// <summary>Without sun settings skyFraction is dayFraction, so a layout that swaps one for the
    /// other changes nothing until the sun is set, and a preview pinned by time.at behaves as before.</summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(5, 17)]
    [InlineData(12, 0)]
    [InlineData(18, 42)]
    [InlineData(23, 59)]
    public async Task Without_Sun_Settings_Sky_Fraction_Is_Day_Fraction(int h, int m)
    {
        var v = await new TimeSource("time", new FakeClock(At(h, m))).RefreshAsync(default);
        Assert.Equal(Num(v, "dayFraction"), Num(v, "skyFraction"), 4);
    }

    [Fact]
    public async Task Publishes_Sunrise_And_Sunset_As_Times_Today()
    {
        var v = await Sun(At(12, 0), "7:02", "18:45").RefreshAsync(default);
        Assert.Equal(At(7, 2), ((TimeValue)v.Get("sunrise")!).Time);
        Assert.Equal(At(18, 45), ((TimeValue)v.Get("sunset")!).Time);
    }

    /// <summary>Open-Meteo's daily block with timezone=auto: arrays of local ISO times, no offset.</summary>
    private const string Weather = """
        {"daily":{"time":["2026-09-30","2026-10-01"],"sunrise":["2026-09-30T07:02","2026-10-01T07:04"],"sunset":["2026-09-30T18:45","2026-10-01T18:43"]}}
        """;

    private static RecordValue Tree(string json, IReadOnlySet<string>? unix = null)
        => new(new Dictionary<string, Value> { ["weather"] = new RecordValue(new Dictionary<string, Value> { ["json"] = JsonValues.Parse(json, unix) }) });

    [Fact]
    public async Task Resolves_A_Binding_Path_Into_Another_Sources_Values()
    {
        var tree = Tree(Weather);
        var v = await Sun(At(18, 10), "weather.json.daily.sunrise[0]", "weather.json.daily.sunset[0]", () => tree).RefreshAsync(default);
        Assert.Equal(At(7, 2), ((TimeValue)v.Get("sunrise")!).Time);
        Assert.Equal(At(18, 45), ((TimeValue)v.Get("sunset")!).Time);
        Assert.Equal("dusk", Text(v, "phase"));
    }

    [Fact]
    public async Task Resolves_A_Path_To_A_Unix_Time()
    {
        var rise = At(7, 2).ToUnixTimeSeconds();
        var set = At(18, 45).ToUnixTimeSeconds();
        var tree = Tree($$$"""{"sun":{"rise":{{{rise}}},"set":{{{set}}}}}""", new HashSet<string> { "rise" });
        var v = await Sun(At(12, 0), "weather.json.sun.rise", "weather.json.sun.set", () => tree).RefreshAsync(default);
        Assert.Equal(At(7, 2), ((TimeValue)v.Get("sunrise")!).Time);    // a TimeValue (unixTimeFields)
        Assert.Equal(At(18, 45), ((TimeValue)v.Get("sunset")!).Time);   // a bare number, read as Unix seconds
    }

    /// <summary>Before the weather source first answers the day is 06:00-18:00; once it has, a
    /// tree without it (a failed or stale fetch) keeps the last real times rather than jumping back.</summary>
    [Fact]
    public async Task An_Unresolved_Path_Uses_The_Default_Then_The_Last_Good_Times()
    {
        RecordValue? tree = null;
        var clock = new FakeClock(At(12, 0));
        var src = new TimeSource("time", clock, "weather.json.daily.sunrise[0]", "weather.json.daily.sunset[0]", () => tree);

        var first = await src.RefreshAsync(default);
        Assert.Equal(At(6, 0), ((TimeValue)first.Get("sunrise")!).Time);

        tree = Tree(Weather);
        var second = await src.RefreshAsync(default);
        Assert.Equal(At(7, 2), ((TimeValue)second.Get("sunrise")!).Time);

        tree = new RecordValue(new Dictionary<string, Value>());
        var third = await src.RefreshAsync(default);
        Assert.Equal(At(7, 2), ((TimeValue)third.Get("sunrise")!).Time);
        Assert.Equal(At(18, 45), ((TimeValue)third.Get("sunset")!).Time);
    }

    [Fact]
    public async Task A_Sunset_Before_The_Sunrise_Is_Ignored()
    {
        var v = await Sun(At(12, 0), "19:00", "07:00").RefreshAsync(default);
        Assert.Equal(At(6, 0), ((TimeValue)v.Get("sunrise")!).Time);
        Assert.Equal(At(18, 0), ((TimeValue)v.Get("sunset")!).Time);
    }

    [Fact]
    public void A_Setting_That_Is_Neither_A_Time_Nor_A_Path_Fails_At_Load()
        => Assert.Throws<ArgumentException>(() => Sun(At(12, 0), "weather..sunrise", null));

    [Fact]
    public void The_Registry_Leaves_Its_Latest_Tree_For_Sources_To_Read()
    {
        var reg = new SourceRegistry();
        var values = JsonValues.Parse(Weather);
        reg.Set(SourceSnapshot.Initial("weather").Succeeded(values, At(12, 0)));
        var tree = reg.Tree();
        Assert.Same(tree, SourceTree.Latest);
        var judged = reg.Tree([], At(12, 0));
        Assert.Same(judged, SourceTree.Latest);
    }

    [Fact]
    public async Task The_Factory_Passes_The_Sun_Settings()
    {
        var def = new DeskWall.Core.Layout.SourceDef { Name = "time", Type = "time", Settings = new Dictionary<string, string> { ["sunrise"] = "07:00", ["sunset"] = "19:00" } };
        var src = SourceFactory.Create(def, new FakeClock(At(18, 30)));
        Assert.Equal("dusk", Text(await src.RefreshAsync(default), "phase"));
    }
}
