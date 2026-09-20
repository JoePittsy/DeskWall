using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DeskWall.Designer.Model;
using CRect = DeskWall.Core.Rect;

namespace DeskWall.Designer.Views;

/// <summary>Which of the eight handles a point is over. <see cref="None"/> is -1 so the value can
/// be stored as a plain index into <see cref="CanvasAdorner.HandlePoints"/>.</summary>
internal enum Handle
{
    None = -1,
    TopLeft = 0, Top = 1, TopRight = 2, Right = 3, BottomRight = 4, Bottom = 5, BottomLeft = 6, Left = 7,
}

/// <summary>
/// The drawing surface. Paints the Core preview bitmap and, over it, the repeater cell outlines,
/// the selection outline, the eight resize handles, the snap guides and the marquee - all in one
/// <see cref="OnRender"/> with one transform, so the overlay cannot drift a pixel from the pixels
/// it is annotating at any zoom. Presentation only: <see cref="CanvasView"/> owns the state and
/// every gesture, and sets these fields then calls <see cref="FrameworkElement.InvalidateVisual"/>.
/// </summary>
internal sealed class CanvasAdorner : FrameworkElement
{
    /// <summary>Side of a resize handle in screen pixels: constant on screen, so it stays grabbable
    /// at 25 percent and does not swamp the component at 400 percent.</summary>
    public const double HandleSize = 8;

