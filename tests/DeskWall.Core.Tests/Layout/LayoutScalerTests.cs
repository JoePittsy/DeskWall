using DeskWall.Core;
using DeskWall.Core.Display;
using DeskWall.Core.Layout;
using Xunit;

public class LayoutScalerTests
{
    private const string Json = """
    { "version": 1, "baseImage": "x.jpg", "sources": [],
      "components": [
        { "type": "text", "id": "clock", "rect": [3220, 40, 172, 78], "size": 64, "text": "x" },
        { "type": "repeater", "id": "r", "rect": [3220, 1260, 172, 92], "gap": 14, "cellHeight": 46, "items": { "bind": "d.items" },
          "template": [ { "type": "bar", "id": "b", "rect": [0, 26, 172, 6], "fraction": 0.5 } ] }
      ] }
    """;

    [Fact]
    public void Scales_Rects_Templates_Sizes_And_Gaps()
    {
        var src = LayoutFile.Parse(Json);
        var from = new DisplaySignature("A", 3440, 1440, 100);
        var to = new DisplaySignature("B", 1720, 720, 100);   // exactly half
        var s = LayoutScaler.Scale(src, from, to);
        var clock = (TextDef)s.Components[0];
        Assert.Equal(new Rect(1610, 20, 86, 39), clock.Rect);
        Assert.Equal("32", clock.Size.LiteralText);
        var rep = (RepeaterDef)s.Components[1];
        Assert.Equal(new Rect(1610, 630, 86, 46), rep.Rect);
        Assert.Equal(7, rep.Gap);
        Assert.Equal("23", rep.CellHeight.LiteralText);
        Assert.Equal(new Rect(0, 13, 86, 3), rep.Template[0].Rect);
        Assert.NotSame(src, s);
        Assert.Equal(new Rect(3220, 40, 172, 78), ((TextDef)src.Components[0]).Rect);   // source untouched
    }

    [Fact]
    public void Bound_Size_And_Auto_CellHeight_Are_Left_Alone()
    {
        var src = LayoutFile.Parse("""
        { "version": 1, "baseImage": "x", "sources": [], "components": [
          { "type": "text", "id": "t", "rect": [0,0,100,100], "text": "x", "size": { "bind": "a.b" } },
          { "type": "repeater", "id": "r", "rect": [0,0,100,100], "items": { "bind": "a.c" }, "template": [] } ] }
        """);
        var s = LayoutScaler.Scale(src, new DisplaySignature("A", 200, 200, 100), new DisplaySignature("B", 100, 100, 100));
        Assert.True(((TextDef)s.Components[0]).Size.IsBound);
        Assert.Equal("auto", ((RepeaterDef)s.Components[1]).CellHeight.LiteralText);
    }
}
