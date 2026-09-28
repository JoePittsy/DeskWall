using DeskWall.Core;
using DeskWall.Core.Layout;
using DeskWall.Core.Render;
using DeskWall.Core.Resolve;
using DeskWall.Core.Values;
using Xunit;

namespace DeskWall.Core.Tests.Render;

/// <summary>
/// Regression cover for the bug where raising a text component's font size made the text vanish
/// instead of overflowing. <c>Surface.DrawText</c> clipped to the authored rect plus a constant
/// effect margin, so glyphs larger than the rect were trimmed; with <see cref="Align.Right"/> the
/// trailing-aligned run starts <em>left</em> of <c>rect.X</c> once it is wider than its box, so the
/// clip removed nearly all of it. Everything here is pinned to a measurement
/// (<see cref="TextMeasure"/>) rather than to a constant.
/// </summary>
public class TextMeasureTests
{
    private const int W = 300, H = 200;
    private static readonly Color Background = new(255, 0, 0, 0);
    private static readonly TextStyle Big = TextStyle.Default with { Size = 72, Effect = TextEffect.Shadow, EffectRadius = 6 };

    private static Surface Painted(string text, TextStyle style, Rect rect)
    {
        var s = Surface.Create(W, H);
        s.Clear(Background);
        s.DrawText(text, style, rect);
        return s;
    }

    /// <summary>Pixels that are not the background, i.e. something was painted there.</summary>
    private static List<(int X, int Y)> Ink(Surface s)
    {
        var ink = new List<(int, int)>();
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
                if (s.GetPixel(x, y) != (Background.A, Background.R, Background.G, Background.B)) ink.Add((x, y));
        return ink;
    }

    private static void AssertIdentical(Surface a, Surface b, string what)
    {
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
                if (a.GetPixel(x, y) != b.GetPixel(x, y))
                    Assert.Fail($"{what}: differs at ({x},{y}): {a.GetPixel(x, y)} vs {b.GetPixel(x, y)}");
    }

    /// <summary>
    /// A font size far larger than the rect paints every glyph. The reference is the same string,
    /// same style, same origin, in a rect wide enough to hold it: with leading alignment and
    /// wrapping off the layout box's width does not move a single glyph, so the two frames must be
    /// pixel-identical. Before the fix the narrow one was cut off at <c>rect.Right + margin</c>.
    /// </summary>
    [Fact]
    public void Text_Larger_Than_Its_Rect_Is_Not_Clipped()
    {
        var narrow = new Rect(30, 30, 24, 24);
        var wide = new Rect(30, 30, 260, 24);

        using var small = Painted("888", Big, narrow);
        using var reference = Painted("888", Big, wide);
        AssertIdentical(small, reference, "oversized text in a small rect");

        var ink = Ink(small);
        Assert.NotEmpty(ink);
        // It really did overflow: there is paint well past the right and bottom edges of the rect.
        Assert.Contains(ink, p => p.X > narrow.Right + Big.PaintMargin());
        Assert.Contains(ink, p => p.Y > narrow.Bottom + Big.PaintMargin());
        // And every painted pixel is inside what PaintBounds promised, which is what the incremental
        // renderer will restore the base over next tick.
        var bounds = TextMeasure.PaintBounds("888", Big, narrow);
        Assert.All(ink, p => Assert.True(p.X >= bounds.X && p.X < bounds.Right && p.Y >= bounds.Y && p.Y < bounds.Bottom,
            $"painted ({p.X},{p.Y}) outside PaintBounds {bounds}"));
    }

    /// <summary>
    /// The same for right-aligned text, which is what the whole right-hand column uses and where
    /// the bug was worst: DirectWrite aligns the run's right edge to the layout box's right edge,
    /// so a run wider than the box starts left of <c>rect.X</c> and the old clip removed all of it.
    /// The reference rect has the same right edge and the same top, so the glyphs land in exactly
    /// the same place.
    /// </summary>
    [Fact]
    public void Right_Aligned_Text_Wider_Than_Its_Rect_Overflows_Left_Instead_Of_Vanishing()
    {
        var style = Big with { Align = Align.Right };
        var narrow = new Rect(230, 30, 24, 24);   // Right = 254
        var wide = new Rect(30, 30, 224, 24);     // Right = 254 as well

        using var small = Painted("888", style, narrow);
        using var reference = Painted("888", style, wide);
        AssertIdentical(small, reference, "oversized right-aligned text in a small rect");

        var ink = Ink(small);
        Assert.NotEmpty(ink);
        // The signature of the bug: most of the run lives to the left of the rect it was authored in.
        Assert.Contains(ink, p => p.X < narrow.X - Big.PaintMargin());
        Assert.True(ink.Count(p => p.X < narrow.X) > ink.Count / 2,
            "most of a right-aligned run wider than its rect should sit left of rect.X");

        var bounds = TextMeasure.PaintBounds("888", style, narrow);
        Assert.True(bounds.X < narrow.X, $"PaintBounds {bounds} should start left of the rect at {narrow.X}");
        Assert.All(ink, p => Assert.True(p.X >= bounds.X && p.X < bounds.Right && p.Y >= bounds.Y && p.Y < bounds.Bottom,
            $"painted ({p.X},{p.Y}) outside PaintBounds {bounds}"));
    }

