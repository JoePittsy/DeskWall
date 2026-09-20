using DeskWall.Core.Resolve;

namespace DeskWall.Core.Render;

public sealed class FrameRenderer(int width, int height)
{
    public int Width => width;
    public int Height => height;

    /// <summary>Full render: base then every component in z-order. Returns the frame.</summary>
    public Surface RenderAll(string baseRawPath, IReadOnlyList<Resolved> components)
    {
        var frame = Surface.LoadRaw(baseRawPath);
        if (frame.Width != width || frame.Height != height)
        {
            frame.Dispose();
            throw new InvalidOperationException("base cache size mismatch");
        }
        foreach (var c in components.OrderBy(c => c.Z)) Draw(frame, c);
        return frame;
    }

    /// <summary>
    /// Incremental render: start from the previous frame, repaint the base under every dirty rect
    /// (changed components plus components that vanished since the previous frame), then redraw in
    /// z-order every component that intersects a dirty rect. Pixels outside the dirty rects are the
    /// previous frame's, untouched. <paramref name="previous"/> is modified in place and returned;
    /// the caller owns it either way.
    /// </summary>
    public Surface RenderIncremental(Surface previous, string baseRawPath, IReadOnlyList<Resolved> all,
        IReadOnlySet<string> changedIds, IReadOnlyDictionary<string, Rect> previousRects)
    {
        if (previous.Width != width || previous.Height != height) throw new InvalidOperationException("previous frame size mismatch");
        var dirty = new List<Rect>();
        foreach (var c in all) if (changedIds.Contains(c.Id)) dirty.Add(c.Rect);
        var liveIds = all.Select(c => c.Id).ToHashSet();
        foreach (var (id, rect) in previousRects) if (!liveIds.Contains(id)) dirty.Add(rect);
        if (dirty.Count == 0) return previous;

        var frame = previous;
        using (var baseSurf = Surface.LoadRaw(baseRawPath))
            foreach (var d in dirty) frame.CopyRect(baseSurf, d);
        foreach (var c in all.OrderBy(c => c.Z))
            if (dirty.Any(d => d.Intersects(c.Rect))) Draw(frame, c);
        return frame;
    }

    /// <summary>Draw one component into an existing frame.</summary>
    public static void Draw(Surface frame, Resolved c)
    {
        switch (c)
        {
            case ResolvedText t:
                frame.DrawText(t.Text, t.Style, t.Rect);
                break;
            case ResolvedImage i:
                if (!File.Exists(i.Path)) { frame.FillRect(i.Rect, new Color(140, 0, 0, 0), i.Radius); break; }
                using (var img = Surface.Load(i.Path)) frame.DrawSurface(img, i.Rect, i.Fit, i.Opacity, i.Radius);
                break;
            case ResolvedBar b:
                frame.FillRect(b.Rect, b.Track);
                var f = Math.Clamp(b.Fraction, 0, 1);
                var fill = b.Direction == Axis.Horizontal
                    ? new Rect(b.Rect.X, b.Rect.Y, (int)Math.Round(b.Rect.W * f), b.Rect.H)
                    : new Rect(b.Rect.X, b.Rect.Bottom - (int)Math.Round(b.Rect.H * f), b.Rect.W, (int)Math.Round(b.Rect.H * f));
                if (fill.W > 0 && fill.H > 0) frame.FillRect(fill, b.Fill);
                break;
            case ResolvedShortcut:
                break;   // draws nothing; Phase 3 owns the icon
        }
    }
}
