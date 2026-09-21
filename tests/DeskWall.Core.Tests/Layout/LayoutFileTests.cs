using DeskWall.Core;
using DeskWall.Core.Layout;
using Xunit;

namespace DeskWall.Core.Tests.Layout;

public class LayoutFileTests
{
    private const string Json = """
    {
      "version": 1, "baseImage": "C:\\wall.jpg", "baseFit": "cover", "encode": "jpeg", "jpegQuality": 92,
      "sources": [ { "name": "time", "type": "time" }, { "name": "disks", "type": "disks", "every": 300 } ],
      "components": [
        { "type": "text", "id": "clock", "rect": [3220, 48, 172, 70], "z": 1, "text": { "bind": "time.now | HH:mm" }, "font": "Segoe UI Light", "size": 64, "align": "right" },
        { "type": "repeater", "id": "drives", "rect": [3220, 1260, 172, 92], "items": { "bind": "disks.drives" }, "axis": "vertical", "gap": 0, "cellHeight": 46,
          "template": [
            { "type": "bar", "id": "bar", "rect": [0, 26, 172, 6], "fraction": { "bind": "usedFraction" }, "threshold": 0.85, "thresholdFill": "#D13438" },
            { "type": "shortcut", "id": "go", "rect": [0, 0, 172, 46], "target": { "bind": "letter | \"explorer.exe {0}:\\\\\"" }, "tooltip": "Open" }
          ] }
      ]
    }
    """;

    [Fact]
    public void Parses_Header_And_Sources()
    {
        var l = LayoutFile.Parse(Json);
        Assert.Equal(1, l.Version);
        Assert.Equal(Fit.Cover, l.BaseFit);
        Assert.Equal(92, l.JpegQuality);
        Assert.Equal(300, l.Sources[1].EverySeconds);
        Assert.Null(l.Sources[0].EverySeconds);
    }

    [Fact]
    public void Parses_Polymorphic_Components()
    {
        var l = LayoutFile.Parse(Json);
        var clock = Assert.IsType<TextDef>(l.Components[0]);
        Assert.Equal(new Rect(3220, 48, 172, 70), clock.Rect);
        Assert.Equal("time.now | \"HH:mm\"", clock.Text.Binding!.ToString());
        Assert.Equal("Segoe UI Light", clock.Font.LiteralText);
        Assert.Equal("64", clock.Size.LiteralText);
        var rep = Assert.IsType<RepeaterDef>(l.Components[1]);
        Assert.Equal(Axis.Vertical, rep.Axis);
        Assert.Equal("46", rep.CellHeight.LiteralText);
        Assert.IsType<BarDef>(rep.Template[0]);
        var sc = Assert.IsType<ShortcutDef>(rep.Template[1]);
        Assert.Equal("Open", sc.Tooltip.LiteralText);
    }

    [Fact]
    public void Defaults_Apply()
    {
        var l = LayoutFile.Parse("""{ "version": 1, "baseImage": "x.jpg", "sources": [], "components": [ { "type": "text", "id": "t", "rect": [0,0,10,10], "text": "hi" } ] }""");
        Assert.Equal(Fit.Cover, l.BaseFit);
        Assert.Equal("jpeg", l.Encode);
        Assert.Equal(92, l.JpegQuality);
        var t = (TextDef)l.Components[0];
        Assert.Equal(0, t.Z);
        Assert.Equal("hi", t.Text.LiteralText);
        Assert.Equal("Segoe UI", t.Font.LiteralText);
    }

    [Fact]
    public void RoundTrips()
    {
        var l = LayoutFile.Parse(Json);
        var again = LayoutFile.Parse(l.ToJson());
        Assert.Equal(l.ToJson(), again.ToJson());
    }

    [Fact]
    public void Unknown_Type_Throws()
        => Assert.ThrowsAny<Exception>(() => LayoutFile.Parse("""{ "version": 1, "baseImage": "x", "sources": [], "components": [ { "type": "gauge", "id": "g", "rect": [0,0,1,1] } ] }"""));

    /// <summary>The designer's widget picker fields (spec "designer widgets" section 3):
    /// component-level ownership and the layout-level instance record round trip untouched, and a
    /// layout that never used the picker keeps <c>Widgets</c> null rather than gaining an empty
    /// object on every save.</summary>
    [Fact]
    public void Widget_Fields_Round_Trip()
    {
        const string json = """
        { "version": 1, "baseImage": "x.jpg", "sources": [],
          "components": [ { "type": "text", "id": "weather-1.temp", "rect": [0,0,10,10], "text": "hi", "widget": "weather-1" } ],
          "widgets": { "weather-1": { "template": "weather", "knobs": { "town": "Leeds" }, "unlocked": true } } }
        """;
        var l = LayoutFile.Parse(json);
        Assert.Equal("weather-1", l.Components[0].Widget);
        Assert.Equal("weather", l.Widgets!["weather-1"].Template);
        Assert.Equal("Leeds", l.Widgets["weather-1"].Knobs["town"]);
        Assert.True(l.Widgets["weather-1"].Unlocked);

        var again = LayoutFile.Parse(l.ToJson());
        Assert.Equal(l.ToJson(), again.ToJson());
        Assert.Equal("weather-1", again.Components[0].Widget);
        Assert.Equal("weather", again.Widgets!["weather-1"].Template);
    }

    [Fact]
    public void Widget_Fields_Default_To_Null_Not_Empty()
    {
        var l = LayoutFile.Parse(Json);
        Assert.Null(l.Widgets);
        Assert.All(l.Components, c => Assert.Null(c.Widget));
    }

    /// <summary>Finding 10: a failing Save used to leave &lt;path&gt;.tmp behind forever.</summary>
    [Fact]
    public void Save_Failure_Leaves_No_Tmp_File()
    {
        var l = LayoutFile.Parse(Json);
        var dir = Path.Combine(Path.GetTempPath(), "deskwall-tests", "layoutfile-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        // A destination that is itself an existing directory makes File.Move fail after the tmp
        // file has already been written, which is the failure shape the finding describes.
        var path = Path.Combine(dir, "layout.json");
        Directory.CreateDirectory(path);

        Assert.ThrowsAny<Exception>(() => l.Save(path));
        Assert.False(File.Exists(path + ".tmp"));
    }
}
