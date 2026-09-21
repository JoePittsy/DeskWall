using System.IO;
using DeskWall.Core;
using DeskWall.Core.Layout;
using DeskWall.Designer.Model.Widgets;
using Xunit;

namespace DeskWall.Designer.Tests.Widgets;

public class WidgetInstanceTests
{
    private static LayoutFile NewLayout() => new() { BaseImage = "x.jpg" };

    private static WidgetTemplate ClockTemplate() => new()
    {
        Name = "Clock",
        Key = "clock",
        Description = "d",
        Width = 172,
        Height = 78,
        Sources = [new SourceDef { Name = "time", Type = "time" }],
        Components = [new TextDef { Id = "clock", Rect = new Rect(0, 0, 172, 78), Text = PropertyValue.Bound(DeskWall.Core.Bindings.Binding.Parse("time.now | HH:mm")) }],
    };

    private static WidgetTemplate DialTemplate() => new()
    {
        Name = "Dial",
        Key = "dial",
        Description = "d",
        Width = 80,
        Height = 80,
        Sources = [new SourceDef { Name = "hardware", Type = "hardware" }],
        Components =
        [
            new DialDef { Id = "dial", Rect = new Rect(0, 0, 80, 80), Fraction = PropertyValue.Bound(DeskWall.Core.Bindings.Binding.Parse("hardware.cpu")), Threshold = PropertyValue.Literal(1.0) },
            new TextDef { Id = "value", Rect = new Rect(0, 26, 80, 28), Text = PropertyValue.Bound(DeskWall.Core.Bindings.Binding.Parse("hardware.cpuPct | \"{0}%\"")) },
            new TextDef { Id = "label", Rect = new Rect(0, 62, 80, 16), Text = PropertyValue.Literal("cpu") },
        ],
        Knobs =
        [
            new Knob("metric", "Metric", KnobType.Choice, "CPU||hardware.cpu||hardware.cpuPct | \"{0}%\"||cpu",
                ["components.dial.fraction=bind:hardware.cpu", "components.value.text=bind:hardware.cpuPct | \"{0}%\"", "components.label.text"],
                ["CPU||hardware.cpu||hardware.cpuPct | \"{0}%\"||cpu", "GPU||hardware.gpu||hardware.gpuPct | \"{0}%\"||gpu"], null, null),
            new Knob("warnAt", "Warn at", KnobType.Number, "0.9", ["components.dial.threshold"], null, 0, 1),
        ],
    };

    private static WidgetTemplate WeatherTemplate() => new()
    {
        Name = "Weather",
        Key = "weather",
        Description = "d",
        Width = 172,
        Height = 60,
        Sources = [new SourceDef { Name = "weather", Type = "http", Settings = new() { ["url"] = "https://api.open-meteo.com/v1/forecast?latitude={lat}&longitude={lon}" } }],
        Components = [new TextDef { Id = "temp", Rect = new Rect(0, 0, 108, 52), Text = PropertyValue.Bound(DeskWall.Core.Bindings.Binding.Parse("weather.json.current.temperature_2m")) }],
        Knobs = [new Knob("town", "Town", KnobType.Town, "Leeds||53.8008||-1.5491", ["sources.weather.settings.url:{lat}", "sources.weather.settings.url:{lon}"], null, null, null)],
    };

    /// <summary>A drive widget: one knob substituting "{drive}" into two component <em>bindings</em>
    /// rather than into two literals. Nothing shipped is shaped like this yet; the editor's Drive
    /// knob writes exactly this.</summary>
    private static WidgetTemplate DriveTemplate() => new()
    {
        Name = "Drive",
        Key = "drive",
        Description = "d",
        Width = 172,
        Height = 40,
        Sources = [new SourceDef { Name = "disks", Type = "disks" }],
        Components =
        [
            new BarDef { Id = "bar", Rect = new Rect(0, 0, 172, 6), Fraction = PropertyValue.Bound(DeskWall.Core.Bindings.Binding.Parse("disks.drives[{drive}].usedFraction")) },
            new TextDef { Id = "free", Rect = new Rect(0, 10, 172, 20), Text = PropertyValue.Bound(DeskWall.Core.Bindings.Binding.Parse("disks.drives[{drive}].freeGB | \"{0:N0} GB\"")) },
        ],
        Knobs = [new Knob("drive", "Drive", KnobType.Drive, "C", ["components.bar.fraction:{drive}", "components.free.text:{drive}"], null, null, null)],
    };

