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

    [Fact]
    public void Widget_Fields_Survive_A_Scale()
    {
        var src = LayoutFile.Parse("""
        { "version": 1, "baseImage": "x.jpg", "sources": [],
          "components": [ { "type": "text", "id": "clock-1.clock", "rect": [3220, 40, 172, 78], "size": 64, "text": "x", "widget": "clock-1" } ],
          "widgets": { "clock-1": { "template": "clock", "knobs": {}, "unlocked": false } } }
        """);
        var s = LayoutScaler.Scale(src, new DisplaySignature("A", 3440, 1440, 100), new DisplaySignature("B", 1720, 720, 100));
        Assert.Equal("clock-1", s.Components[0].Widget);
        Assert.Equal("clock", s.Widgets!["clock-1"].Template);
    }

    [Fact]
    public void Dial_Thickness_Scales_With_The_Smaller_Factor()
    {
        // A dial's radius comes from the short side of its rect (min(w, h) / 2 - thickness / 2), so
        // the stroke tracks the smaller of the two factors, not the geometric mean a text size uses:
        // at sx 0.5, sy 2 the mean is 1 and a 10 px stroke would swallow a rect that halved in width.
        var layout = LayoutFile.Parse("""
        { "version": 1, "baseImage": "x.jpg", "sources": [],
          "components": [ { "type": "dial", "id": "d", "rect": [0, 0, 100, 100], "fraction": 0.5, "thickness": 10 } ] }
        """);
        var scaled = LayoutScaler.Scale(layout, new DisplaySignature("A", 1000, 1000, 100), new DisplaySignature("B", 500, 2000, 100));
        var d = Assert.IsType<DialDef>(Assert.Single(scaled.Components));
        Assert.Equal("5", d.Thickness.LiteralText);
    }

    /// <summary>Harden review finding 7. A shape bar's and a line's path is inset from its rect by
    /// thickness / 2 + glow on both axes, so the stroke has to shrink with the smaller factor (as a
    /// dial's does) to still fit the rect it was authored to fit. Bars were not scaled at all, and
    /// lines rounded to whole pixels, so a 1 px line at 0.4 became 0 (then the 0.1 px floor).</summary>
    [Fact]
    public void Shape_And_Line_Strokes_Scale_With_The_Smaller_Factor_And_Keep_Fractions()
    {
        var layout = LayoutFile.Parse("""
        { "version": 1, "baseImage": "x.jpg", "sources": [],
          "components": [
            { "type": "bar", "id": "b", "rect": [0, 0, 100, 36], "fraction": 1, "shape": "M0,0 L1000,0", "thickness": 8, "glow": 14 },
            { "type": "line", "id": "l", "rect": [0, 0, 100, 100], "values": { "bind": "h.v" }, "thickness": 1, "glow": 3 } ] }
        """);
        var scaled = LayoutScaler.Scale(layout, new DisplaySignature("A", 1000, 1000, 100), new DisplaySignature("B", 400, 800, 100));
        var bar = Assert.IsType<BarDef>(scaled.Components[0]);
        Assert.Equal("3.2", bar.Thickness.LiteralText);
        Assert.Equal("5.6", bar.Glow.LiteralText);
        var line = Assert.IsType<LineDef>(scaled.Components[1]);
        Assert.Equal("0.4", line.Thickness.LiteralText);
        Assert.Equal("1.2", line.Glow.LiteralText);
    }

    /// <summary>The same finding, as drawn: alpine-vision's disk bar and peer light are authored
    /// with 2 * (thickness / 2 + glow) equal to the rect's short side, so the halo just meets the
    /// rect's edges and the flat axis lands on the centre. Scaled to a 1920x1200 ratio the rect
    /// shrank but the stroke did not: the fill clip cut the halo off hard at the rect's edge, the
    /// bar sat 3 px below centre, and the light's interior went negative, mirroring the dot into
    /// a pill.</summary>
    [Fact]
    public void A_Scaled_Shape_Bar_Keeps_Its_Halo_Inside_The_Rect_And_Centred()
    {
        var layout = LayoutFile.Parse("""
        { "version": 1, "baseImage": "x.jpg", "sources": [],
          "components": [
            { "type": "bar", "id": "disk", "rect": [20, 20, 100, 36], "fraction": 1, "threshold": 2, "track": "#00000000",
              "fill": "#FF00FF00", "shape": "M0,0 L1000,0", "thickness": 8, "glow": 14, "glowColor": "#FF00FF00", "glowStrength": 0.35 },
            { "type": "bar", "id": "light", "rect": [200, 20, 26, 26], "fraction": 1, "threshold": 2, "track": "#00000000",
              "fill": "#FF00FF00", "shape": "M0,0 h0.01", "thickness": 14, "glow": 6, "glowColor": "#FF00FF00", "glowStrength": 0.35 } ] }
        """);
        var scaled = LayoutScaler.Scale(layout, new DisplaySignature("A", 344, 144, 100), new DisplaySignature("B", 192, 120, 100));
        var resolved = DeskWall.Core.Resolve.LayoutResolver.Resolve(scaled, DeskWall.Core.Values.ValueTree.Of());
        var png = Path.Combine(TestRun.Root, "scaled-shape-base.png");
        Directory.CreateDirectory(Path.GetDirectoryName(png)!);
        using (var b = DeskWall.Core.Render.Surface.Create(192, 120)) { b.Clear(new DeskWall.Core.Render.Color(255, 0, 0, 255)); b.SavePng(png); }
        using var frame = new DeskWall.Core.Render.FrameRenderer(192, 120).RenderAll(DeskWall.Core.Render.BaseCache.Ensure(png, 192, 120, Fit.Cover), resolved);

        var rects = scaled.Components.Select(c => c.Rect).ToArray();
        for (var y = 0; y < 120; y++)
            for (var x = 0; x < 192; x++)
            {
                // One pixel of slack for the antialiased edge of a halo that exactly fits.
                if (rects.Any(r => x >= r.X - 1 && x < r.X + r.W + 1 && y >= r.Y - 1 && y < r.Y + r.H + 1)) continue;
                Assert.True(frame.GetPixel(x, y) == (255, 0, 0, 255), $"painted outside every rect at ({x},{y}): {frame.GetPixel(x, y)}");
            }
        foreach (var r in rects)
        {
            var cx = r.X + r.W / 2;
            Assert.True(frame.GetPixel(cx, r.Y + r.H / 2).G > 200, $"not lit at the centre of {r}");
            Assert.True(frame.GetPixel(cx, r.Y) == (255, 0, 0, 255), $"halo reaches the top edge of {r}: {frame.GetPixel(cx, r.Y)}");
            Assert.True(frame.GetPixel(cx, r.Y + r.H - 1) == (255, 0, 0, 255), $"halo reaches the bottom edge of {r}: {frame.GetPixel(cx, r.Y + r.H - 1)}");
        }
    }

    /// <summary>The designer's zoom: every rect and pixel size times the zoom, top-level rects then
    /// offset into the viewport; repeater children scale but stay cell-relative; "auto" stays.</summary>
    [Fact]
    public void Transform_Scales_About_The_Origin_Then_Offsets()
    {
        var src = LayoutFile.Parse(Json);
        var t = LayoutScaler.Transform(src, 8, -25000, -100);
        var clock = (TextDef)t.Components[0];
        Assert.Equal(new Rect(3220 * 8 - 25000, 40 * 8 - 100, 172 * 8, 78 * 8), clock.Rect);
        Assert.Equal("512", clock.Size.LiteralText);
        Assert.Equal("auto", clock.EffectRadius.LiteralText);
        var rep = (RepeaterDef)t.Components[1];
        Assert.Equal(112, rep.Gap);
        Assert.Equal("368", rep.CellHeight.LiteralText);
        Assert.Equal(new Rect(0, 208, 1376, 48), rep.Template[0].Rect);
        Assert.Equal(new Rect(3220, 40, 172, 78), ((TextDef)src.Components[0]).Rect);   // source untouched
    }
}
