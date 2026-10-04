using DeskWall.Core.Tick;
using DeskWall.Core.Values;
using Xunit;

namespace DeskWall.Core.Tests.Tick;

public class PreviewOverridesTests
{
    private static RecordValue Tree() => new(new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase)
    {
        ["audio"] = new RecordValue(new Dictionary<string, Value> { ["volume"] = new NumberValue(0.2), ["muted"] = new BoolValue(false) }),
        ["disks"] = new RecordValue(new Dictionary<string, Value>
        {
            ["drives"] = new ListValue([
                new RecordValue(new Dictionary<string, Value> { ["letter"] = new TextValue("C"), ["usedFraction"] = new NumberValue(0.4) }),
                new RecordValue(new Dictionary<string, Value> { ["letter"] = new TextValue("D"), ["usedFraction"] = new NumberValue(0.5) }),
            ], "letter"),
        }),
    });

    private static Value? At(RecordValue r, params string[] path)
    {
        Value? cur = r;
        foreach (var p in path)
            cur = cur switch
            {
                RecordValue rv => rv.Get(p),
                ListValue l when int.TryParse(p, out var i) => l.Items[i],
                _ => null,
            };
        return cur;
    }

    [Fact]
    public void Types_values_and_creates_missing_records()
    {
        var t = PreviewOverrides.Parse("audio.volume=0.8,audio.muted=true,weather.json.current.weather_code=63,media.title=Hello, world").Apply(Tree());
        Assert.Equal(new NumberValue(0.8), At(t, "audio", "volume"));
        Assert.Equal(new BoolValue(true), At(t, "audio", "muted"));
        Assert.Equal(new NumberValue(63), At(t, "weather", "json", "current", "weather_code"));
        Assert.Equal(new TextValue("Hello, world"), At(t, "media", "title"));
    }

    [Fact]
    public void Steps_into_one_list_item()
    {
        var t = PreviewOverrides.Parse("disks.drives.0.usedFraction=0.95").Apply(Tree());
        Assert.Equal(new NumberValue(0.95), At(t, "disks", "drives", "0", "usedFraction"));
        Assert.Equal(new NumberValue(0.5), At(t, "disks", "drives", "1", "usedFraction"));
    }

    [Fact]
    public void Time_is_pinned_as_a_whole_and_the_last_time_pin_wins()
    {
        var t = PreviewOverrides.Parse("time.at=12:00,time.dayFraction=0.9").Apply(Tree());
        Assert.Equal(new NumberValue(0.9), At(t, "time", "dayFraction"));
        Assert.Equal(new TextValue("night"), At(t, "time", "phase"));
        Assert.Equal(21, ((TimeValue)At(t, "time", "now")!).Time.Hour);

        var noon = PreviewOverrides.Parse("time.at=12:00").Apply(Tree());
        Assert.Equal(new NumberValue(0.5), At(noon, "time", "dayFraction"));
        Assert.Equal(new TextValue("day"), At(noon, "time", "phase"));
    }

    /// <summary>The tree as the time source publishes it with sun settings: sunrise 07:12, sunset
    /// 18:41, both today.</summary>
    private static RecordValue SunTree()
    {
        var today = DateTimeOffset.Now.Date;
        TimeValue At(int h, int m) => new(new DateTimeOffset(today.AddHours(h).AddMinutes(m), TimeZoneInfo.Local.GetUtcOffset(today.AddHours(h).AddMinutes(m))));
        var fields = new Dictionary<string, Value>(Tree().Fields, StringComparer.OrdinalIgnoreCase)
        {
            ["time"] = new RecordValue(new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase)
            {
                ["phase"] = new TextValue("day"), ["sunrise"] = At(7, 12), ["sunset"] = At(18, 41),
            }),
        };
        return new RecordValue(fields);
    }

    [Theory]
    // 06:00 is dawn on the fixed thresholds (0.25) but night with a 07:12 sunrise (dawn from 06:32);
    // 19:30 is dusk on the fixed thresholds (0.81) but night with an 18:41 sunset (dusk to 19:21).
    [InlineData("06:00", "night", 0.0)]
    [InlineData("07:00", "dawn", 0.0)]
    [InlineData("12:00", "day", 0.418)]
    [InlineData("18:30", "dusk", 0.984)]
    [InlineData("19:30", "night", 1.0)]
    public void A_time_pin_keeps_the_live_sunrise_and_sunset(string at, string phase, double sunFraction)
    {
        var t = PreviewOverrides.Parse($"time.at={at}").Apply(SunTree());
        Assert.Equal(new TextValue(phase), At(t, "time", "phase"));
        Assert.Equal(sunFraction, ((NumberValue)At(t, "time", "sunFraction")!).Number, 4);
        Assert.Equal(7, ((TimeValue)At(t, "time", "sunrise")!).Time.Hour);
        Assert.Equal(18, ((TimeValue)At(t, "time", "sunset")!).Time.Hour);
    }

    /// <summary>A sky on time.skyFraction previews where the real sun puts it: sunrise lands on
    /// 0.25 and sunset on 0.75, whether the sun is the live one or pinned.</summary>
    [Theory]
    [InlineData("time.at=07:12", 0.25)]
    [InlineData("time.at=18:41", 0.75)]
    [InlineData("time.at=07:00,time.sunrise=07:00,time.sunset=19:00", 0.25)]
    [InlineData("time.at=13:00,time.sunrise=07:00,time.sunset=19:00", 0.5)]
    public void A_time_pin_puts_the_sky_fraction_on_the_sun(string pins, double sky)
    {
        var t = PreviewOverrides.Parse(pins).Apply(SunTree());
        Assert.Equal(sky, ((NumberValue)At(t, "time", "skyFraction")!).Number, 4);
    }

    [Fact]
    public void Pinned_sunrise_and_sunset_drive_the_phase_too()
    {
        // A preview can move the sun as well as the clock: the pins win over the live values.
        var t = PreviewOverrides.Parse("time.at=06:00,time.sunrise=06:10,time.sunset=20:00").Apply(SunTree());
        Assert.Equal(new TextValue("dawn"), At(t, "time", "phase"));
        var rise = Assert.IsType<TimeValue>(At(t, "time", "sunrise"));
        Assert.Equal(new TimeSpan(6, 10, 0), rise.Time.TimeOfDay);

        // And with no live sun at all (no settings), pinning both is enough.
        var bare = PreviewOverrides.Parse("time.at=19:30,time.sunrise=07:00,time.sunset=20:00").Apply(Tree());
        Assert.Equal(new TextValue("dusk"), At(bare, "time", "phase"));
    }

    [Fact]
    public void Without_sun_settings_a_time_pin_keeps_the_fixed_thresholds()
    {
        var t = PreviewOverrides.Parse("time.at=06:00").Apply(Tree());
        Assert.Equal(new TextValue("dawn"), At(t, "time", "phase"));
        Assert.Null(At(t, "time", "sunrise"));
    }

    [Fact]
    public void Pinning_cpu_gives_its_history_a_peak_at_that_value()
    {
        var t = PreviewOverrides.Parse("hardware.cpu=0.9").Apply(Tree());
        var history = Assert.IsType<ListValue>(At(t, "hardware", "cpuHistory"));
        var values = history.Items.Select(i => ((NumberValue)i.Get("v")!).Number).ToList();
        Assert.Equal(30, values.Count);
        Assert.Equal(0.9, values.Max(), 3);
        Assert.True(values[0] < 0.5);
    }
}