    // ---- Add: prefixing, offset, source add, instance id -----------------------------------

    [Fact]
    public void Add_Prefixes_Ids_Offsets_Rects_Adds_Source_And_Widget_Record()
    {
        var layout = NewLayout();
        var id = WidgetInstance.Add(layout, ClockTemplate(), new Rect(3220, 40, 0, 0));

        Assert.Equal("clock-1", id);
        var comp = Assert.IsType<TextDef>(Assert.Single(layout.Components));
        Assert.Equal("clock-1.clock", comp.Id);
        Assert.Equal("clock-1", comp.Widget);
        Assert.Equal(new Rect(3220, 40, 172, 78), comp.Rect);
        Assert.Single(layout.Sources);
        Assert.Equal("time", layout.Sources[0].Name);
        Assert.Equal("clock", layout.Widgets!["clock-1"].Template);
    }

    [Fact]
    public void Add_Twice_Increments_The_Instance_Id()
    {
        var layout = NewLayout();
        var a = WidgetInstance.Add(layout, ClockTemplate(), new Rect(0, 0, 0, 0));
        var b = WidgetInstance.Add(layout, ClockTemplate(), new Rect(0, 100, 0, 0));
        Assert.Equal("clock-1", a);
        Assert.Equal("clock-2", b);
        Assert.Equal(2, layout.Components.Count);
        Assert.Equal(2, layout.Widgets!.Count);
    }

    [Fact]
    public void Add_Reuses_A_Source_With_The_Same_Name_And_Type()
    {
        var layout = NewLayout();
        WidgetInstance.Add(layout, ClockTemplate(), new Rect(0, 0, 0, 0));
        WidgetInstance.Add(layout, ClockTemplate(), new Rect(0, 100, 0, 0));
        Assert.Single(layout.Sources);   // "time" reused, not duplicated
    }

    [Fact]
    public void Add_Renames_On_A_Type_Clash_And_Rewrites_The_New_Components_Binding()
    {
        var layout = NewLayout();
        layout.Sources.Add(new SourceDef { Name = "weather", Type = "rss" });   // pre-existing, different type

        var id = WidgetInstance.Add(layout, WeatherTemplate(), new Rect(0, 0, 0, 0));

        Assert.Equal(2, layout.Sources.Count);
        Assert.Contains(layout.Sources, s => s.Name == "weather" && s.Type == "rss");
        Assert.Contains(layout.Sources, s => s.Name == "weather2" && s.Type == "http");

        var temp = layout.Components.Single(c => c.Id == $"{id}.temp") as TextDef;
        Assert.Equal("weather2", temp!.Text.Binding!.Path[0].ToString());
    }

    // ---- Knob defaults on Add ----------------------------------------------------------------

    [Fact]
    public void Add_Applies_Knob_Defaults()
    {
        var layout = NewLayout();
        var id = WidgetInstance.Add(layout, DialTemplate(), new Rect(0, 0, 0, 0));

        var dial = (DialDef)layout.Components.Single(c => c.Id == $"{id}.dial");
        Assert.Equal("0.9", dial.Threshold.LiteralText);
        Assert.Equal("0.9", layout.Widgets![id].Knobs["warnAt"]);
        Assert.Equal("CPU||hardware.cpu||hardware.cpuPct | \"{0}%\"||cpu", layout.Widgets[id].Knobs["metric"]);
    }

    [Fact]
    public void Add_Applies_A_Town_Knob_Default_Without_Any_Network_Call()
    {
        var layout = NewLayout();
        var id = WidgetInstance.Add(layout, WeatherTemplate(), new Rect(0, 0, 0, 0));
        var url = layout.Sources.Single(s => s.Name == "weather").Settings["url"];
        Assert.DoesNotContain("{lat}", url);
        Assert.DoesNotContain("{lon}", url);
        Assert.Contains("latitude=53.8008", url);
        Assert.Contains("longitude=-1.5491", url);
    }