    private static readonly Brush Back = Freeze(new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)));
    private static readonly Pen Accent = Freeze(new Pen(Freeze(new SolidColorBrush(SystemColors.HighlightColor)), 1));
    private static readonly Pen CellFirstPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF))), 1));
    private static readonly Pen CellRestPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(0x3A, 0xFF, 0xFF, 0xFF))), 1));
    private static readonly Pen GuidePen = Freeze(new Pen(Freeze(new SolidColorBrush(SystemColors.HighlightColor)), 1) { DashStyle = new DashStyle([4, 3], 0) });
    private static readonly Pen MarqueePen = Freeze(new Pen(Freeze(new SolidColorBrush(SystemColors.HighlightColor)), 1) { DashStyle = new DashStyle([3, 3], 0) });
    /// <summary>The repeater a template child in the layers panel belongs to. Dashed, so it reads as
    /// "this is the thing you are editing inside" and never as a selection you can drag.</summary>
    private static readonly Pen HighlightPen = Freeze(new Pen(Freeze(new SolidColorBrush(SystemColors.HighlightColor)), 2) { DashStyle = new DashStyle([6, 4], 0) });
    private static readonly Brush MarqueeFill = Freeze(new SolidColorBrush(Color.FromArgb(0x30, SystemColors.HighlightColor.R, SystemColors.HighlightColor.G, SystemColors.HighlightColor.B)));
    private static readonly Brush HandleFill = Freeze(new SolidColorBrush(Colors.White));

    // ---- state, set by CanvasView -----------------------------------------------------------

    public BitmapSource? Bitmap;
    public int CanvasW;
    public int CanvasH;
    public double Zoom = 1;
    /// <summary>Screen point of canvas (0, 0).</summary>
    public Point Origin;
    /// <summary>Selected components, in canvas pixels, already including any live drag or resize.</summary>
    public IReadOnlyList<CRect> Outlines = Array.Empty<CRect>();
    /// <summary>The rect the handles sit on: single selection only, null otherwise.</summary>
    public CRect? Handles;
    /// <summary>Expanded repeater cells; the first cell of each repeater is drawn at full opacity.</summary>
    public IReadOnlyList<(CRect Rect, bool First)> Cells = Array.Empty<(CRect, bool)>();
    public IReadOnlyList<Snap.Guide> Guides = Array.Empty<Snap.Guide>();
    /// <summary>Marquee in canvas pixels, null when not dragging one.</summary>
    public CRect? Marquee;
    /// <summary>A component to outline without selecting it: the repeater whose template child the
    /// layers panel is pointing at. Null the rest of the time.</summary>
    public CRect? Highlight;

    public CanvasAdorner()
    {
        ClipToBounds = true;
        Focusable = false;
        IsHitTestVisible = false;   // CanvasView takes the input; this only paints
    }

    // ---- coordinates -------------------------------------------------------------------------

    public Point ToScreen(double cx, double cy) => new(Origin.X + cx * Zoom, Origin.Y + cy * Zoom);

    public Rect ToScreen(CRect r) => new(ToScreen(r.X, r.Y), new Size(Math.Max(0, r.W * Zoom), Math.Max(0, r.H * Zoom)));

    /// <summary>The eight handle centres of a screen rect, in <see cref="Handle"/> order.</summary>
    public static Point[] HandlePoints(Rect r)
    {
        double l = r.Left, cx = r.Left + r.Width / 2, rt = r.Right, t = r.Top, cy = r.Top + r.Height / 2, b = r.Bottom;
        return [new(l, t), new(cx, t), new(rt, t), new(rt, cy), new(rt, b), new(cx, b), new(l, b), new(l, cy)];
    }

    public static Cursor CursorFor(Handle h) => h switch
    {
        Handle.TopLeft or Handle.BottomRight => Cursors.SizeNWSE,
        Handle.TopRight or Handle.BottomLeft => Cursors.SizeNESW,
        Handle.Top or Handle.Bottom => Cursors.SizeNS,
        Handle.Left or Handle.Right => Cursors.SizeWE,
        _ => Cursors.Arrow,
    };

    // ---- painting ----------------------------------------------------------------------------

    /// <summary>Repaint. Not InvalidateVisual directly: the scaling mode is a dependency property
    /// and must not be written from inside OnRender.</summary>
    public void Refresh()
    {
        // Nearest-neighbour once a canvas pixel is bigger than a screen pixel: at 400 percent the
        // user is placing single pixels and must see them, not a smoothed guess.
        var mode = Zoom >= 1 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality;
        if (RenderOptions.GetBitmapScalingMode(this) != mode) RenderOptions.SetBitmapScalingMode(this, mode);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Back, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (Bitmap is null || CanvasW <= 0 || CanvasH <= 0) return;

        var canvas = new Rect(Origin.X, Origin.Y, CanvasW * Zoom, CanvasH * Zoom);
        dc.DrawImage(Bitmap, canvas);

        foreach (var (rect, first) in Cells) dc.DrawRectangle(null, first ? CellFirstPen : CellRestPen, Crisp(ToScreen(rect)));

        foreach (var g in Guides)
        {
            if (g.Vertical)
            {
                var x = Snap05(ToScreen(g.Position, 0).X);
                dc.DrawLine(GuidePen, new Point(x, canvas.Top), new Point(x, canvas.Bottom));
            }
            else
            {
                var y = Snap05(ToScreen(0, g.Position).Y);
                dc.DrawLine(GuidePen, new Point(canvas.Left, y), new Point(canvas.Right, y));
            }
        }

        if (Highlight is { } hl) dc.DrawRectangle(null, HighlightPen, Crisp(ToScreen(hl)));

        foreach (var o in Outlines) dc.DrawRectangle(null, Accent, Crisp(ToScreen(o)));

        if (Handles is { } h)
            foreach (var p in HandlePoints(ToScreen(h)))
                dc.DrawRectangle(HandleFill, Accent,
                    Crisp(new Rect(p.X - HandleSize / 2, p.Y - HandleSize / 2, HandleSize, HandleSize)));

        if (Marquee is { } m) dc.DrawRectangle(MarqueeFill, MarqueePen, Crisp(ToScreen(m)));
    }

    /// <summary>Half-pixel offset so a 1 px pen lands on one device pixel instead of two grey ones.</summary>
    private static double Snap05(double v) => Math.Round(v) + 0.5;

    private static Rect Crisp(Rect r)
    {
        var l = Snap05(r.Left); var t = Snap05(r.Top);
        return new Rect(l, t, Math.Max(0, Math.Round(r.Right) - 0.5 - l), Math.Max(0, Math.Round(r.Bottom) - 0.5 - t));
    }

    private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }
}
