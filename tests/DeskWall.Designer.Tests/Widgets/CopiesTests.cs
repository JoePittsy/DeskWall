using System.IO;
using DeskWall.Core;
using DeskWall.Core.Layout;
using DeskWall.Designer.Model;
using Xunit;
using DeskWall.Core.Widgets;

namespace DeskWall.Designer.Tests.Widgets;

/// <summary>What was WidgetInstanceTests. A copy is only a key, an origin and the knobs the owner
/// changed; everything a placed widget draws is its expansion, so the behaviours that still apply
/// are asserted on <see cref="WidgetExpander.Expand"/> of the layout <see cref="Copies"/> edits.</summary>
public class CopiesTests
{
    private static LayoutFile NewLayout() => new() { BaseImage = "x.jpg" };

    private static LayoutFile Expand(LayoutFile layout, params WidgetTemplate[] templates)
        => WidgetExpander.Expand(layout, key => templates.FirstOrDefault(t => t.Key == key)).Layout;

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

    /// <summary>A drive widget: one knob substituting "{drive}" into two component bindings.</summary>
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

    // ---- Add ---------------------------------------------------------------------------------------

    [Fact]
    public void Add_Records_A_Copy_And_Nothing_Else()
    {
        var layout = NewLayout();
        var id = Copies.Add(layout, ClockTemplate(), 3220, 40);

        Assert.Equal("clock-1", id);
        var copy = Assert.Single(layout.Copies!);
        Assert.Equal(("clock-1", "clock", 3220, 40, 0), (copy.Id, copy.Widget, copy.X, copy.Y, copy.Z));
        Assert.Empty(copy.Knobs);
        Assert.Empty(copy.Overrides);
        Assert.Equal(2, layout.Version);
        Assert.Empty(layout.Components);   // the parts only exist in the expansion
        Assert.Empty(layout.Sources);
        Assert.Null(layout.Widgets);
    }

    [Fact]
    public void The_Expansion_Prefixes_Ids_Offsets_Rects_And_Brings_The_Source()
    {
        var layout = NewLayout();
        Copies.Add(layout, ClockTemplate(), 3220, 40);

        var x = Expand(layout, ClockTemplate());
        var comp = Assert.IsType<TextDef>(Assert.Single(x.Components));
        Assert.Equal("clock-1.clock", comp.Id);
        Assert.Equal("clock-1", comp.Widget);
        Assert.Equal(new Rect(3220, 40, 172, 78), comp.Rect);
        Assert.Equal("time", Assert.Single(x.Sources).Name);
    }

    [Fact]
    public void Add_Twice_Increments_The_Copy_Id_And_Shares_The_Source()
    {
        var layout = NewLayout();
        Assert.Equal("clock-1", Copies.Add(layout, ClockTemplate(), 0, 0));
        Assert.Equal("clock-2", Copies.Add(layout, ClockTemplate(), 0, 100));
        var x = Expand(layout, ClockTemplate());
        Assert.Equal(2, x.Components.Count);
        Assert.Single(x.Sources);   // "time" shared, not duplicated
    }

    /// <summary>A loose component the migrator left with its v1 stamped id must not collide with a
    /// new copy's expanded id.</summary>
    [Fact]
    public void Add_Skips_An_Id_A_Loose_V1_Component_Still_Uses()
    {
        var layout = NewLayout();
        layout.Components.Add(new TextDef { Id = "clock-1.clock", Rect = new Rect(0, 0, 10, 10), Text = PropertyValue.Literal("x") });
        Assert.Equal("clock-2", Copies.Add(layout, ClockTemplate(), 0, 0));
    }

    [Fact]
    public void A_Layout_Source_Of_The_Same_Name_But_Another_Definition_Renames_The_Copys()
    {
        var layout = NewLayout();
        layout.Sources.Add(new SourceDef { Name = "weather", Type = "rss" });
        var id = Copies.Add(layout, WeatherTemplate(), 0, 0);

        var x = Expand(layout, WeatherTemplate());
        Assert.Contains(x.Sources, s => s.Name == "weather" && s.Type == "rss");
        Assert.Contains(x.Sources, s => s.Name == "weather2" && s.Type == "http");
        var temp = (TextDef)x.Components.Single(c => c.Id == $"{id}.temp");
        Assert.Equal("weather2", temp.Text.Binding!.Path[0].ToString());
    }

