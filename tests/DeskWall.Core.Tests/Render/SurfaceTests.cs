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

    /// <summary>Finding 10: a failing SaveRaw used to leave &lt;path&gt;.tmp behind forever.</summary>
    [Fact]
    public void SaveRaw_Failure_Leaves_No_Tmp_File()
    {
        var dir = Path.Combine(TempDir(), "saveraw-fail-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "frame.raw");
        Directory.CreateDirectory(path);   // destination is a directory: File.Move fails after the tmp write

        using var s = Surface.Create(4, 4);
        s.Clear(new Color(255, 1, 2, 3));
        Assert.ThrowsAny<Exception>(() => s.SaveRaw(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    /// <summary>Same failure shape through Encode (SaveJpeg/SavePng share it).</summary>
    [Fact]
    public void SaveJpeg_Failure_Leaves_No_Tmp_File()
    {
        var dir = Path.Combine(TempDir(), "savejpeg-fail-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "out.jpg");
        Directory.CreateDirectory(path);   // destination is a directory: File.Move fails after the tmp write

        using var s = Surface.Create(4, 4);
        s.Clear(new Color(255, 1, 2, 3));
        Assert.ThrowsAny<Exception>(() => s.SaveJpeg(path, 92));
        Assert.False(File.Exists(path + ".tmp"));
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

    /// <summary>
    /// Finding 8: the process-wide factory init used a non-volatile double-checked pointer read.
    /// This cannot prove memory-model correctness (that needs a weak-ordering CPU), but it is a
    /// smoke test that concurrent first-use from several threads never observes a partially
    /// published factory set (which would surface as a NullReferenceException or an access
    /// violation inside Rt()/EnsureFactories, not a normal managed exception).
    /// </summary>
    [Fact]
    public void Concurrent_First_Use_Does_Not_Race_Factory_Init()
    {
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        Parallel.For(0, 16, _ =>
        {
            try
            {
                using var s = Surface.Create(4, 4);
                s.Clear(new Color(255, 1, 2, 3));
                Assert.Equal(((byte)255, (byte)1, (byte)2, (byte)3), s.GetPixel(0, 0));
            }
            catch (Exception ex) { exceptions.Add(ex); }
        });
        Assert.Empty(exceptions);
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
    /// Finding 1. Text and its shadow used to paint outside the component rect while the
    /// incremental path restored the base only inside that rect, so every tick left a little more
    /// of the previous string on the wallpaper. Renders a wide string in a narrow rect, replaces it
    /// with a narrow one, and requires the incremental frame to be pixel-identical to a full render
    /// of the new state everywhere on the surface.
    /// </summary>
    [Fact]
    public void RenderIncremental_Text_Leaves_No_Residue_Outside_The_Rect()
    {
        var dir = Path.Combine(Path.GetTempPath(), "deskwall-tests");
        Directory.CreateDirectory(dir);
        var png = Path.Combine(dir, "base-textresidue.png");
        using (var b = Surface.Create(160, 60)) { b.Clear(new Color(255, 0, 0, 255)); b.SavePng(png); }
        var raw = BaseCache.Ensure(png, 160, 60, Fit.Cover);

        var style = TextStyle.Default with { Size = 32, Effect = TextEffect.Shadow, EffectRadius = 6 };
        var rect = new Rect(10, 10, 30, 32);
        Resolved before = new ResolvedText("t", rect, 0, "88888", style);   // far wider than its rect
        Resolved after = new ResolvedText("t", rect, 0, "1", style);

        var r = new FrameRenderer(160, 60);
        using var incremental = r.RenderIncremental(r.RenderAll(raw, [before]), raw, [after],
            new HashSet<string> { "t" }, new Dictionary<string, Rect> { ["t"] = before.PaintBounds });
        using var full = r.RenderAll(raw, [after]);

        for (var y = 0; y < 60; y++)
            for (var x = 0; x < 160; x++)
                if (incremental.GetPixel(x, y) != full.GetPixel(x, y))
                    Assert.Fail($"incremental frame differs from a full render at ({x},{y}): {incremental.GetPixel(x, y)} vs {full.GetPixel(x, y)}");
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

public class FrameRendererLeakTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "deskwall-tests");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Finding 7: a throw partway through RenderAll's draw loop (a corrupt image file, here) used
    /// to leak the base frame surface it had just loaded, reclaimed only by the GC finalizer.
    /// </summary>
    [Fact]
    public void RenderAll_Disposes_The_Frame_When_A_Component_Throws_Mid_Draw()
    {
        var dir = TempDir();
        var basePng = Path.Combine(dir, "leak-base-" + Guid.NewGuid().ToString("N")[..8] + ".png");
        using (var b = Surface.Create(20, 20)) { b.Clear(new Color(255, 0, 0, 255)); b.SavePng(basePng); }
        var raw = BaseCache.Ensure(basePng, 20, 20, Fit.Cover);

        // Exists, but is not a decodable image: Surface.Load throws from inside FrameRenderer.Draw.
        var corrupt = Path.Combine(dir, "corrupt-" + Guid.NewGuid().ToString("N")[..8] + ".png");
        File.WriteAllBytes(corrupt, [1, 2, 3, 4, 5]);

        var r = new FrameRenderer(20, 20);
        var comps = new Resolved[] { new ResolvedImage("i", new Rect(0, 0, 20, 20), 0, corrupt, Fit.Cover, 0, 1) };

        var before = Surface.LiveCount;
        Assert.ThrowsAny<Exception>(() => r.RenderAll(raw, comps));
        Assert.Equal(before, Surface.LiveCount);   // the frame RenderAll loaded must have been disposed, not leaked
    }
}

public class PaintBoundsTests
{
    [Fact]
    public void PaintBounds_Is_Rect_Plus_The_Shared_Margin()
    {
        var rect = new Rect(3220, 40, 172, 78);
        Resolved plain = new ResolvedBar("b", rect, 0, 0.5, Color.White, Color.White, Axis.Horizontal);
        Assert.Equal(rect, plain.PaintBounds);

        var shadow = TextStyle.Default with { Effect = TextEffect.Shadow, EffectRadius = 6 };
        Assert.Equal(8, shadow.PaintMargin());
        Assert.Equal(new Rect(3212, 32, 188, 94), new ResolvedText("t", rect, 0, "14:32", shadow).PaintBounds);

        Assert.Equal(0, (TextStyle.Default with { Effect = TextEffect.None }).PaintMargin());
        Assert.Equal(rect, new ResolvedText("t", rect, 0, "14:32", TextStyle.Default with { Effect = TextEffect.None }).PaintBounds);
        Assert.Equal(14, (TextStyle.Default with { Effect = TextEffect.Plate, EffectRadius = 6 }).PaintMargin());
    }
}
