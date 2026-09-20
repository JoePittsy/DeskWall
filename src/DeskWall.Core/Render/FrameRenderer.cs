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
