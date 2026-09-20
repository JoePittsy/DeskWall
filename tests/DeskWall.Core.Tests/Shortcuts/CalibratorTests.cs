using DeskWall.Core;
using DeskWall.Core.Render;
using DeskWall.Core.Shortcuts;
using Xunit;

namespace DeskWall.Core.Tests.Shortcuts;

public class CalibratorTests
{
    [Fact]
    public void DiffBounds_Finds_The_Arrow_Square()
    {
        using var reference = Surface.Create(400, 300);
        reference.Clear(new Color(255, 128, 128, 128));
        using var shot = Surface.Create(400, 300);
        shot.Clear(new Color(255, 128, 128, 128));
        shot.FillRect(new Rect(105, 140, 13, 13), new Color(255, 255, 255, 255));   // the arrow overlay
        shot.FillRect(new Rect(300, 20, 1, 1), new Color(255, 160, 128, 128));      // JPEG-noise-sized blip, under threshold
        var b = Calibrator.DiffBounds(shot, reference, new Rect(0, 0, 400, 300));
        Assert.Equal(new Rect(105, 140, 13, 13), b);
    }

    [Fact]
    public void DiffBounds_Null_When_Identical()
    {
        using var a = Surface.Create(20, 20);
        a.Clear(Color.White);
        using var b = Surface.Create(20, 20);
        b.Clear(Color.White);
        Assert.Null(Calibrator.DiffBounds(a, b, new Rect(0, 0, 20, 20)));
    }

    [Fact]
    public void DiffBounds_Ignores_Everything_Outside_The_Probe()
    {
        using var reference = Surface.Create(100, 100);
        reference.Clear(Color.White);
        using var shot = Surface.Create(100, 100);
        shot.Clear(Color.White);
        shot.FillRect(new Rect(5, 5, 4, 4), new Color(255, 0, 0, 0));
        Assert.Null(Calibrator.DiffBounds(shot, reference, new Rect(20, 20, 40, 40)));
        Assert.Equal(new Rect(5, 5, 4, 4), Calibrator.DiffBounds(shot, reference, new Rect(0, 0, 100, 100)));
    }

    [Fact]
    [Trait("Category", "Desktop")]
    public void Screenshot_Captures_Primary_Monitor_Size()
    {
        if (!DesktopView.IsAvailable()) return;   // Session 0 or a locked workstation: skip, do not fail
        var m = DeskWall.Core.Display.Monitors.Enumerate().First(x => x.IsPrimary);
        using var s = Screenshot.Capture(m.Bounds);
        Assert.Equal((m.Bounds.W, m.Bounds.H), (s.Width, s.Height));
    }

    /// <summary>The second half of the name is the half that matters: a capture on a locked session
    /// comes back fully black and still has alpha 255, so asserting only alpha proved nothing.</summary>
    [Fact]
    [Trait("Category", "Desktop")]
    public void Screenshot_Is_Opaque_And_Not_Uniformly_Black()
    {
        if (!DesktopView.IsAvailable()) return;
        var m = DeskWall.Core.Display.Monitors.Enumerate().First(x => x.IsPrimary);
        // The middle of the primary monitor, not its top-left corner: a wallpaper can be black at an edge.
        using var s = Screenshot.Capture(new Rect(m.Bounds.X + m.Bounds.W / 2 - 64, m.Bounds.Y + m.Bounds.H / 2 - 64, 128, 128));
        var (a, _, _, _) = s.GetPixel(10, 10);
        Assert.Equal(255, a);
        var lit = false;
        for (var y = 0; y < 128 && !lit; y += 4)
            for (var x = 0; x < 128 && !lit; x += 4)
            {
                var (_, r, g, b) = s.GetPixel(x, y);
                lit = r != 0 || g != 0 || b != 0;
            }
        Assert.True(lit, "the capture was uniformly black: nothing was on screen to capture");
    }
}