    /// <summary>
    /// Text that shrinks between two ticks must leave nothing of the longer string behind. The
    /// dirty set has to be the union of the previous and the current paint bounds; with measured
    /// bounds the current one is genuinely smaller, so dirtying only it would restore the base over
    /// part of what was painted and the rest would stay on the wallpaper for good. Right-aligned,
    /// because that is where the residue lands outside the rect entirely.
    /// </summary>
    [Fact]
    public void Text_That_Shrinks_Between_Ticks_Leaves_No_Residue()
    {
        var dir = Path.Combine(Path.GetTempPath(), "deskwall-tests");
        Directory.CreateDirectory(dir);
        var png = Path.Combine(dir, "base-textshrink.png");
        using (var b = Surface.Create(W, H)) { b.Clear(new Color(255, 0, 0, 255)); b.SavePng(png); }
        var raw = BaseCache.Ensure(png, W, H, Fit.Cover);

        var style = Big with { Align = Align.Right };
        var rect = new Rect(230, 30, 24, 24);
        Resolved before = new ResolvedText("t", rect, 0, "88888", style);
        Resolved after = new ResolvedText("t", rect, 0, "8", style);

        // The premise: measured bounds shrink with the content, where Rect + a constant margin could not.
        Assert.True(after.PaintBounds.W < before.PaintBounds.W,
            $"expected the shorter string to measure narrower: {after.PaintBounds} vs {before.PaintBounds}");

        var r = new FrameRenderer(W, H);
        using var incremental = r.RenderIncremental(r.RenderAll(raw, [before]), raw, [after],
            new HashSet<string> { "t" }, new Dictionary<string, Rect> { ["t"] = before.PaintBounds }, out _);
        using var full = r.RenderAll(raw, [after]);
        AssertIdentical(incremental, full, "incremental frame after the text shrank");
    }

    /// <summary>The measurement itself, independent of any drawing.</summary>
    [Fact]
    public void PaintBounds_Is_The_Measured_Ink_Plus_The_Effect_Margin()
    {
        var rect = new Rect(3220, 40, 172, 78);
        var shadow = TextStyle.Default with { Effect = TextEffect.Shadow, EffectRadius = 6 };
        var ink = TextMeasure.Ink("14:32", shadow, rect);
        var bounds = TextMeasure.PaintBounds("14:32", shadow, rect);
        Assert.Equal(new Rect(ink.X - 8, ink.Y - 8, ink.W + 16, ink.H + 16), bounds);

        // A 16 px clock does not fill a 172x78 box, and the measured bounds say so - where the old
        // Rect + margin claimed the whole 188x94.
        Assert.True(ink.W < rect.W && ink.H < rect.H, $"a 16 px string should measure smaller than {rect}, got {ink}");

        // Effect None paints nothing outside the glyphs, so its bounds are the ink exactly.
        var none = TextStyle.Default with { Effect = TextEffect.None };
        Assert.Equal(TextMeasure.Ink("14:32", none, rect), TextMeasure.PaintBounds("14:32", none, rect));

        // Empty text paints nothing; DrawText returns before touching the surface.
        Assert.Equal(new Rect(rect.X, rect.Y, 0, 0), TextMeasure.PaintBounds("", shadow, rect));
    }

    /// <summary>A repeater clamps a template child that does not fit its cell, and can clamp it to
    /// nothing. Measuring a zero-sized box must still work and still report where the glyphs would
    /// go, because the renderer draws them and the next tick has to clean them up.</summary>
    [Fact]
    public void A_Zero_Sized_Rect_Still_Measures()
    {
        var style = TextStyle.Default with { Size = 20, Effect = TextEffect.None };
        var ink = TextMeasure.Ink("clamped", style, new Rect(40, 50, 0, 0));
        Assert.True(ink.W > 0 && ink.H > 0, $"expected real glyph extent from a zero-sized box, got {ink}");
        Assert.Equal(40, ink.X);
        Assert.Equal(50, ink.Y);
    }