    [Fact]
    public void Knob_Defaults_Apply_Without_Being_Stored()
    {
        var layout = NewLayout();
        var id = Copies.Add(layout, DialTemplate(), 0, 0);

        Assert.Empty(layout.Copies!.Single().Knobs);
        var dial = (DialDef)Expand(layout, DialTemplate()).Components.Single(c => c.Id == $"{id}.dial");
        Assert.Equal("0.9", dial.Threshold.LiteralText);
    }

    [Fact]
    public void A_Town_Knob_Default_Applies_Without_Any_Network_Call()
    {
        var layout = NewLayout();
        Copies.Add(layout, WeatherTemplate(), 0, 0);
        var url = Expand(layout, WeatherTemplate()).Sources.Single(s => s.Name == "weather").Settings["url"];
        Assert.Equal("https://api.open-meteo.com/v1/forecast?latitude=53.8008&longitude=-1.5491", url);
    }

    // ---- Remove, Find, Components ----------------------------------------------------------------

    [Fact]
    public void Remove_Takes_The_Copy_And_With_It_Its_Parts_And_Sources()
    {
        var layout = NewLayout();
        var a = Copies.Add(layout, ClockTemplate(), 0, 0);
        var b = Copies.Add(layout, ClockTemplate(), 0, 100);

        Assert.True(Copies.Remove(layout, a));
        Assert.False(Copies.Remove(layout, a));
        Assert.Null(Copies.Find(layout, a));
        Assert.Single(Expand(layout, ClockTemplate()).Sources);   // "time" still used by b

        Copies.Remove(layout, b);
        var x = Expand(layout, ClockTemplate());
        Assert.Empty(x.Components);
        Assert.Empty(x.Sources);
    }

    [Fact]
    public void Components_Returns_Only_This_Copys_Parts()
    {
        var layout = NewLayout();
        var a = Copies.Add(layout, DialTemplate(), 0, 0);
        Copies.Add(layout, ClockTemplate(), 0, 100);
        var x = WidgetExpander.Expand(layout, k => k == "dial" ? DialTemplate() : ClockTemplate());
        Assert.Equal(["dial-1.dial", "dial-1.value", "dial-1.label"], Copies.Components(x, a).Select(c => c.Id));
    }

    // ---- Bounds ------------------------------------------------------------------------------------

    [Fact]
    public void Bounds_Is_The_Union_Of_The_Expanded_Parts()
    {
        var layout = NewLayout();
        var id = Copies.Add(layout, DialTemplate(), 100, 200);
        var x = WidgetExpander.Expand(layout, _ => DialTemplate());
        Assert.Equal(new Rect(100, 200, 80, 80), Copies.Bounds(Copies.Find(layout, id)!, x, _ => DialTemplate()));
    }

    [Fact]
    public void Bounds_Falls_Back_To_The_Widget_Size_When_Every_Part_Is_Hidden()
    {
        var layout = NewLayout();
        var id = Copies.Add(layout, ClockTemplate(), 100, 200);
        var copy = Copies.Find(layout, id)!;
        copy.Overrides["components.clock.hidden"] = PropertyValue.Literal("true");
        var x = WidgetExpander.Expand(layout, _ => ClockTemplate());
        Assert.Empty(x.Layout.Components);
        Assert.Equal(new Rect(100, 200, 172, 78), Copies.Bounds(copy, x, _ => ClockTemplate()));
    }

    [Fact]
    public void Bounds_Of_A_Missing_Widget_Is_A_Broken_Link_Box_At_The_Origin()
    {
        var layout = NewLayout();
        var id = Copies.Add(layout, ClockTemplate(), 100, 200);
        var x = WidgetExpander.Expand(layout, _ => null);
        Assert.Equal(ExpandProblemKind.MissingWidget, Assert.Single(x.Problems).Kind);
        Assert.Equal(new Rect(100, 200, 172, 40), Copies.Bounds(Copies.Find(layout, id)!, x, _ => null));
    }