    // ---- Components / Bounds -----------------------------------------------------------------

    [Fact]
    public void Bounds_Is_The_Union_Of_The_Instances_Components()
    {
        var layout = NewLayout();
        var id = WidgetInstance.Add(layout, DialTemplate(), new Rect(100, 200, 0, 0));
        var bounds = WidgetInstance.Bounds(layout, id);
        Assert.Equal(new Rect(100, 200, 80, 80), bounds);
    }

    [Fact]
    public void Components_Returns_Only_This_Instances_Components()
    {
        var layout = NewLayout();
        var a = WidgetInstance.Add(layout, ClockTemplate(), new Rect(0, 0, 0, 0));
        var b = WidgetInstance.Add(layout, ClockTemplate(), new Rect(0, 100, 0, 0));
        Assert.Single(WidgetInstance.Components(layout, a));
        Assert.Single(WidgetInstance.Components(layout, b));
        Assert.All(WidgetInstance.Components(layout, a), c => Assert.Equal(a, c.Widget));
    }

    // ---- Remove: cleans up orphaned sources, keeps shared ones -------------------------------

    [Fact]
    public void Remove_Deletes_A_Source_Nothing_Else_Binds_To()
    {
        var layout = NewLayout();
        var id = WidgetInstance.Add(layout, ClockTemplate(), new Rect(0, 0, 0, 0));
        WidgetInstance.Remove(layout, id);
        Assert.Empty(layout.Components);
        Assert.Empty(layout.Sources);
        Assert.False(layout.Widgets!.ContainsKey(id));
    }

    [Fact]
    public void Remove_Keeps_A_Source_Another_Instance_Still_Binds_To()
    {
        var layout = NewLayout();
        var a = WidgetInstance.Add(layout, ClockTemplate(), new Rect(0, 0, 0, 0));
        var b = WidgetInstance.Add(layout, ClockTemplate(), new Rect(0, 100, 0, 0));
        WidgetInstance.Remove(layout, a);
        Assert.Single(layout.Sources);   // "time" still used by b
        WidgetInstance.Remove(layout, b);
        Assert.Empty(layout.Sources);
    }

    // ---- SetKnob: literal, setting, token, binding --------------------------------------------

    [Fact]
    public void SetKnob_Writes_A_Literal()
    {
        var layout = NewLayout();
        var template = DialTemplate();
        var id = WidgetInstance.Add(layout, template, new Rect(0, 0, 0, 0));

        WidgetInstance.SetKnob(layout, template, id, "warnAt", "0.83");

        var dial = (DialDef)layout.Components.Single(c => c.Id == $"{id}.dial");
        Assert.Equal("0.83", dial.Threshold.LiteralText);
        Assert.Equal("0.83", layout.Widgets![id].Knobs["warnAt"]);
    }

    [Fact]
    public void SetKnob_Writes_A_Setting_Substitution_Token()
    {
        var layout = NewLayout();
        var template = WeatherTemplate();
        var id = WidgetInstance.Add(layout, template, new Rect(0, 0, 0, 0));

        WidgetInstance.SetKnob(layout, template, id, "town", "Leeds||53.8008||-1.5491");

        var url = layout.Sources.Single(s => s.Name == "weather").Settings["url"];
        Assert.Equal("https://api.open-meteo.com/v1/forecast?latitude=53.8008&longitude=-1.5491", url);
        Assert.Equal("Leeds||53.8008||-1.5491", layout.Widgets![id].Knobs["town"]);
    }

