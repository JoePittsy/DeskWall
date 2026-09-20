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
    public void Screenshot_Captures_Primary_Monitor_Size()
    {
        var m = DeskWall.Core.Display.Monitors.Enumerate().First(x => x.IsPrimary);
        using var s = Screenshot.Capture(m.Bounds);
        Assert.Equal((m.Bounds.W, m.Bounds.H), (s.Width, s.Height));
    }

    [Fact]
    public void Screenshot_Is_Opaque_And_Not_Uniformly_Black()
    {
        var m = DeskWall.Core.Display.Monitors.Enumerate().First(x => x.IsPrimary);
        using var s = Screenshot.Capture(new Rect(m.Bounds.X, m.Bounds.Y, 64, 64));
        var (a, _, _, _) = s.GetPixel(10, 10);
        Assert.Equal(255, a);
    }
}
