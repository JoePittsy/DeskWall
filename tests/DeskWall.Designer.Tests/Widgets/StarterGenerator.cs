using DeskWall.Core.Layout;
using DeskWall.Designer.Model;
using DeskWall.Designer.Model.Widgets;
using DeskWall.Core.Widgets;

namespace DeskWall.Designer.Tests.Widgets;

/// <summary>Builds layouts/column-system.json, clock-disks.json and steam-recent.json from the
/// shipped widget templates as v2 layouts of linked copies, so the repo has one source of truth for
/// what those starters contain. Not itself a test -- <see cref="StarterGeneratorTests"/> asserts
/// the committed files equal what this produces.</summary>
internal static class StarterGenerator
{
    private const string SpotlightImage = @"C:\Windows\SystemApps\MicrosoftWindows.Client.CBS_cw5n1h2txyewy\DesktopSpotlight\Assets\Images\image_3.jpg";

    private static LayoutFile New() => new() { Version = 2, BaseImage = SpotlightImage, Copies = [] };

    public static LayoutFile ColumnSystem(IReadOnlyList<WidgetTemplate> catalog)
    {
        var layout = New();
        WidgetTemplate Find(string key) => catalog.First(t => t.Key == key);

        var clock = Copies.Add(layout, Find("clock"), 0, 0);
        var weather = Copies.Add(layout, Find("weather"), 0, 0);
        var vpn = Copies.Add(layout, Find("vpn"), 0, 0);

        var dialTemplate = Find("dial");
        var cpu = Copies.Add(layout, dialTemplate, 0, 0);
        var gpu = Copies.Add(layout, dialTemplate, 0, 0);
        Copies.SetKnob(layout, dialTemplate, gpu, "metric", "GPU||hardware.gpu||hardware.gpuPct | \"{0}%\"||gpu");
        var ram = Copies.Add(layout, dialTemplate, 0, 0);
        Copies.SetKnob(layout, dialTemplate, ram, "metric", "RAM||hardware.ram||hardware.ramPct | \"{0}%\"||ram");
        var temp = Copies.Add(layout, dialTemplate, 0, 0);
        Copies.SetKnob(layout, dialTemplate, temp, "metric", "GPU temperature||hardware.gpuTempFraction||hardware.gpuTempC | \"{0}\u00b0\"||gpu \u00b0C");
        // The GPU-temperature dial warns earlier than the load dials; the widget model has no way
        // for one knob's default to depend on another knob's value, so this is the copy's knob.
        Copies.SetKnob(layout, dialTemplate, temp, "warnAt", "0.83");

        var drives = Copies.Add(layout, Find("drives"), 0, 0);

        Arranger.Arrange(layout, catalog, [clock, weather, vpn, cpu, gpu, ram, temp, drives]);
        return layout;
    }

    /// <summary>Clock at the top, Steam covers below it, drives up from the bottom.</summary>
    public static LayoutFile SteamRecent(IReadOnlyList<WidgetTemplate> catalog)
    {
        var layout = New();
        WidgetTemplate Find(string key) => catalog.First(t => t.Key == key);

        var clock = Copies.Add(layout, Find("clock"), 0, 0);
        var covers = Copies.Add(layout, Find("steam-covers"), 0, 0);
        var drives = Copies.Add(layout, Find("drives"), 0, 0);

        Arranger.Arrange(layout, catalog, [clock, covers, drives]);
        return layout;
    }

    public static LayoutFile ClockDisks(IReadOnlyList<WidgetTemplate> catalog)
    {
        var layout = New();
        WidgetTemplate Find(string key) => catalog.First(t => t.Key == key);

        var clock = Copies.Add(layout, Find("clock"), 0, 0);
        var drives = Copies.Add(layout, Find("drives"), 0, 0);

        Arranger.Arrange(layout, catalog, [clock, drives]);
        return layout;
    }
}
