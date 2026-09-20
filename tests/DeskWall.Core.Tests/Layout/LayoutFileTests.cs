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
}