    /// <summary>
    /// The default blur radius follows the font size instead of being a flat 6 px. The 64 px clock
    /// keeps exactly the shadow it was tuned with; the 13 px labels beside it, which the flat value
    /// turned into a dark crust because the blur was about half their cap height, get a 1 px ring.
    /// The margin the measurement reserves - and therefore the area the incremental renderer
    /// repaints - follows it down.
    /// </summary>
    [Theory]
    [InlineData(64f, 6f, 8)]     // the owner's clock: unchanged
    [InlineData(40f, 4f, 6)]     // the weather temperature
    [InlineData(15f, 2f, 4)]     // the drive labels
    [InlineData(13f, 1f, 3)]     // days-since-crash, VPN, pending-reboot
    [InlineData(4f, 1f, 3)]      // floored at 1: the shadow path clamps to 1 internally anyway
    public void Default_Effect_Radius_And_Paint_Margin_Follow_The_Font_Size(float size, float radius, int margin)
    {
        Assert.Equal(radius, TextStyle.DefaultRadius(size));
        var style = TextStyle.Default with { Size = size, Effect = TextEffect.Shadow };
        Assert.Null(style.EffectRadius);
        Assert.Equal(radius, style.Radius);
        Assert.Equal(margin, style.PaintMargin());
    }

    /// <summary>An explicit radius still wins, at any size: the proportional value is only what an
    /// unset one falls back to.</summary>
    [Fact]
    public void An_Explicit_Effect_Radius_Overrides_The_Proportional_Default()
    {
        var pinned = TextStyle.Default with { Size = 13, Effect = TextEffect.Shadow, EffectRadius = 6 };
        Assert.Equal(6f, pinned.Radius);
        Assert.Equal(8, pinned.PaintMargin());
        // Including zero, which the old sentinel-free default could not express.
        Assert.Equal(0f, (pinned with { EffectRadius = 0 }).Radius);
        Assert.Equal(2, (pinned with { EffectRadius = 0 }).PaintMargin());
    }

    /// <summary>
    /// The layout-file default, end to end. A text component that omits "effectRadius" gets the
    /// proportional radius; one that sets it keeps what it set. The model's own default is the
    /// literal "auto" (the same sentinel a repeater's "cellHeight" uses), which the resolver reads
    /// as "not a number" and so leaves for TextStyle to derive.
    /// </summary>
    [Fact]
    public void A_Layout_That_Omits_EffectRadius_Gets_The_Proportional_One()
    {
        var layout = LayoutFile.Parse("""
        { "version": 1, "baseImage": "x.jpg", "sources": [], "components": [
            { "type": "text", "id": "small", "rect": [0, 0, 100, 20], "text": "hi", "size": 13 },
            { "type": "text", "id": "big",   "rect": [0, 40, 200, 80], "text": "hi", "size": 64 },
            { "type": "text", "id": "pinned","rect": [0, 130, 100, 20], "text": "hi", "size": 13, "effectRadius": 6 } ] }
        """);
        var r = LayoutResolver.Resolve(layout, new RecordValue(new Dictionary<string, Value>()));
        Assert.Equal(1f, ((ResolvedText)r.Single(c => c.Id == "small")).Style.Radius);
        Assert.Equal(6f, ((ResolvedText)r.Single(c => c.Id == "big")).Style.Radius);
        Assert.Equal(6f, ((ResolvedText)r.Single(c => c.Id == "pinned")).Style.Radius);
    }

    /// <summary>Deterministic given text, style and box, which is what lets the content key stay on
    /// Rect: a measurement cannot move without one of the key's own inputs moving.</summary>
    [Fact]
    public void Measurement_Is_Stable_Across_Calls_And_Tracks_Its_Inputs()
    {
        var rect = new Rect(100, 20, 60, 30);
        var style = TextStyle.Default with { Size = 24 };
        Assert.Equal(TextMeasure.Ink("9%", style, rect), TextMeasure.Ink("9%", style, rect));
        Assert.NotEqual(TextMeasure.Ink("9%", style, rect), TextMeasure.Ink("100%", style, rect));
        Assert.NotEqual(TextMeasure.Ink("9%", style, rect), TextMeasure.Ink("9%", style with { Size = 48 }, rect));
        // Colour cannot move a glyph, and the cache key leaves it out on purpose.
        Assert.Equal(TextMeasure.Ink("9%", style, rect), TextMeasure.Ink("9%", style with { Color = new Color(255, 1, 2, 3) }, rect));
    }
}