    /// <summary>Re-editing a knob whose sets already replaced the template's placeholders once
    /// must still work: the instance's current URL no longer contains "{lat}"/"{lon}" after the
    /// Add-time default is applied, so a second SetKnob has to substitute from the template's
    /// own value again, not from what is currently sitting on the instance.</summary>
    [Fact]
    public void SetKnob_Re_Edited_Town_Replaces_The_Previous_Coordinates_Not_Just_The_First_Ones()
    {
        var layout = NewLayout();
        var template = WeatherTemplate();
        var id = WidgetInstance.Add(layout, template, new Rect(0, 0, 0, 0));   // default: Leeds

        WidgetInstance.SetKnob(layout, template, id, "town", "Leeds||53.8008||-1.5491");
        WidgetInstance.SetKnob(layout, template, id, "town", "Bristol||51.4545||-2.5879");

        var url = layout.Sources.Single(s => s.Name == "weather").Settings["url"];
        Assert.Equal("https://api.open-meteo.com/v1/forecast?latitude=51.4545&longitude=-2.5879", url);
        Assert.DoesNotContain("{lat}", url);
        Assert.DoesNotContain("{lon}", url);
        Assert.DoesNotContain("53.8008", url);
        Assert.Equal("Bristol||51.4545||-2.5879", layout.Widgets![id].Knobs["town"]);
    }

    [Fact]
    public void SetKnob_Writes_A_Setting_Plain_Overwrite_When_There_Is_No_Token()
    {
        var layout = NewLayout();
        var template = new WidgetTemplate
        {
            Name = "S", Key = "s", Description = "d", Width = 10, Height = 10,
            Sources = [new SourceDef { Name = "src", Type = "command", Settings = new() { ["timeout"] = "10" } }],
            Components = [],
            Knobs = [new Knob("timeout", "Timeout", KnobType.Number, "10", ["sources.src.settings.timeout"], null, null, null)],
        };
        var id = WidgetInstance.Add(layout, template, new Rect(0, 0, 0, 0));

        WidgetInstance.SetKnob(layout, template, id, "timeout", "30");

        Assert.Equal("30", layout.Sources.Single(s => s.Name == "src").Settings["timeout"]);
    }

    [Fact]
    public void SetKnob_Writes_A_Binding_And_A_Plain_Literal_From_A_Composite_Choice()
    {
        var layout = NewLayout();
        var template = DialTemplate();
        var id = WidgetInstance.Add(layout, template, new Rect(0, 0, 0, 0));

        WidgetInstance.SetKnob(layout, template, id, "metric", "GPU||hardware.gpu||hardware.gpuPct | \"{0}%\"||gpu");

        var dial = (DialDef)layout.Components.Single(c => c.Id == $"{id}.dial");
        var value = (TextDef)layout.Components.Single(c => c.Id == $"{id}.value");
        var label = (TextDef)layout.Components.Single(c => c.Id == $"{id}.label");

        Assert.True(dial.Fraction.IsBound);
        Assert.Equal("hardware.gpu", dial.Fraction.Binding!.ToString());
        Assert.True(value.Text.IsBound);
        Assert.Equal("hardware.gpuPct | \"{0}%\"", value.Text.Binding!.ToString());
        Assert.False(label.Text.IsBound);
        Assert.Equal("gpu", label.Text.LiteralText);
    }

    /// <summary>A ":{token}" knob whose target is a <em>binding</em>, not a literal. Before this
    /// worked, ApplyTokenGroup read the template's LiteralText -- null for a bound property -- and
    /// wrote an empty literal over the binding, so a drive-keyed widget lost both its bindings the
    /// moment its knob was applied, which Add does for every knob's default.</summary>
    [Fact]
    public void SetKnob_Substitutes_Into_A_Bound_Property_And_Leaves_It_Bound()
    {
        var layout = NewLayout();
        var template = DriveTemplate();
        var id = WidgetInstance.Add(layout, template, new Rect(0, 0, 0, 0));

        WidgetInstance.SetKnob(layout, template, id, "drive", "D");

        var bar = (BarDef)layout.Components.Single(c => c.Id == $"{id}.bar");
        var free = (TextDef)layout.Components.Single(c => c.Id == $"{id}.free");
        Assert.True(bar.Fraction.IsBound);
        Assert.Equal("disks.drives[D].usedFraction", bar.Fraction.Binding!.ToString());
        Assert.True(free.Text.IsBound);
        Assert.Equal("disks.drives[D].freeGB | \"{0:N0} GB\"", free.Text.Binding!.ToString());
    }

