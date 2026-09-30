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
