using DeskWall.Core;
using DeskWall.Core.Render;

namespace DeskWall.Core.Tests.Goldens;

/// <summary>Pixel comparison for golden-image tests. Reads both surfaces out in one lock each
/// (Surface.CopyTo) rather than per-pixel GetPixel, which would take one WIC lock per pixel over
/// a 860x360 canvas.</summary>
public static class Compare
{
    /// <summary>Per-channel (A, R, G, B) difference on the premultiplied bytes, which is enough to
    /// find a rendering regression without needing to unpremultiply first: two renders of the same
    /// scene premultiply identically.</summary>
    public static (int DifferentPixels, Rect? Bounds) Diff(Surface a, Surface b, int tolerance = 8)
    {
        if (a.Width != b.Width || a.Height != b.Height)
            throw new ArgumentException($"surfaces differ in size: {a.Width}x{a.Height} vs {b.Width}x{b.Height}");

        var w = a.Width; var h = a.Height;
        var ba = new byte[w * h * 4];
        var bb = new byte[w * h * 4];
        a.CopyTo(ba);
        b.CopyTo(bb);

        var different = 0;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (var y = 0; y < h; y++)
        {
            var row = y * w * 4;
            for (var x = 0; x < w; x++)
            {
                var i = row + x * 4;
                if (Differs(ba, bb, i, tolerance))
                {
                    different++;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
        }
        Rect? bounds = different == 0 ? null : new Rect(minX, minY, maxX - minX + 1, maxY - minY + 1);
        return (different, bounds);
    }

    /// <summary>Writes an image the same size as <paramref name="a"/>: black everywhere the two
    /// surfaces agree (within tolerance), opaque red everywhere they do not.</summary>
    public static void WriteDiffPng(Surface a, Surface b, string path, int tolerance = 8)
    {
        var w = a.Width; var h = a.Height;
        var ba = new byte[w * h * 4];
        var bb = new byte[w * h * 4];
        a.CopyTo(ba);
        b.CopyTo(bb);

        var outBgra = new byte[w * h * 4];
        for (var i = 0; i < ba.Length; i += 4)
        {
            if (Differs(ba, bb, i, tolerance))
            {
                outBgra[i] = 0; outBgra[i + 1] = 0; outBgra[i + 2] = 255; outBgra[i + 3] = 255;   // red
            }
            else
            {
                outBgra[i] = 0; outBgra[i + 1] = 0; outBgra[i + 2] = 0; outBgra[i + 3] = 255;   // black
            }
        }
        using var s = Surface.FromBgra(w, h, outBgra);
        s.SavePng(path);
    }

    private static bool Differs(byte[] a, byte[] b, int i, int tolerance)
        => Math.Abs(a[i] - b[i]) > tolerance || Math.Abs(a[i + 1] - b[i + 1]) > tolerance ||
           Math.Abs(a[i + 2] - b[i + 2]) > tolerance || Math.Abs(a[i + 3] - b[i + 3]) > tolerance;
}
