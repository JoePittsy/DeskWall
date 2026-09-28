using DeskWall.Core;
using DeskWall.Core.Layout;
using DeskWall.Designer.Model.Widgets;

namespace DeskWall.Designer.Tests.Widgets;

/// <summary>Builds layouts/column-system.json and layouts/clock-disks.json from the shipped
/// widget templates, so the repo has one source of truth for what those starters contain
/// (plan step 4: "the generator's output becomes the committed file"). Not itself a test --
/// <see cref="StarterGeneratorTests"/> asserts the committed files equal what this produces.</summary>
internal static class StarterGenerator
{
    private const string SpotlightImage = @"C:\Windows\SystemApps\MicrosoftWindows.Client.CBS_cw5n1h2txyewy\DesktopSpotlight\Assets\Images\image_3.jpg";

    public static LayoutFile ColumnSystem(IReadOnlyList<WidgetTemplate> catalog)
    {
        var layout = new LayoutFile { BaseImage = SpotlightImage };
        WidgetTemplate Find(string key) => catalog.First(t => t.Key == key);

        var clock = WidgetInstance.Add(layout, Find("clock"), new Rect(0, 0, 0, 0));
        var weather = WidgetInstance.Add(layout, Find("weather"), new Rect(0, 0, 0, 0));
        var vpn = WidgetInstance.Add(layout, Find("vpn"), new Rect(0, 0, 0, 0));

        var dialTemplate = Find("dial");
        var cpu = WidgetInstance.Add(layout, dialTemplate, new Rect(0, 0, 0, 0));
        var gpu = WidgetInstance.Add(layout, dialTemplate, new Rect(0, 0, 0, 0));
        WidgetInstance.SetKnob(layout, dialTemplate, gpu, "metric", "GPU||hardware.gpu||hardware.gpuPct | \"{0}%\"||gpu");
        var ram = WidgetInstance.Add(layout, dialTemplate, new Rect(0, 0, 0, 0));
        WidgetInstance.SetKnob(layout, dialTemplate, ram, "metric", "RAM||hardware.ram||hardware.ramPct | \"{0}%\"||ram");
        var temp = WidgetInstance.Add(layout, dialTemplate, new Rect(0, 0, 0, 0));
        WidgetInstance.SetKnob(layout, dialTemplate, temp, "metric", "GPU temperature||hardware.gpuTempFraction||hardware.gpuTempC | \"{0}\u00b0\"||gpu \u00b0C");
        // The GPU-temperature dial warns earlier than the load dials (matches today's
        // column-system.json); the widget model has no way for one knob's default to depend on
        // another knob's value, so this override is the instantiator's job, not the template's.
        WidgetInstance.SetKnob(layout, dialTemplate, temp, "warnAt", "0.83");

        var drives = WidgetInstance.Add(layout, Find("drives"), new Rect(0, 0, 0, 0));

        Arranger.Arrange(layout, catalog, [clock, weather, vpn, cpu, gpu, ram, temp, drives]);
        return layout;
    }

    /// <summary>The last hand-authored starter, now built from the same three widgets that
    /// describe what was in it: clock at the top, Steam covers below it, drives up from the
    /// bottom. The bands move a few pixels against the file it replaces -- the Arranger stacks by
    /// each widget's own height and gap rather than the numbers that were typed in -- and the
    /// widgets bring their own current settings (an effectRadius, a thresholdFill) with them.
    /// That is the point of defining it from them.</summary>
    public static LayoutFile SteamRecent(IReadOnlyList<WidgetTemplate> catalog)
    {
        var layout = new LayoutFile { BaseImage = SpotlightImage };
        WidgetTemplate Find(string key) => catalog.First(t => t.Key == key);

        var clock = WidgetInstance.Add(layout, Find("clock"), new Rect(0, 0, 0, 0));
        var covers = WidgetInstance.Add(layout, Find("steam-covers"), new Rect(0, 0, 0, 0));
        var drives = WidgetInstance.Add(layout, Find("drives"), new Rect(0, 0, 0, 0));

        Arranger.Arrange(layout, catalog, [clock, covers, drives]);
        return layout;
    }

    public static LayoutFile ClockDisks(IReadOnlyList<WidgetTemplate> catalog)
    {
        var layout = new LayoutFile { BaseImage = SpotlightImage };
        WidgetTemplate Find(string key) => catalog.First(t => t.Key == key);

        var clock = WidgetInstance.Add(layout, Find("clock"), new Rect(0, 0, 0, 0));
        var drives = WidgetInstance.Add(layout, Find("drives"), new Rect(0, 0, 0, 0));

        Arranger.Arrange(layout, catalog, [clock, drives]);
        return layout;
    }
}
