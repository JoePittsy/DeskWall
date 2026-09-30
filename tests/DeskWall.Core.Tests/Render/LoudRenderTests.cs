using DeskWall.Core;
using DeskWall.Core.Layout;
using DeskWall.Core.Render;
using DeskWall.Core.Resolve;
using DeskWall.Core.Values;
using Xunit;

/// <summary>The properties the loud alpine layout added: image tint, bar opacity, glow strength,
/// a line's own area fill, and per-layer draw timing.</summary>
public class LoudRenderTests
{
    private static string Dir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "deskwall-tests");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string BlueBase(string name)
    {
        var png = Path.Combine(Dir(), name);
        using (var b = Surface.Create(40, 20)) { b.Clear(new Color(255, 0, 0, 255)); b.SavePng(png); }
        return BaseCache.Ensure(png, 40, 20, Fit.Cover);
    }

    private static string WhitePng(string name)
    {
        var png = Path.Combine(Dir(), name);
        using (var s = Surface.Create(10, 10)) { s.Clear(Color.White); s.SavePng(png); }
        return png;
    }

    [Fact]
    public void Tint_Multiplies_A_White_Image_Into_The_Tint_Colour()
    {
        var raw = BlueBase("loud-tint-base.png");
        var img = new ResolvedImage("i", new Rect(0, 0, 20, 20), 1, WhitePng("loud-white.png"), Fit.Stretch, 0, 1, new Color(255, 255, 0, 0));
        using var frame = new FrameRenderer(40, 20).RenderAll(raw, [img]);
        Assert.Equal(((byte)255, (byte)255, (byte)0, (byte)0), frame.GetPixel(10, 10));
        Assert.Equal(((byte)255, (byte)0, (byte)0, (byte)255), frame.GetPixel(30, 10));   // outside the image: base
    }

    [Fact]
    public void A_Fully_Transparent_Tint_Draws_Nothing()
    {
        var raw = BlueBase("loud-tint0-base.png");
        var img = new ResolvedImage("i", new Rect(0, 0, 40, 20), 1, WhitePng("loud-white0.png"), Fit.Stretch, 0, 1, new Color(0, 255, 0, 0));
        var ms = new Dictionary<string, double>();
        using var frame = new FrameRenderer(40, 20) { LayerMs = ms }.RenderAll(raw, [img]);
        Assert.Equal(((byte)255, (byte)0, (byte)0, (byte)255), frame.GetPixel(10, 10));
    }

    [Fact]
    public void Tint_Is_Part_Of_The_Content_Key()
    {
        var a = new ResolvedImage("i", new Rect(0, 0, 10, 10), 0, "x.png", Fit.Cover, 0, 1, Color.White);
        var b = a with { Tint = new Color(255, 255, 0, 0) };
        Assert.NotEqual(string.Join("|", a.KeyParts()), string.Join("|", b.KeyParts()));
    }

    [Fact]
    public void Bar_Opacity_Scales_Track_Fill_And_Glow()
    {
        var layout = LayoutFile.Parse("""
            { "version": 1, "baseImage": "x.jpg", "sources": [],
              "components": [ { "type": "bar", "id": "b", "rect": [0, 0, 50, 10], "fraction": 0.5, "track": "#C8FFFFFF",
                                "fill": "#FFFF0000", "glowColor": "#8000FF00", "shape": "M0,0 L1,0", "thickness": 2, "glow": 2,
                                "glowStrength": 0.4, "opacity": 0.5 } ] }
            """);
        var b = Assert.IsType<ResolvedBar>(Assert.Single(LayoutResolver.Resolve(layout, ValueTree.Empty)));
        Assert.Equal(100, b.Track.A);
        Assert.Equal(127, b.Fill.A);
        Assert.Equal(64, b.GlowColor!.Value.A);
        Assert.Equal(0.4f, b.GlowStrength);
    }

    [Fact]
    public void A_Bound_Bar_Opacity_That_Does_Not_Resolve_Hides_The_Bar()
    {
        var layout = LayoutFile.Parse("""
            { "version": 1, "baseImage": "x.jpg", "sources": [],
              "components": [ { "type": "bar", "id": "b", "rect": [0, 0, 50, 10], "fraction": 1, "fill": "#FFFF0000",
                                "opacity": { "bind": "weather.nope" } } ] }
            """);
        var b = Assert.IsType<ResolvedBar>(Assert.Single(LayoutResolver.Resolve(layout, ValueTree.Empty)));
        Assert.Equal(0, b.Fill.A);
        Assert.Equal(0, b.Track.A);
    }

    [Fact]
    public void Line_Area_Uses_Its_Own_Fill_When_Set()
    {
        var raw = BlueBase("loud-area-base.png");
        var layout = LayoutFile.Parse("""
            { "version": 1, "baseImage": "x.jpg", "sources": [],
              "components": [ { "type": "line", "id": "l", "rect": [0, 0, 40, 20], "values": { "bind": "h.v" }, "baseline": "true",
                                "stroke": "#FFFFFFFF", "thickness": 1, "areaFill": "#FFFF0000" } ] }
            """);
        RecordValue S(double v) => new(new Dictionary<string, Value> { ["v"] = new NumberValue(v) });
        var tree = ValueTree.Of(("h", new RecordValue(new Dictionary<string, Value> { ["v"] = new ListValue([S(0.5), S(0.5)], null) })));
        var line = Assert.IsType<ResolvedLine>(Assert.Single(LayoutResolver.Resolve(layout, tree)));
        Assert.Equal(Color.Parse("#FFFF0000"), line.AreaFill);
        using var frame = new FrameRenderer(40, 20).RenderAll(raw, [line]);
        Assert.Equal(((byte)255, (byte)255, (byte)0, (byte)0), frame.GetPixel(20, 17));   // under the line: the area fill, opaque
    }

    [Fact]
    public void LayerMs_Records_Every_Drawn_Component()
    {
        var raw = BlueBase("loud-layers-base.png");
        var ms = new Dictionary<string, double>();
        var r = new FrameRenderer(40, 20) { LayerMs = ms };
        using var frame = r.RenderAll(raw, [
            new ResolvedBar("a", new Rect(0, 0, 20, 20), 1, 1, Color.White, Color.White, Axis.Horizontal),
            new ResolvedBar("b", new Rect(20, 0, 20, 20), 2, 1, Color.White, Color.White, Axis.Horizontal),
        ]);
        Assert.Equal(["a", "b"], ms.Keys.Order());
        Assert.All(ms.Values, v => Assert.True(v >= 0));
    }
}