    [Fact]
    public void Bounds_Of_A_Broken_Widget_Is_A_Broken_Link_Box_And_Does_Not_Throw()
    {
        var layout = NewLayout();
        var id = Copies.Add(layout, ClockTemplate(), 100, 200);
        Func<string, WidgetTemplate?> broken = _ => throw new FormatException("bad file");
        var x = WidgetExpander.Expand(layout, broken);
        Assert.Equal(ExpandProblemKind.BrokenWidget, Assert.Single(x.Problems).Kind);
        Assert.Equal(new Rect(100, 200, 172, 40), Copies.Bounds(Copies.Find(layout, id)!, x, broken));
    }

    // ---- SetKnob -----------------------------------------------------------------------------------

    [Fact]
    public void SetKnob_Stores_A_Changed_Value_And_Removes_It_At_The_Default()
    {
        var layout = NewLayout();
        var template = DialTemplate();
        var id = Copies.Add(layout, template, 0, 0);
        var copy = Copies.Find(layout, id)!;

        Copies.SetKnob(layout, template, id, "warnAt", "0.83");
        Assert.Equal("0.83", copy.Knobs["warnAt"]);
        Assert.Equal("0.83", Copies.KnobValue(copy, template.Knobs[1]));
        Assert.Equal("0.83", ((DialDef)Expand(layout, template).Components.Single(c => c.Id == $"{id}.dial")).Threshold.LiteralText);

        Copies.SetKnob(layout, template, id, "warnAt", "0.9");   // the default
        Assert.False(copy.Knobs.ContainsKey("warnAt"));
        Assert.Equal("0.9", Copies.KnobValue(copy, template.Knobs[1]));
    }

    [Fact]
    public void SetKnob_Refuses_An_Unknown_Knob_Or_Copy()
    {
        var layout = NewLayout();
        var template = DialTemplate();
        var id = Copies.Add(layout, template, 0, 0);
        Assert.Throws<ArgumentException>(() => Copies.SetKnob(layout, template, id, "nope", "1"));
        Assert.Throws<ArgumentException>(() => Copies.SetKnob(layout, template, "dial-9", "warnAt", "1"));
    }

    [Fact]
    public void SetKnob_Composite_Choice_Rebinds_Two_Parts_And_Relabels_The_Third()
    {
        var layout = NewLayout();
        var template = DialTemplate();
        var id = Copies.Add(layout, template, 0, 0);

        Copies.SetKnob(layout, template, id, "metric", "GPU||hardware.gpu||hardware.gpuPct | \"{0}%\"||gpu");

        var x = Expand(layout, template);
        Assert.Equal("hardware.gpu", ((DialDef)x.Components.Single(c => c.Id == $"{id}.dial")).Fraction.Binding!.ToString());
        Assert.Equal("hardware.gpuPct | \"{0}%\"", ((TextDef)x.Components.Single(c => c.Id == $"{id}.value")).Text.Binding!.ToString());
        Assert.Equal("gpu", ((TextDef)x.Components.Single(c => c.Id == $"{id}.label")).Text.LiteralText);
    }

    /// <summary>Re-editing a token knob: expansion always substitutes into the template's own
    /// placeholders, so the second town replaces the first rather than finding nothing to replace.</summary>
    [Fact]
    public void SetKnob_Re_Edited_Town_Replaces_The_Previous_Coordinates()
    {
        var layout = NewLayout();
        var template = WeatherTemplate();
        var id = Copies.Add(layout, template, 0, 0);

        Copies.SetKnob(layout, template, id, "town", "York||53.96||-1.08");
        Copies.SetKnob(layout, template, id, "town", "Bristol||51.4545||-2.5879");

        var url = Expand(layout, template).Sources.Single(s => s.Name == "weather").Settings["url"];
        Assert.Equal("https://api.open-meteo.com/v1/forecast?latitude=51.4545&longitude=-2.5879", url);
    }

