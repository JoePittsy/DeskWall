using DeskWall.Core;
using DeskWall.Core.Render;
using Xunit;

namespace DeskWall.Core.Tests.Render;

/// <summary>
/// The bitmap downscale filter. <c>ID2D1RenderTarget::DrawBitmap</c> offers only nearest and
/// linear, and linear takes a 2x2 sample, which aliases on the 1.7x downscale the weather widget
/// does (96 px icons into a 56 px rect). The render target created here queries out as an
/// <c>ID2D1DeviceContext</c>, whose DrawBitmap takes the full interpolation set, so
/// <see cref="Resample.High"/> can use the pre-downscaling high-quality cubic.
/// </summary>
public class ResampleTests
{
    /// <summary>The whole feature rests on this QueryInterface succeeding. If it ever stops (an
    /// older Windows, a different render-target type), <see cref="Surface.DrawSurface"/> falls back
    /// to bilinear silently and the aliasing test below would be the only sign - so assert it
    /// directly and fail with something that says what happened.</summary>
    [Fact]
    public void The_Software_Render_Target_Exposes_A_Device_Context()
    {
        using var s = Surface.Create(16, 16);
        Assert.True(s.HasDeviceContext,
            "the WIC bitmap render target no longer queries out as ID2D1DeviceContext; DrawSurface has fallen back to bilinear");
    }

    /// <summary>
    /// A one-pixel stripe pattern is far above the Nyquist limit of a 1.7x downscale, so the right
    /// answer is a flat field: every column the same mid grey. Bilinear samples 2x2 and beats
    /// against the stripes instead, leaving bands. High-quality cubic pre-downscales first, which
    /// is exactly the difference the weather icons show. Measured as the variance across columns of
    /// the result, which is zero for the ideal flat field.
    /// </summary>
    [Fact]
    public void High_Quality_Downscaling_Aliases_Less_Than_Bilinear()
    {
        using var stripes = Surface.Create(96, 96);
        stripes.Clear(new Color(255, 0, 0, 0));
        for (var x = 0; x < 96; x += 2) stripes.FillRect(new Rect(x, 0, 1, 96), new Color(255, 255, 255, 255));

        var fast = ColumnVariance(stripes, Resample.Fast);
        var high = ColumnVariance(stripes, Resample.High);

        Assert.True(high < fast / 2,
            $"high-quality downscale should be markedly flatter than bilinear on a sub-Nyquist pattern: {high:N1} vs {fast:N1}");
    }

    /// <summary>Upscaling and same-size draws are unaffected in kind; this only pins that the
    /// quality argument is honoured rather than ignored, so a caller asking for Fast really gets
    /// the cheap filter (which is what <c>BaseCache</c> relies on for its cold-start cost).</summary>
    [Fact]
    public void The_Two_Resample_Modes_Produce_Different_Pixels()
    {
        using var src = Surface.Create(96, 96);
        src.Clear(new Color(255, 0, 0, 0));
        for (var x = 0; x < 96; x += 2) src.FillRect(new Rect(x, 0, 1, 96), new Color(255, 255, 255, 255));

        using var a = Downscaled(src, Resample.Fast);
        using var b = Downscaled(src, Resample.High);
        var differs = false;
        for (var y = 0; y < 56 && !differs; y++)
            for (var x = 0; x < 56 && !differs; x++)
                differs = a.GetPixel(x, y) != b.GetPixel(x, y);
        Assert.True(differs, "Resample.Fast and Resample.High produced identical pixels: the argument is being ignored");
    }

    private static Surface Downscaled(Surface src, Resample r)
    {
        var dst = Surface.Create(56, 56);
        dst.Clear(new Color(255, 0, 0, 0));
        dst.DrawSurface(src, new Rect(0, 0, 56, 56), Fit.Stretch, resample: r);
        return dst;
    }

    /// <summary>Variance of the middle row's luminance across columns. Zero means the stripes
    /// resolved to a flat field, which is what a correct downscale of a sub-Nyquist pattern is.</summary>
    private static double ColumnVariance(Surface src, Resample r)
    {
        using var dst = Downscaled(src, r);
        var values = new double[56];
        for (var x = 0; x < 56; x++) values[x] = dst.GetPixel(x, 28).R;
        var mean = values.Average();
        return values.Sum(v => (v - mean) * (v - mean)) / values.Length;
    }
}
