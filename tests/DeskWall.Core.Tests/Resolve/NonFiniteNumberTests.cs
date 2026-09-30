using DeskWall.Core;
using DeskWall.Core.Layout;
using DeskWall.Core.Render;
using DeskWall.Core.Resolve;
using DeskWall.Core.Values;
using Xunit;

/// <summary>Harden review finding 6. "NaN" and "Infinity" parse as numbers, from a literal, a text
/// value, a blend's output or a source that divides by zero, and Math.Clamp / Math.Max hand NaN
/// straight back: it reached Direct2D as a stroke width, a glow radius and an alpha share.</summary>
public class NonFiniteNumberTests
{
    private static RecordValue Rec(params (string K, Value V)[] kv) => new(kv.ToDictionary(p => p.K, p => p.V));

    private static RecordValue Tree(string bad)
    {
        var history = new ListValue([Rec(("v", new NumberValue(0.2))), Rec(("v", new NumberValue(0.8)))], null);
        return ValueTree.Of(("h", Rec(("v", history), ("bad", new TextValue(bad)), ("nan", new NumberValue(double.NaN)))));
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    public void A_Non_Finite_Number_Is_Unresolved_And_Takes_The_Fallback(string bad)
    {
        var scope = Tree(bad);
        Assert.Null(PropertyReader.Number(PropertyValue.Literal(bad), scope));
        Assert.Null(PropertyReader.Number(PropertyValue.Bound(DeskWall.Core.Bindings.Binding.Parse("h.bad")), scope));
        Assert.Null(PropertyReader.Number(PropertyValue.Bound(DeskWall.Core.Bindings.Binding.Parse("h.nan")), scope));
    }

    [Fact]
    public void A_Line_And_A_Bar_With_NaN_Styling_Resolve_To_Their_Defaults_And_Still_Draw()
    {
        var layout = LayoutFile.Parse("""
            { "version": 1, "baseImage": "x.jpg", "sources": [],
              "components": [
                { "type": "line", "id": "l", "rect": [0, 0, 40, 20], "values": { "bind": "h.v" }, "stroke": "#FFFFFFFF",
                  "thickness": { "bind": "h.bad" }, "glow": { "bind": "h.nan" }, "glowStrength": "NaN" },
                { "type": "bar", "id": "b", "rect": [0, 20, 40, 20], "fraction": 1, "threshold": 2, "fill": "#FF00FF00", "track": "#FF000000",
                  "shape": "M0,0 L10,0 L10,10 L0,10 Z", "thickness": { "bind": "h.bad" }, "glow": "NaN", "glowStrength": { "bind": "h.nan" },
                  "glowColor": "#FFFF0000" } ] }
            """);
        var resolved = LayoutResolver.Resolve(layout, Tree("NaN"));
        var line = Assert.IsType<ResolvedLine>(resolved[0]);
        Assert.Equal(2f, line.Thickness);
        Assert.Equal(0f, line.Glow);
        Assert.Equal(0.12f, line.GlowStrength);
        var bar = Assert.IsType<ResolvedBar>(resolved[1]);
        Assert.Equal(0f, bar.Thickness);
        Assert.Equal(0f, bar.Glow);
        Assert.Equal(0.12f, bar.GlowStrength);

        var png = Path.Combine(Path.GetTempPath(), "deskwall-tests", "nonfinite-base.png");
        Directory.CreateDirectory(Path.GetDirectoryName(png)!);
        using (var b = Surface.Create(40, 40)) { b.Clear(new Color(255, 0, 0, 255)); b.SavePng(png); }
        using var frame = new FrameRenderer(40, 40).RenderAll(BaseCache.Ensure(png, 40, 40, Fit.Cover), resolved);
        Assert.Equal(((byte)255, (byte)0, (byte)255, (byte)0), frame.GetPixel(20, 30));   // the bar's fill
    }
}
