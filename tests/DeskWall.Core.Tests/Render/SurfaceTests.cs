using DeskWall.Core;
using DeskWall.Core.Render;
using DeskWall.Core.Resolve;
using Xunit;

public class SurfaceTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "deskwall-tests");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Create_Clear_FillRect_GetPixel()
    {
        using var s = Surface.Create(64, 32);
        s.Clear(new Color(255, 10, 20, 30));
        s.FillRect(new Rect(8, 8, 16, 8), new Color(255, 200, 100, 50));
        Assert.Equal(((byte)255, (byte)10, (byte)20, (byte)30), s.GetPixel(0, 0));
        Assert.Equal(((byte)255, (byte)200, (byte)100, (byte)50), s.GetPixel(10, 10));
    }

    [Fact]
    public void Jpeg_And_Raw_RoundTrip()
    {
        var dir = TempDir();
        using var s = Surface.Create(100, 50);
        s.Clear(new Color(255, 0, 128, 255));
        var jpg = Path.Combine(dir, "t.jpg");
        s.SaveJpeg(jpg, 92);
        Assert.True(new FileInfo(jpg).Length > 500);
        using var back = Surface.Load(jpg);
        Assert.Equal((100, 50), (back.Width, back.Height));
        var p = back.GetPixel(50, 25);
        Assert.InRange(p.G, 120, 136);
        var raw = Path.Combine(dir, "t.raw");
        s.SaveRaw(raw);
        using var raw2 = Surface.LoadRaw(raw);
        Assert.Equal(s.GetPixel(3, 3), raw2.GetPixel(3, 3));
    }

    [Fact]
    public void DrawText_Marks_Pixels()
    {
        using var s = Surface.Create(200, 80);
        s.Clear(new Color(255, 0, 0, 0));
        s.DrawText("14:32", TextStyle.Default with { Size = 48, Effect = TextEffect.None }, new Rect(0, 0, 200, 80));
        var lit = 0;
        for (var y = 0; y < 80; y += 2) for (var x = 0; x < 200; x += 2) if (s.GetPixel(x, y).R > 128) lit++;
        Assert.InRange(lit, 100, 3000);
    }

    [Fact]
    public void Effects_And_Rounded_Image_Draw_Without_Throwing()
    {
        using var s = Surface.Create(240, 200);
        s.Clear(new Color(255, 40, 40, 40));
        using var img = Surface.Create(30, 45);
        img.Clear(new Color(255, 0, 200, 0));
        s.DrawSurface(img, new Rect(10, 10, 60, 90), Fit.Cover, opacity: 0.9f, radius: 12);
        s.DrawSurface(img, new Rect(80, 10, 60, 30), Fit.Contain);
        s.DrawSurface(img, new Rect(150, 10, 60, 30), Fit.Stretch);
        s.DrawText("Shadow", TextStyle.Default with { Size = 24 }, new Rect(10, 110, 220, 30));
        s.DrawText("Outline", TextStyle.Default with { Size = 24, Effect = TextEffect.Outline }, new Rect(10, 140, 220, 30));
        s.DrawText("Plate", TextStyle.Default with { Size = 24, Effect = TextEffect.Plate, EffectColor = new Color(200, 0, 0, 0) }, new Rect(10, 170, 220, 30));
        Assert.InRange(s.GetPixel(40, 55).G, 170, 200);                                         // inside the rounded image: green at 0.9 over grey
        Assert.Equal(((byte)255, (byte)40, (byte)40, (byte)40), s.GetPixel(10, 10));        // corner clipped by the radius
        Assert.NotEqual(((byte)255, (byte)40, (byte)40, (byte)40), s.GetPixel(11, 185));    // plate painted behind the text
    }

    [Fact]
    public void CopyRect_Replaces_Only_That_Rect()
    {
        using var a = Surface.Create(20, 20);
        using var b = Surface.Create(20, 20);
        a.Clear(new Color(255, 255, 0, 0));
        b.Clear(new Color(255, 0, 0, 255));
        a.CopyRect(b, new Rect(10, 0, 10, 20));
        Assert.Equal(((byte)255, (byte)255, (byte)0, (byte)0), a.GetPixel(5, 5));
        Assert.Equal(((byte)255, (byte)0, (byte)0, (byte)255), a.GetPixel(15, 5));
    }
}

