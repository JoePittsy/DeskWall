using DeskWall.Core;

namespace DeskWall.Designer.Model;

/// <summary>Edge snapping: given a moving rect, the other rects and the canvas, return the adjusted
/// rect and the guide lines that explain the adjustment. Pure maths, in canvas pixels; the view
/// divides the threshold by its zoom.</summary>
public static class Snap
{
    public const int Threshold = 6;

    public sealed record Guide(bool Vertical, int Position);

    public static (Rect Snapped, IReadOnlyList<Guide> Guides) Apply(Rect moving, IEnumerable<Rect> others, Rect canvas, int threshold = Threshold)
    {
        var xs = new List<int> { canvas.X, canvas.Right };
        var ys = new List<int> { canvas.Y, canvas.Bottom };
        foreach (var o in others)
        {
            xs.Add(o.X); xs.Add(o.Right); xs.Add(o.X + o.W / 2);
            ys.Add(o.Y); ys.Add(o.Bottom); ys.Add(o.Y + o.H / 2);
        }
        var guides = new List<Guide>();
        var dx = Best(xs, [moving.X, moving.Right, moving.X + moving.W / 2], threshold, out var gx);
        var dy = Best(ys, [moving.Y, moving.Bottom, moving.Y + moving.H / 2], threshold, out var gy);
        if (gx is { } vx) guides.Add(new Guide(true, vx));
        if (gy is { } hy) guides.Add(new Guide(false, hy));
        return (moving.Offset(dx, dy), guides);
    }

    /// <summary>Smallest delta that brings any of the moving edges onto a candidate line.</summary>
    private static int Best(List<int> candidates, int[] edges, int threshold, out int? matched)
    {
        var bestDelta = 0; var bestAbs = threshold + 1; matched = null;
        foreach (var c in candidates)
            foreach (var e in edges)
            {
                var d = c - e;
                if (Math.Abs(d) <= threshold && Math.Abs(d) < bestAbs) { bestAbs = Math.Abs(d); bestDelta = d; matched = c; }
            }
        return bestDelta;
    }
}
