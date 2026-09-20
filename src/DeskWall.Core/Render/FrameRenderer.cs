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
        try
        {
            foreach (var c in components.OrderBy(c => c.Z)) Draw(frame, c);
        }
        catch
        {
            // Finding 7: a throw partway through (a corrupt image file, a COM failure) must not
            // leak the 19.8 MB (at 3440x1440) bitmap this method just loaded.
            frame.Dispose();
            throw;
        }
        return frame;
    }

    /// <summary>
    /// Incremental render: start from the previous frame, repaint the base under every dirty rect,
    /// then redraw in z-order every component that intersects a dirty rect. Pixels outside the
    /// dirty rects are the previous frame's, untouched. <paramref name="previous"/> is modified in
    /// place and returned; the caller owns it either way.
    /// <para>
    /// A rect is dirty when it is the new paint bounds of a changed component, the <em>previous</em>
    /// paint bounds of a changed component (otherwise a component that moved or shrank leaves its
    /// old pixels behind), or the paint bounds of a component that vanished since the previous
    /// frame. Paint bounds, not rects: text paints its shadow outside its rect and the clip in
    /// <see cref="Surface.DrawText"/> is set to exactly the same bounds.
    /// </para>
    /// </summary>
    /// <param name="drawn">Finding 13: how many components were actually painted, which is not the
    /// number of changed ids - a dirty rect drags every component that overlaps it back onto the
    /// frame, and a shortcut paints nothing at all. This is the number the tick log reports.</param>
    public Surface RenderIncremental(Surface previous, string baseRawPath, IReadOnlyList<Resolved> all,
        IReadOnlySet<string> changedIds, IReadOnlyDictionary<string, Rect> previousRects, out int drawn)
    {
        drawn = 0;
        if (previous.Width != width || previous.Height != height) throw new InvalidOperationException("previous frame size mismatch");
        var dirty = new List<Rect>();
        foreach (var c in all) if (changedIds.Contains(c.Id)) dirty.Add(c.PaintBounds);
        foreach (var id in changedIds) if (previousRects.TryGetValue(id, out var pr)) dirty.Add(pr);
        var liveIds = all.Select(c => c.Id).ToHashSet();
        foreach (var (id, rect) in previousRects) if (!liveIds.Contains(id)) dirty.Add(rect);
        if (dirty.Count == 0) return previous;

        var frame = previous;
        using (var baseSurf = Surface.LoadRaw(baseRawPath))
            foreach (var d in dirty) frame.CopyRect(baseSurf, d);
        foreach (var c in all.OrderBy(c => c.Z))
            if (c is not ResolvedShortcut && dirty.Any(d => d.Intersects(c.PaintBounds))) { Draw(frame, c); drawn++; }
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
