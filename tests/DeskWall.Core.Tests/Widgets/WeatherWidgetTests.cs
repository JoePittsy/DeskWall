using DeskWall.Core.Layout;
using DeskWall.Core.Resolve;
using DeskWall.Core.Sources;
using DeskWall.Core.Values;
using DeskWall.Core.Widgets;
using Xunit;

namespace DeskWall.Core.Tests.Widgets;

/// <summary>The shipped weather widget, expanded and resolved: is_day picks which of its two icon
/// parts shows (#45). Swapped or identical Step maps on the two opacities fail here.</summary>
public class WeatherWidgetTests
{
    private static LayoutFile Layout()
    {
        var layout = new LayoutFile { Version = 2, BaseImage = "" };
        layout.Copies = [new WidgetCopy { Id = "weather-1", Widget = "weather", X = 0, Y = 0 }];
        return WidgetExpander.Expand(layout, Repo.Shipped()).Layout;
    }

    private static RecordValue Tree(int code, int isDay)
    {
        var json = JsonValues.Parse($$$"""{"current":{"temperature_2m":14.2,"weather_code":{{{code}}},"is_day":{{{isDay}}}}}""");
        return new RecordValue(new Dictionary<string, Value> { ["weather"] = new RecordValue(new Dictionary<string, Value> { ["json"] = json }) });
    }

    private static ResolvedImage Icon(IReadOnlyList<Resolved> resolved, string part)
        => Assert.IsType<ResolvedImage>(Assert.Single(resolved, r => r.Id == "weather-1." + part));

    [Theory]
    // code, is_day, the file the visible part draws
    [InlineData(0, 1, "0.png")]
    [InlineData(0, 0, "0-night.png")]
    [InlineData(2, 0, "2-night.png")]
    [InlineData(63, 0, "63-night.png")]
    public void Is_Day_Shows_Exactly_One_Icon(int code, int isDay, string file)
    {
        var resolved = LayoutResolver.Resolve(Layout(), Tree(code, isDay));
        var day = Icon(resolved, "sky");
        var night = Icon(resolved, "sky-night");

        Assert.Equal(isDay == 1 ? 1f : 0f, day.Opacity);
        Assert.Equal(isDay == 1 ? 0f : 1f, night.Opacity);
        var shown = isDay == 1 ? day : night;
        Assert.EndsWith(@"assets\weather\" + file, shown.Path);
        // ...and the file it names ships, so the night is never a fallback plate.
        Assert.True(File.Exists(Path.Combine(Repo.Root, "assets", "weather", file)), file);
    }
}