    [Fact]
    public void SetKnob_Re_Edited_Drive_Repoints_Both_Bindings_And_Keeps_Them_Bound()
    {
        var layout = NewLayout();
        var template = DriveTemplate();
        var id = Copies.Add(layout, template, 0, 0);

        Copies.SetKnob(layout, template, id, "drive", "D");
        Copies.SetKnob(layout, template, id, "drive", "E");

        var x = Expand(layout, template);
        var bar = (BarDef)x.Components.Single(c => c.Id == $"{id}.bar");
        var free = (TextDef)x.Components.Single(c => c.Id == $"{id}.free");
        Assert.Equal("disks.drives[E].usedFraction", bar.Fraction.Binding!.ToString());
        Assert.Equal("disks.drives[E].freeGB | \"{0:N0} GB\"", free.Text.Binding!.ToString());
    }

    // ---- The shipped templates' own knobs -----------------------------------------------------
    //
    // A "sets" path naming a component, property or setting the widget does not have is the one
    // mistake a template author actually makes. The expander reports it as an orphan knob, so these
    // load widgets/*.json exactly as shipped and drive the real knobs.

    private static WidgetTemplate Shipped(string key)
        => TestRepo.Widgets().FirstOrDefault(t => t.Key == key) ?? throw new InvalidOperationException($"no shipped widget \"{key}\"");

    public static TheoryData<string> ShippedKeys()
    {
        var data = new TheoryData<string>();
        foreach (var t in TestRepo.Widgets()) data.Add(t.Key);
        return data;
    }

    private static (LayoutFile Layout, WidgetTemplate Template, string Id) Placed(string key)
    {
        var t = Shipped(key);
        var layout = NewLayout();
        return (layout, t, Copies.Add(layout, t, 0, 0));
    }

    /// <summary>Every knob of every shipped widget expands with no problem at its default and, for a
    /// choice knob, at every one of its choices.</summary>
    [Theory]
    [MemberData(nameof(ShippedKeys))]
    public void Every_Shipped_Knob_Default_And_Choice_Expands_Cleanly(string key)
    {
        var (layout, t, id) = Placed(key);
        Assert.Empty(WidgetExpander.Expand(layout, _ => t).Problems);
        foreach (var knob in t.Knobs)
            foreach (var choice in knob.Choices ?? [])
            {
                Copies.SetKnob(layout, t, id, knob.Id, choice);
                Assert.Empty(WidgetExpander.Expand(layout, _ => t).Problems);
            }
    }

    [Fact]
    public void Text_Widget_Knobs_Set_The_Words_The_Size_And_The_Alignment()
    {
        var (layout, t, id) = Placed("text");
        var x = Expand(layout, t);
        Assert.Equal("Your text here", ((TextDef)x.Components.Single(c => c.Id == $"{id}.label")).Text.LiteralText);
        Assert.Empty(x.Sources);   // the primitive: no source at all

        Copies.SetKnob(layout, t, id, "text", "Render box");
        Copies.SetKnob(layout, t, id, "size", "28");
        Copies.SetKnob(layout, t, id, "align", "Left");

        var label = (TextDef)Expand(layout, t).Components.Single(c => c.Id == $"{id}.label");
        Assert.Equal("Render box", label.Text.LiteralText);
        Assert.Equal("28", label.Size.LiteralText);
        Assert.Equal("Left", label.Align.LiteralText);
    }

    [Fact]
    public void Image_Widget_Knobs_Set_The_File_And_The_Fit()
    {
        var (layout, t, id) = Placed("image");
        Assert.Equal("", ((ImageDef)Expand(layout, t).Components.Single(c => c.Id == $"{id}.picture")).Source.LiteralText);

        Copies.SetKnob(layout, t, id, "path", @"C:\pictures\view.png");
        Copies.SetKnob(layout, t, id, "fit", "Cover");

        var picture = (ImageDef)Expand(layout, t).Components.Single(c => c.Id == $"{id}.picture");
        Assert.Equal(@"C:\pictures\view.png", picture.Source.LiteralText);
        Assert.Equal("Cover", picture.Fit.LiteralText);
    }

