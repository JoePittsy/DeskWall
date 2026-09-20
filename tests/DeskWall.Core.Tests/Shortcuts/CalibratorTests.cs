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

    /// <summary>Everything calibrate produces is one measurement of pixels, so a screenshot of whatever
    /// is in front is not a degraded answer, it is a wrong one. MinimizeAll had in fact never worked in
    /// a compiled build, and because the failure was only a warning it took a whole lane to notice.
    /// <para>
    /// Nothing here goes near the live desktop, which is the point of the pre-flight: Run must throw
    /// before the folder flags are read, before the probe image is built and before it is applied.
    /// </para></summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_Minimiser_That_Fails_Stops_Run_Before_Anything_Is_Touched(bool byThrowing)
    {
        var monitor = new DeskWall.Core.Display.MonitorInfo(
            new DeskWall.Core.Display.DisplaySignature("TEST", 800, 600, 100), new Rect(0, 0, 800, 600), true, "TEST");
        var probeJpg = Paths.InRuntime("calibrate.jpg");
        if (File.Exists(probeJpg)) File.Delete(probeJpg);
        var shell = new InvalidOperationException("the shell said no");
        var minimise = byThrowing ? new Func<bool>(() => throw shell) : new Func<bool>(() => false);
        var probesWritten = 0;
        var linesSaid = 0;

        var ex = Assert.Throws<InvalidOperationException>(() => Calibrator.Run(
            monitor, "no-such-wallpaper.jpg", _ => probesWritten++, _ => linesSaid++, minimise));

        Assert.Equal("could not minimise windows; calibration would measure whatever is in front", ex.Message);
        if (byThrowing) Assert.Same(shell, ex.InnerException); else Assert.Null(ex.InnerException);
        // The probe wallpaper is the first thing Run writes, and it writes it before applying it, so
        // its absence is also proof that no wallpaper was changed.
        Assert.False(File.Exists(probeJpg));
        Assert.Equal(0, probesWritten);
        Assert.Equal(0, linesSaid);   // the first `say` reports the shell's icon size: not reached
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
