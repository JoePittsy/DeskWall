namespace DeskWall.Core;

/// <summary>Physical pixels. Immutable.</summary>
public readonly record struct Rect(int X, int Y, int W, int H)
{
    public int Right => X + W;
    public int Bottom => Y + H;
    public bool Intersects(Rect o) => X < o.Right && o.X < Right && Y < o.Bottom && o.Y < Bottom;
    public Rect Offset(int dx, int dy) => new(X + dx, Y + dy, W, H);
    public Rect Scale(double sx, double sy) => new((int)Math.Round(X * sx), (int)Math.Round(Y * sy), (int)Math.Round(W * sx), (int)Math.Round(H * sy));
}

public enum Fit { Cover, Contain, Stretch }
public enum Axis { Vertical, Horizontal }
public enum Align { Left, Center, Right }

/// <summary>
/// Which filter <see cref="Render.Surface.DrawSurface"/> scales a bitmap with.
/// <para>
/// <see cref="High"/> is Direct2D's high-quality cubic: it pre-downscales before the cubic pass, so
/// it does not alias the way a 2x2 bilinear sample does on the 96 px weather icons drawn into 56 px.
/// Measured against an independent high-quality resample of the same icon, mean error per channel
/// went from 0.71 (bilinear) to 0.20 and the worst pixel from 128 to 26; plain cubic was worse than
/// bilinear at that ratio (1.06) because it rings. It costs nothing measurable on component-sized
/// bitmaps: a layout with four of those icons measured the same draw stage either way.
/// </para>
/// <para>
/// <see cref="Fast"/> is bilinear, and exists for one caller. Scaling the base photo
/// (3840x2160 -> 3440x1440) is 5 megapixels through WARP, and there the high-quality filter cost a
/// measured +163 ms on the draw stage against a 500 ms cold-start budget -- for a 1.12x downscale,
/// which bilinear handles perfectly well. That cost is paid on a base-cache miss, not per tick
/// (<c>BaseCache</c> keeps the scaled result), so it is cold start and display changes that would
/// pay it. Flip <c>BaseCache</c> to <see cref="High"/> if the base ever needs it.
/// </para>
/// </summary>
public enum Resample { High, Fast }