    [Theory]
    [InlineData("Numeric (yyyy-MM-dd)||time.date", "time.date")]
    [InlineData("Weekday only||time.weekday", "time.weekday")]
    [InlineData("Day and month||time.now | \"d MMMM\"", "time.now | \"d MMMM\"")]
    public void Date_Widget_Wording_Knob_Rebinds_The_Line(string choice, string expected)
    {
        var (layout, t, id) = Placed("date");
        Assert.Equal("time.now | \"dddd d MMMM\"", ((TextDef)Expand(layout, t).Components.Single(c => c.Id == $"{id}.date")).Text.Binding!.ToString());

        Copies.SetKnob(layout, t, id, "format", choice);

        var date = (TextDef)Expand(layout, t).Components.Single(c => c.Id == $"{id}.date");
        Assert.True(date.Text.IsBound);
        Assert.Equal(expected, date.Text.Binding!.ToString());
    }

    [Fact]
    public void Uptime_Widget_Binds_The_Ready_Formatted_System_Field()
    {
        var (layout, t, id) = Placed("uptime");
        var x = Expand(layout, t);
        Assert.Equal("system.uptimeText | \"up {0}\"", ((TextDef)x.Components.Single(c => c.Id == $"{id}.uptime")).Text.Binding!.ToString());
        Assert.Equal("system", Assert.Single(x.Sources).Type);
    }

    [Fact]
    public void Command_Widget_Knobs_Set_The_Command_Its_Arguments_And_Its_Timeout()
    {
        var (layout, t, id) = Placed("command");
        Copies.SetKnob(layout, t, id, "command", @"C:\tools\status.exe");
        Copies.SetKnob(layout, t, id, "args", "--one-line");
        Copies.SetKnob(layout, t, id, "timeout", "5");

        var x = Expand(layout, t);
        var source = x.Sources.Single(s => s.Name == "command");
        Assert.Equal(@"C:\tools\status.exe", source.Settings["command"]);
        Assert.Equal("--one-line", source.Settings["args"]);
        Assert.Equal("5", source.Settings["timeout"]);
        // stdout only -- layouts/README.md "Command source stderr".
        Assert.Equal("command.text", ((TextDef)x.Components.Single(c => c.Id == $"{id}.line")).Text.Binding!.ToString());
    }

    [Fact]
    public void Headline_Widget_Url_Knob_Sets_The_Feed_And_The_Line_Is_The_First_Items_Title()
    {
        var (layout, t, id) = Placed("headline");
        Assert.Equal("feed.items[0].title", ((TextDef)Expand(layout, t).Components.Single(c => c.Id == $"{id}.headline")).Text.Binding!.ToString());

        Copies.SetKnob(layout, t, id, "url", "https://example.invalid/atom.xml");

        var source = Expand(layout, t).Sources.Single(s => s.Name == "feed");
        Assert.Equal("rss", source.Type);
        Assert.Equal("https://example.invalid/atom.xml", source.Settings["url"]);
    }

    /// <summary>The volume widget stands on the `audio` source alone: no script, no file.</summary>
    [Fact]
    public void Volume_Widget_Is_A_Dial_On_The_Audio_Source_That_Turns_Red_When_Muted()
    {
        var (layout, t, id) = Placed("volume");
        var x = Expand(layout, t);

        var source = Assert.Single(x.Sources);
        Assert.Equal("audio", source.Type);
        Assert.Empty(source.Settings);

        var dial = (DialDef)x.Components.Single(c => c.Id == $"{id}.dial");
        Assert.Equal("audio.volume", dial.Fraction.Binding!.ToString());
        Assert.Equal("audio.muted | \"?true=#FFD13438,*=#EBFFFFFF\"", dial.Fill.Binding!.ToString());
        Assert.Equal("audio.volumePct | \"{0}%\"", ((TextDef)x.Components.Single(c => c.Id == $"{id}.value")).Text.Binding!.ToString());
        Assert.Equal("audio.muted | \"?true=muted,*=vol\"", ((TextDef)x.Components.Single(c => c.Id == $"{id}.label")).Text.Binding!.ToString());
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
