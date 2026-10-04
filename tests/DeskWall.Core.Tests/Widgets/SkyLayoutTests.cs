using DeskWall.Core.Layout;
using DeskWall.Core.Widgets;
using Xunit;

namespace DeskWall.Core.Tests.Widgets;

/// <summary>The layouts that draw a sky (sun, moon, stars on time.skyFraction, a palette on
/// time.phase) take the real sunrise and sunset from the weather widget's daily block (#37), and
/// show the weather widget's night icon after dark (#45).</summary>
public class SkyLayoutTests
{
    public static TheoryData<string> SkyLayouts() => new() { "alpine-rice.json", "alpine-vision.json", "alpine-vision-photos.json", "vapor.json" };

    private static LayoutFile Expanded(string file)
        => WidgetExpander.Expand(LayoutFile.Load(Path.Combine(Repo.Root, "layouts", file)), Repo.Shipped()).Layout;

    /// <summary>One time source, carrying the sun settings: the clock copy's own time source matches
    /// the layout's through its overrides, so it is shared rather than split off as "time2" (which
    /// would leave the clock on a second, sunless source).</summary>
    [Theory]
    [MemberData(nameof(SkyLayouts))]
    public void One_Time_Source_Follows_The_Weather_Sun(string file)
    {
        var layout = Expanded(file);
        var time = Assert.Single(layout.Sources, s => s.Type == "time");
        Assert.Equal("time", time.Name);
        Assert.Equal("weather.json.daily.sunrise[0]", time.Settings["sunrise"]);
        Assert.Equal("weather.json.daily.sunset[0]", time.Settings["sunset"]);
        var weather = Assert.Single(layout.Sources, s => s.Name == "weather");
        Assert.Contains("daily=sunrise,sunset", weather.Settings["url"]);
    }

    /// <summary>The sky moves on the real sun: nothing is left choreographed on the clock-only
    /// dayFraction.</summary>
    [Theory]
    [MemberData(nameof(SkyLayouts))]
    public void The_Sky_Binds_Sky_Fraction_Not_Day_Fraction(string file)
    {
        var text = File.ReadAllText(Path.Combine(Repo.Root, "layouts", file));
        Assert.DoesNotContain("time.dayFraction", text);
        Assert.Contains("time.skyFraction", text);
    }

    /// <summary>A copy that moved its day icon moved its night icon with it.</summary>
    [Theory]
    [MemberData(nameof(SkyLayouts))]
    public void The_Night_Icon_Sits_Where_The_Day_Icon_Does(string file)
    {
        var layout = Expanded(file);
        var day = layout.Components.Single(c => c.Id == "weather-1.sky");
        var night = layout.Components.Single(c => c.Id == "weather-1.sky-night");
        Assert.Equal(day.Rect, night.Rect);
    }
}
