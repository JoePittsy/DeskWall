using CRect = DeskWall.Core.Rect;

namespace DeskWall.Designer.Model;

/// <summary>Where the canvas is looking: the zoom, and where canvas (0, 0) sits in the pane, in
/// screen pixels. Immutable and pure, so fit, zoom-about-a-point and the pan clamp are testable
/// without a window (plan Task 3.1).
/// <para>Continuous zoom from <see cref="MinZoom"/> to <see cref="MaxZoom"/>: Ctrl+wheel scales by a
/// factor per notch (<see cref="ZoomAbout"/>), and the + and - commands step through
/// <see cref="Steps"/>. Above 1 the preview renders only this viewport, at this zoom
/// (<see cref="PreviewRenderer"/>), so small parts stay crisp.</para></summary>
/// <param name="Zoom">Screen pixels per canvas pixel.</param>
/// <param name="OriginX">Screen x of canvas x = 0.</param>
/// <param name="OriginY">Screen y of canvas y = 0.</param>
/// <param name="Width">The pane's width in screen pixels.</param>
/// <param name="Height">The pane's height in screen pixels.</param>
public readonly record struct Viewport(double Zoom, double OriginX, double OriginY, double Width, double Height)
{
    public const double MinZoom = 1.0 / 8;
    public const double MaxZoom = 16;

    /// <summary>What + and - step through. Round numbers a reader recognises in the zoom label.</summary>
    public static readonly IReadOnlyList<double> Steps =
        [1.0 / 8, 1.0 / 4, 1.0 / 3, 1.0 / 2, 2.0 / 3, 1, 1.5, 2, 3, 4, 6, 8, 12, 16];

    /// <summary>Screen pixels left round what a fit fits, so its edges are not jammed against the pane.</summary>
    public const double FitMargin = 24;

    /// <summary>How much of the canvas must stay in the pane, in screen pixels, however far it is
    /// panned. A view that can be scrolled into blank grey is a view that gets lost.</summary>
    public const double KeepVisible = 120;

    public static double ClampZoom(double zoom) => Math.Clamp(double.IsFinite(zoom) ? zoom : 1, MinZoom, MaxZoom);

    public (double X, double Y) ToCanvas(double screenX, double screenY)
        => ((screenX - OriginX) / Zoom, (screenY - OriginY) / Zoom);

    public (double X, double Y, double W, double H) ToScreen(CRect r)
        => (OriginX + r.X * Zoom, OriginY + r.Y * Zoom, r.W * Zoom, r.H * Zoom);

    /// <summary>The same view in a pane of a new size, with the canvas point that was at the pane's
    /// centre still there.</summary>
    public Viewport Resized(double width, double height)
        => this with
        {
            OriginX = OriginX + (width - Width) / 2,
            OriginY = OriginY + (height - Height) / 2,
            Width = width,
            Height = height,
        };

    /// <summary><paramref name="zoom"/> (clamped), keeping the canvas point under the screen point
    /// (<paramref name="screenX"/>, <paramref name="screenY"/>) where it is: Ctrl+wheel.</summary>
    public Viewport ZoomAbout(double zoom, double screenX, double screenY)
    {
        var z = ClampZoom(zoom);
        var (cx, cy) = ToCanvas(screenX, screenY);
        return this with { Zoom = z, OriginX = screenX - cx * z, OriginY = screenY - cy * z };
    }

    /// <summary>The next step in or out of <see cref="Steps"/> from the current zoom, about the pane's centre.</summary>
    public Viewport Step(bool zoomIn)
    {
        var zoom = Zoom;
        var next = zoomIn
            ? Steps.FirstOrDefault(s => s > zoom * 1.001, MaxZoom)
            : Steps.LastOrDefault(s => s < zoom / 1.001, MinZoom);
        return ZoomAbout(next, Width / 2, Height / 2);
    }

    /// <summary>The whole of <paramref name="content"/> in the pane with <see cref="FitMargin"/> round
    /// it, centred: Shift+1 on the canvas, Shift+2 on the selection. Clamped to the zoom range, and to
    /// <paramref name="maxZoom"/> so fitting a 13 px label does not fill a monitor with it.</summary>
    public Viewport Fit(CRect content, double maxZoom = MaxZoom)
    {
        if (content.W <= 0 || content.H <= 0 || Width <= 0 || Height <= 0) return this;
        var z = Math.Min((Width - 2 * FitMargin) / content.W, (Height - 2 * FitMargin) / content.H);
        z = Math.Min(ClampZoom(z), Math.Max(MinZoom, maxZoom));
        return this with
        {
            Zoom = z,
            OriginX = (Width - content.W * z) / 2 - content.X * z,
            OriginY = (Height - content.H * z) / 2 - content.Y * z,
        };
    }

    public Viewport Pan(double dx, double dy) => this with { OriginX = OriginX + dx, OriginY = OriginY + dy };

    /// <summary>Keep at least <see cref="KeepVisible"/> screen pixels (or all of it, when it is
    /// smaller) of a <paramref name="canvasW"/> x <paramref name="canvasH"/> canvas inside the pane on
    /// each axis. Moved here from <c>PreviewView.ClampPan</c>.</summary>
    public Viewport Clamp(int canvasW, int canvasH)
        => this with { OriginX = Axis(OriginX, canvasW * Zoom, Width), OriginY = Axis(OriginY, canvasH * Zoom, Height) };

    private static double Axis(double origin, double extent, double pane)
    {
        if (pane <= 0 || extent <= 0) return origin;
        var keep = Math.Min(KeepVisible, Math.Min(extent, pane));
        var lo = keep - extent;     // the canvas's far edge at least `keep` into the pane
        var hi = pane - keep;       // its near edge at least `keep` before the pane's far side
        return Math.Clamp(origin, Math.Min(lo, hi), Math.Max(lo, hi));
    }
}
