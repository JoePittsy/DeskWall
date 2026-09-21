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
}
