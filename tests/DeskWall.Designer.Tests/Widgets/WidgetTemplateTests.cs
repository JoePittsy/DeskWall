using System.IO;
using DeskWall.Core;
using DeskWall.Core.Layout;
using DeskWall.Designer.Model.Widgets;
using Xunit;

namespace DeskWall.Designer.Tests.Widgets;

public class WidgetTemplateTests
{
    private static string WriteTemp(string name, string json)
    {
        var dir = Path.Combine(Path.GetTempPath(), "deskwall-tests", "widget-templates");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, json);
        return path;
    }

    private const string ValidClock = """
    {
      "version": 1, "name": "Clock", "description": "The current time.", "size": [172, 78],
      "sources": [ { "name": "time", "type": "time" } ],
      "components": [ { "type": "text", "id": "clock", "rect": [0, 0, 172, 78], "text": { "bind": "time.now | HH:mm" } } ],
      "knobs": []
    }
    """;

    [Fact]
    public void Loads_Name_Description_Size_Sources_Components()
    {
        var path = WriteTemp("clock-valid.json", ValidClock);
        var t = WidgetTemplate.Load(path);
        Assert.Equal("Clock", t.Name);
        Assert.Equal("clock-valid", t.Key);
        Assert.Equal("The current time.", t.Description);
        Assert.Equal(172, t.Width);
        Assert.Equal(78, t.Height);
        Assert.Equal("top", t.Anchor);
        Assert.Null(t.Requires);
        Assert.Single(t.Sources);
        Assert.Equal("time", t.Sources[0].Name);
        var text = Assert.IsType<TextDef>(Assert.Single(t.Components));
        Assert.Equal("clock", text.Id);
        Assert.Equal(new Rect(0, 0, 172, 78), text.Rect);
    }

    [Fact]
    public void Anchor_Bottom_Is_Read()
    {
        var path = WriteTemp("bottom.json", """
        { "version": 1, "name": "Drives", "description": "d", "size": [172, 92], "anchor": "bottom",
          "sources": [], "components": [], "knobs": [] }
        """);
        Assert.Equal("bottom", WidgetTemplate.Load(path).Anchor);
    }

    [Fact]
    public void Requires_Is_Read()
    {
        var path = WriteTemp("vpn.json", """
        { "version": 1, "name": "VPN", "description": "d", "size": [172, 20], "requires": "Needs Tailscale",
          "sources": [], "components": [], "knobs": [] }
        """);
        Assert.Equal("Needs Tailscale", WidgetTemplate.Load(path).Requires);
    }

    [Fact]
    public void Knobs_Are_Read_With_Choices_Min_Max()
    {
        var path = WriteTemp("dial.json", """
        { "version": 1, "name": "Dial", "description": "d", "size": [80, 78], "sources": [], "components": [],
          "knobs": [
            { "id": "metric", "label": "Metric", "type": "choice", "default": "CPU", "choices": ["CPU", "GPU"], "sets": ["components.dial.fraction"] },
            { "id": "warnAt", "label": "Warn at", "type": "number", "default": "0.9", "min": 0, "max": 1, "sets": ["components.dial.threshold"] }
          ] }
        """);
        var t = WidgetTemplate.Load(path);
        Assert.Equal(2, t.Knobs.Count);
        Assert.Equal(KnobType.Choice, t.Knobs[0].Type);
        Assert.Equal(["CPU", "GPU"], t.Knobs[0].Choices);
        Assert.Equal(KnobType.Number, t.Knobs[1].Type);
        Assert.Equal(0, t.Knobs[1].Min);
        Assert.Equal(1, t.Knobs[1].Max);
    }

    [Fact]
    public void Missing_Name_Names_The_File_And_Field()
    {
        var path = WriteTemp("no-name.json", """{ "version": 1, "description": "d", "size": [10, 10], "sources": [], "components": [], "knobs": [] }""");
        var ex = Assert.Throws<FormatException>(() => WidgetTemplate.Load(path));
        Assert.Contains(path, ex.Message);
        Assert.Contains("name", ex.Message);
    }

    [Fact]
    public void Missing_Description_Names_The_File_And_Field()
    {
        var path = WriteTemp("no-desc.json", """{ "version": 1, "name": "X", "size": [10, 10], "sources": [], "components": [], "knobs": [] }""");
        var ex = Assert.Throws<FormatException>(() => WidgetTemplate.Load(path));
        Assert.Contains(path, ex.Message);
        Assert.Contains("description", ex.Message);
    }

    [Fact]
    public void Bad_Size_Names_The_File_And_Field()
    {
        var path = WriteTemp("bad-size.json", """{ "version": 1, "name": "X", "description": "d", "size": [10], "sources": [], "components": [], "knobs": [] }""");
        var ex = Assert.Throws<FormatException>(() => WidgetTemplate.Load(path));
        Assert.Contains(path, ex.Message);
        Assert.Contains("size", ex.Message);
    }

    [Fact]
    public void Bad_Anchor_Names_The_File_And_Field()
    {
        var path = WriteTemp("bad-anchor.json", """{ "version": 1, "name": "X", "description": "d", "size": [10, 10], "anchor": "middle", "sources": [], "components": [], "knobs": [] }""");
        var ex = Assert.Throws<FormatException>(() => WidgetTemplate.Load(path));
        Assert.Contains(path, ex.Message);
        Assert.Contains("anchor", ex.Message);
    }

    [Fact]
    public void Bad_Knob_Type_Names_The_Knob()
    {
        var path = WriteTemp("bad-knob.json", """
        { "version": 1, "name": "X", "description": "d", "size": [10, 10], "sources": [], "components": [],
          "knobs": [ { "id": "weird", "label": "Weird", "type": "nope", "default": "x", "sets": [] } ] }
        """);
        var ex = Assert.Throws<FormatException>(() => WidgetTemplate.Load(path));
        Assert.Contains(path, ex.Message);
        Assert.Contains("weird", ex.Message);
    }

    [Fact]
    public void More_Than_Five_Knobs_Throws()
    {
        var knobs = string.Join(",", Enumerable.Range(0, 6).Select(i => $"{{ \"id\": \"k{i}\", \"label\": \"K{i}\", \"type\": \"text\", \"default\": \"x\", \"sets\": [] }}"));
        var path = WriteTemp("too-many-knobs.json", $$"""
        { "version": 1, "name": "X", "description": "d", "size": [10, 10], "sources": [], "components": [], "knobs": [ {{knobs}} ] }
        """);
        var ex = Assert.Throws<FormatException>(() => WidgetTemplate.Load(path));
        Assert.Contains(path, ex.Message);
    }

    [Fact]
    public void Preview_Puts_Components_On_The_Given_Base_Untouched()
    {
        var path = WriteTemp("preview.json", ValidClock);
        var t = WidgetTemplate.Load(path);
        var layout = t.Preview("C:\\wall.jpg");
        Assert.Equal("C:\\wall.jpg", layout.BaseImage);
        Assert.Single(layout.Components);
        Assert.Single(layout.Sources);
        // a deep copy: mutating the preview's components must not touch the template's own list.
        layout.Components[0].Rect = new Rect(1, 1, 1, 1);
        Assert.Equal(new Rect(0, 0, 172, 78), t.Components[0].Rect);
    }
}
