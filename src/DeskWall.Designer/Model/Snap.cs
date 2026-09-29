using DeskWall.Core;

namespace DeskWall.Designer.Model;

/// <summary>Edge snapping: given a moving rect, the other rects and the canvas, return the adjusted
/// rect and the guide lines that explain the adjustment. Pure maths, in canvas pixels; the view
/// divides the threshold by its zoom.</summary>
public static class Snap
{
    public const int Threshold = 6;

    public sealed record Guide(bool Vertical, int Position);

    /// <summary>The lines a gesture can snap to: the canvas edges, and each other rect's near edge,
    /// far edge and centre line, per axis. Shared by <see cref="Apply"/> (whole-rect moves) and
    /// <see cref="Edge"/> (resizes), so a drag and a resize snap to exactly the same places.</summary>
    public static (IReadOnlyList<int> Xs, IReadOnlyList<int> Ys) Lines(IEnumerable<Rect> others, Rect canvas)
    {
        var xs = new List<int> { canvas.X, canvas.Right };
        var ys = new List<int> { canvas.Y, canvas.Bottom };
        foreach (var o in others)
        {
            xs.Add(o.X); xs.Add(o.Right); xs.Add(o.X + o.W / 2);
            ys.Add(o.Y); ys.Add(o.Bottom); ys.Add(o.Y + o.H / 2);
        }
        return (xs, ys);
    }

    public static (Rect Snapped, IReadOnlyList<Guide> Guides) Apply(Rect moving, IEnumerable<Rect> others, Rect canvas, int threshold = Threshold)
    {
        var (xs, ys) = Lines(others, canvas);
        var guides = new List<Guide>();
        var dx = Best(xs, [moving.X, moving.Right, moving.X + moving.W / 2], threshold, out var gx);
        var dy = Best(ys, [moving.Y, moving.Bottom, moving.Y + moving.H / 2], threshold, out var gy);
        if (gx is { } vx) guides.Add(new Guide(true, vx));
        if (gy is { } hy) guides.Add(new Guide(false, hy));
        return (moving.Offset(dx, dy), guides);
    }

    /// <summary>Pull one edge onto the nearest candidate line, or leave it alone. A resize moves one
    /// or two edges and must not shift the rest of the rect, which is what <see cref="Apply"/>
    /// would do.</summary>
    public static int Edge(int value, IReadOnlyList<int> candidates, int threshold, out int? guide)
    {
        guide = null;
        var best = threshold + 1;
        var result = value;
        foreach (var c in candidates)
        {
            var d = Math.Abs(c - value);
            if (d > threshold || d >= best) continue;
            best = d; result = c; guide = c;
        }
        return result;
    }

    /// <summary>A resize's box (<paramref name="box"/>, from <see cref="Model.Resize.Box"/>) with the
    /// edges <paramref name="handle"/> moves pulled onto the nearest line (<see cref="Edge"/>); the
    /// anchored edges never move. A corner keeps <paramref name="start"/>'s aspect ratio: the axis
    /// that snapped leads and the other follows it, so only that axis's guide is shown.</summary>
    public static (Rect Snapped, IReadOnlyList<Guide> Guides) Resize(Rect start, Rect box, Handle handle,
        IEnumerable<Rect> others, Rect canvas, int threshold = Threshold)
    {
        var (xs, ys) = Lines(others, canvas);
        int left = box.X, top = box.Y, right = box.Right, bottom = box.Bottom;
        int? gx = null, gy = null;
        if (Model.Resize.MovesLeft(handle)) left = Edge(left, xs, threshold, out gx);
        if (Model.Resize.MovesRight(handle)) right = Edge(right, xs, threshold, out gx);
        if (Model.Resize.MovesTop(handle)) top = Edge(top, ys, threshold, out gy);
        if (Model.Resize.MovesBottom(handle)) bottom = Edge(bottom, ys, threshold, out gy);

        var w = Math.Max(Model.Resize.MinSize, right - left);
        var h = Math.Max(Model.Resize.MinSize, bottom - top);
        if (Model.Resize.IsCorner(handle) && start.W > 0 && start.H > 0 && (gx is not null || gy is not null))
        {
            var ratio = start.W / (double)start.H;
            if (gx is not null) { h = Math.Max(Model.Resize.MinSize, (int)Math.Round(w / ratio)); gy = null; }
            else w = Math.Max(Model.Resize.MinSize, (int)Math.Round(h * ratio));
        }
        else if (Model.Resize.IsCorner(handle)) { w = box.W; h = box.H; }
        if (Model.Resize.MovesLeft(handle)) left = right - w; else right = left + w;
        if (Model.Resize.MovesTop(handle)) top = bottom - h; else bottom = top + h;

        var guides = new List<Guide>();
        if (gx is { } vx && (vx == left || vx == right)) guides.Add(new Guide(true, vx));
        if (gy is { } hy && (hy == top || hy == bottom)) guides.Add(new Guide(false, hy));
        return (new Rect(left, top, right - left, bottom - top), guides);
    }

    /// <summary>Smallest delta that brings any of the moving edges onto a candidate line.</summary>
    private static int Best(IReadOnlyList<int> candidates, int[] edges, int threshold, out int? matched)
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