    /// <summary>And again: the substitution comes from the template, so a second edit still finds
    /// a placeholder even though the instance no longer has one.</summary>
    [Fact]
    public void SetKnob_Re_Edited_Drive_Repoints_Both_Bindings()
    {
        var layout = NewLayout();
        var template = DriveTemplate();
        var id = WidgetInstance.Add(layout, template, new Rect(0, 0, 0, 0));

        WidgetInstance.SetKnob(layout, template, id, "drive", "D");
        WidgetInstance.SetKnob(layout, template, id, "drive", "E");

        var bar = (BarDef)layout.Components.Single(c => c.Id == $"{id}.bar");
        var free = (TextDef)layout.Components.Single(c => c.Id == $"{id}.free");
        Assert.Equal("disks.drives[E].usedFraction", bar.Fraction.Binding!.ToString());
        Assert.Equal("disks.drives[E].freeGB | \"{0:N0} GB\"", free.Text.Binding!.ToString());
        Assert.Equal("E", layout.Widgets![id].Knobs["drive"]);
    }

    // ---- The shipped templates' own knobs -----------------------------------------------------
    //
    // Everything above builds its template in code, which cannot catch the one mistake a template
    // author actually makes: a "sets" path naming a component, property or setting that is not
    // there. Such a knob throws when it is applied (or, for a setting nothing reads, quietly does
    // nothing), so these load widgets/*.json exactly as shipped and drive the real knobs.

    private static WidgetTemplate Shipped(string key)
        => TestRepo.Widgets().FirstOrDefault(t => t.Key == key) ?? throw new InvalidOperationException($"no shipped widget \"{key}\"");

    public static TheoryData<string> ShippedKeys()
    {
        var data = new TheoryData<string>();
        foreach (var t in TestRepo.Widgets()) data.Add(t.Key);
        return data;
    }

    /// <summary>Every knob of every shipped widget re-stamps: its default (applied by Add and
    /// again by hand) and, for a choice knob, every one of its choices. A typo in a "sets" path
    /// throws out of SetKnob, so this is the template-wide guard.</summary>
    [Theory]
    [MemberData(nameof(ShippedKeys))]
    public void Every_Shipped_Knob_Default_And_Choice_Applies(string key)
    {
        var t = Shipped(key);
        var layout = NewLayout();
        var id = WidgetInstance.Add(layout, t, new Rect(0, 0, 0, 0));   // Add applies every default

        foreach (var knob in t.Knobs)
        {
            WidgetInstance.SetKnob(layout, t, id, knob.Id, knob.Default);
            foreach (var choice in knob.Choices ?? []) WidgetInstance.SetKnob(layout, t, id, knob.Id, choice);
            Assert.True(layout.Widgets![id].Knobs.ContainsKey(knob.Id));
        }
    }

    [Fact]
    public void Text_Widget_Knobs_Set_The_Words_The_Size_And_The_Alignment()
    {
        var t = Shipped("text");
        var layout = NewLayout();
        var id = WidgetInstance.Add(layout, t, new Rect(0, 0, 0, 0));
        var label = (TextDef)layout.Components.Single(c => c.Id == $"{id}.label");
        Assert.Equal("Your text here", label.Text.LiteralText);
        Assert.Empty(layout.Sources);   // the primitive: no source at all

        WidgetInstance.SetKnob(layout, t, id, "text", "Render box");
        WidgetInstance.SetKnob(layout, t, id, "size", "28");
        WidgetInstance.SetKnob(layout, t, id, "align", "Left");

        Assert.Equal("Render box", label.Text.LiteralText);
        Assert.Equal("28", label.Size.LiteralText);
        Assert.Equal("Left", label.Align.LiteralText);
    }

    [Fact]
    public void Image_Widget_Knobs_Set_The_File_And_The_Fit()
    {
        var t = Shipped("image");
        var layout = NewLayout();
        var id = WidgetInstance.Add(layout, t, new Rect(0, 0, 0, 0));
        var picture = (ImageDef)layout.Components.Single(c => c.Id == $"{id}.picture");
        // Empty by default: the renderer's missing-image plate is the "put a file here" affordance,
        // and no path exists on every machine that would not draw the same plate anyway.
        Assert.Equal("", picture.Source.LiteralText);

        WidgetInstance.SetKnob(layout, t, id, "path", @"C:\pictures\view.png");
        WidgetInstance.SetKnob(layout, t, id, "fit", "Cover");

        Assert.Equal(@"C:\pictures\view.png", picture.Source.LiteralText);
        Assert.Equal("Cover", picture.Fit.LiteralText);
    }