public class FrameRendererTests
{
    [Fact]
    public void RenderAll_Draws_Base_Then_Components_By_Z()
    {
        var dir = Path.Combine(Path.GetTempPath(), "deskwall-tests");
        Directory.CreateDirectory(dir);
        var basePng = Path.Combine(dir, "base.png");
        using (var b = Surface.Create(20, 20)) { b.Clear(new Color(255, 0, 0, 255)); b.SavePng(basePng); }
        var raw = BaseCache.Ensure(basePng, 40, 20, Fit.Cover);
        var r = new FrameRenderer(40, 20);
        var comps = new Resolved[]
        {
            new ResolvedBar("b", new Rect(0, 0, 40, 20), 2, 0.5, new Color(255, 0, 0, 0), new Color(255, 255, 0, 0), Axis.Horizontal),
            new ResolvedBar("under", new Rect(0, 0, 40, 20), 1, 1.0, new Color(255, 0, 255, 0), new Color(255, 0, 255, 0), Axis.Horizontal),
        };
        using var frame = r.RenderAll(raw, comps);
        Assert.Equal(((byte)255, (byte)255, (byte)0, (byte)0), frame.GetPixel(5, 10));   // fill half of top bar
        Assert.Equal(((byte)255, (byte)0, (byte)0, (byte)0), frame.GetPixel(35, 10));    // track of top bar covers the green one
    }

    private static string BlueBase(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "deskwall-tests");
        Directory.CreateDirectory(dir);
        var png = Path.Combine(dir, name);
        using (var b = Surface.Create(40, 20)) { b.Clear(new Color(255, 0, 0, 255)); b.SavePng(png); }
        return BaseCache.Ensure(png, 40, 20, Fit.Cover);
    }

    [Fact]
    public void RenderIncremental_Only_Touches_Dirty_Rects()
    {
        var raw = BlueBase("base2.png");
        var r = new FrameRenderer(40, 20);
        Resolved left = new ResolvedBar("l", new Rect(0, 0, 20, 20), 1, 1, Color.White, new Color(255, 255, 0, 0), Axis.Horizontal);
        Resolved right = new ResolvedBar("r", new Rect(20, 0, 20, 20), 1, 1, Color.White, new Color(255, 0, 255, 0), Axis.Horizontal);
        using var first = r.RenderAll(raw, [left, right]);
        Resolved right2 = new ResolvedBar("r", new Rect(20, 0, 20, 20), 1, 1, Color.White, new Color(255, 0, 0, 200), Axis.Horizontal);
        using var second = r.RenderIncremental(first, raw, [left, right2], new HashSet<string> { "r" }, new Dictionary<string, Rect>());
        Assert.Equal(((byte)255, (byte)255, (byte)0, (byte)0), second.GetPixel(5, 5));    // left untouched
        Assert.Equal(((byte)255, (byte)0, (byte)0, (byte)200), second.GetPixel(30, 5));   // right repainted
    }

    [Fact]
    public void RenderIncremental_Restores_Base_Where_A_Component_Vanished()
    {
        var raw = BlueBase("base3.png");
        var r = new FrameRenderer(40, 20);
        Resolved gone = new ResolvedBar("g", new Rect(0, 0, 40, 20), 1, 1, Color.White, new Color(255, 255, 0, 0), Axis.Horizontal);
        using var first = r.RenderAll(raw, [gone]);
        using var second = r.RenderIncremental(first, raw, [], new HashSet<string>(), new Dictionary<string, Rect> { ["g"] = gone.Rect });
        Assert.Equal(((byte)255, (byte)0, (byte)0, (byte)255), second.GetPixel(5, 5));
    }

    [Fact]
    public void RenderIncremental_Nothing_Dirty_Returns_Previous()
    {
        var raw = BlueBase("base4.png");
        var r = new FrameRenderer(40, 20);
        using var first = r.RenderAll(raw, []);
        var same = r.RenderIncremental(first, raw, [], new HashSet<string>(), new Dictionary<string, Rect>());
        Assert.Same(first, same);
    }

    /// <summary>
    /// Finding 2. A component that moves keeps its identity, so it is in changedIds but its old
    /// rect was never added to the dirty set: the previous pixels stayed on screen until the next
    /// forced tick.
    /// </summary>
    [Fact]
    public void RenderIncremental_Restores_Base_Where_A_Component_Moved_From()
    {
        var raw = BlueBase("base5.png");
        var r = new FrameRenderer(40, 20);
        Resolved atLeft = new ResolvedBar("b", new Rect(0, 0, 10, 20), 1, 1, Color.White, new Color(255, 255, 0, 0), Axis.Horizontal);
        using var first = r.RenderAll(raw, [atLeft]);
        Assert.Equal(((byte)255, (byte)255, (byte)0, (byte)0), first.GetPixel(5, 10));

        Resolved moved = new ResolvedBar("b", new Rect(20, 0, 10, 20), 1, 1, Color.White, new Color(255, 255, 0, 0), Axis.Horizontal);
        using var second = r.RenderIncremental(first, raw, [moved], new HashSet<string> { "b" },
            new Dictionary<string, Rect> { ["b"] = atLeft.PaintBounds });
        Assert.Equal(((byte)255, (byte)0, (byte)0, (byte)255), second.GetPixel(5, 10));    // old location back to the base
        Assert.Equal(((byte)255, (byte)255, (byte)0, (byte)0), second.GetPixel(25, 10));   // new location painted
    }
}