    [Theory]
    [InlineData("Numeric (yyyy-MM-dd)||time.date", "time.date")]
    [InlineData("Weekday only||time.weekday", "time.weekday")]
    [InlineData("Day and month||time.now | \"d MMMM\"", "time.now | \"d MMMM\"")]
    public void Date_Widget_Wording_Knob_Rebinds_The_Line(string choice, string expected)
    {
        var t = Shipped("date");
        var layout = NewLayout();
        var id = WidgetInstance.Add(layout, t, new Rect(0, 0, 0, 0));
        var date = (TextDef)layout.Components.Single(c => c.Id == $"{id}.date");
        Assert.Equal("time.now | \"dddd d MMMM\"", date.Text.Binding!.ToString());

        WidgetInstance.SetKnob(layout, t, id, "format", choice);

        Assert.True(date.Text.IsBound);
        Assert.Equal(expected, date.Text.Binding!.ToString());
    }

    [Fact]
    public void Uptime_Widget_Binds_The_Ready_Formatted_System_Field()
    {
        var t = Shipped("uptime");
        var layout = NewLayout();
        var id = WidgetInstance.Add(layout, t, new Rect(0, 0, 0, 0));
        var uptime = (TextDef)layout.Components.Single(c => c.Id == $"{id}.uptime");
        Assert.Equal("system.uptimeText | \"up {0}\"", uptime.Text.Binding!.ToString());
        Assert.Equal("system", Assert.Single(layout.Sources).Type);
    }

    [Fact]
    public void Command_Widget_Knobs_Set_The_Command_Its_Arguments_And_Its_Timeout()
    {
        var t = Shipped("command");
        var layout = NewLayout();
        var id = WidgetInstance.Add(layout, t, new Rect(0, 0, 0, 0));

        WidgetInstance.SetKnob(layout, t, id, "command", @"C:\tools\status.exe");
        WidgetInstance.SetKnob(layout, t, id, "args", "--one-line");
        WidgetInstance.SetKnob(layout, t, id, "timeout", "5");

        var source = layout.Sources.Single(s => s.Name == "command");
        Assert.Equal(@"C:\tools\status.exe", source.Settings["command"]);
        Assert.Equal("--one-line", source.Settings["args"]);
        Assert.Equal("5", source.Settings["timeout"]);
        // stdout only -- layouts/README.md "Command source stderr".
        var line = (TextDef)layout.Components.Single(c => c.Id == $"{id}.line");
        Assert.Equal("command.text", line.Text.Binding!.ToString());
    }

    [Fact]
    public void Headline_Widget_Url_Knob_Sets_The_Feed_And_The_Line_Is_The_First_Items_Title()
    {
        var t = Shipped("headline");
        var layout = NewLayout();
        var id = WidgetInstance.Add(layout, t, new Rect(0, 0, 0, 0));
        var headline = (TextDef)layout.Components.Single(c => c.Id == $"{id}.headline");
        Assert.Equal("feed.items[0].title", headline.Text.Binding!.ToString());

        WidgetInstance.SetKnob(layout, t, id, "url", "https://example.invalid/atom.xml");

        var source = layout.Sources.Single(s => s.Name == "feed");
        Assert.Equal("rss", source.Type);
        Assert.Equal("https://example.invalid/atom.xml", source.Settings["url"]);
    }

    /// <summary>A command source publishes stderr verbatim, so a CLI that fails and echoes its own
    /// argument list back can put a substituted {secret:} on the wallpaper (layouts/README.md
    /// "Command source stderr"). No shipped widget may bind it.</summary>
    [Fact]
    public void No_Shipped_Widget_Binds_A_Commands_Stderr()
    {
        foreach (var file in Directory.EnumerateFiles(TestRepo.WidgetsDir, "*.json"))
            Assert.DoesNotContain(".stderr", File.ReadAllText(file), StringComparison.OrdinalIgnoreCase);
    }
}
