using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DeskWall.Designer.Model;
using CRect = DeskWall.Core.Rect;

namespace DeskWall.Designer.Views;

/// <summary>
/// The wallpaper, as it will be, with the widgets on it selectable and draggable as whole things.
/// <para>
/// Job: show the owner what the desktop will look like, and let him say which widget he means and
/// what order they go in. It draws the composed frame, the column the arranger owns, and the
/// outline of whatever is selected. It deliberately has no coordinates, no handles, no grid, no
/// rulers and no alignment guides: a widget's position is the arranger's answer, not the owner's,
/// and every pixel of chrome here is chrome over a photograph.
/// </para>
/// </summary>
public partial class PreviewView : UserControl
{
    /// <summary>Screen pixels of vertical travel before a press becomes a reorder rather than a
    /// click. Below this a hand that wobbles would reshuffle the column.</summary>
    private const double DragSlop = 5;

    /// <summary>How much photograph the 1:1 view keeps either side of the column, so it reads as a
    /// slice of wallpaper rather than a floating strip.</summary>
    private const int ColumnMargin = 48;

    private readonly Surface _surface = new();
    private DesignerModel? _model;
    private PreviewRenderer? _renderer;
    private PreviewFrame? _frame;
    private WriteableBitmap? _bitmap;

    private bool _column;                 // false: the whole wallpaper; true: the column at 1:1
    private double _scroll;               // the 1:1 view's vertical offset
    private string? _hover;
    private string? _pressed;
    private bool _dragging;
    private Point _downScreen;
    private CRect _downBounds;

    public PreviewView()
    {
        InitializeComponent();
        Host.Child = _surface;
        _surface.SizeChanged += (_, _) => { PlaceView(); Redraw(); };
    }

    /// <summary>Raised when the owner drops a widget somewhere new in the stack. The shell owns the
    /// arranger, so the view only says "this one, at this index".</summary>
    public event Action<string, int>? Reordered;

    /// <summary>Raised when an unlocked widget - one the arranger has been told to leave alone - is
    /// dragged. The shell turns it into one undo entry.</summary>
    public event Action<string, int, int>? Moved;

    /// <summary>Which instances the arranger is not allowed to move, so a drag on one of them is a
    /// free move rather than a reorder. Set by the shell from the layout's widget records.</summary>
    public Func<string, bool> IsUnlocked { get; set; } = _ => false;

    public void Attach(DesignerModel model, PreviewRenderer renderer)
    {
        if (_model is not null) { _model.Changed -= OnModelChanged; _model.SelectionChanged -= OnSelectionChanged; }
        if (_renderer is not null) _renderer.Rendered -= OnRendered;
        _model = model;
        _renderer = renderer;
        _model.Changed += OnModelChanged;
        _model.SelectionChanged += OnSelectionChanged;
        _renderer.Rendered += OnRendered;
        PlaceView();
        _renderer.Request(_model);
        Redraw();
    }

    // ---- model and frame -----------------------------------------------------------------------

    private void OnModelChanged() { if (_model is not null) _renderer?.Request(_model); Redraw(); }

    private void OnSelectionChanged() => Redraw();

    private void OnRendered(PreviewFrame frame)
    {
        _frame = frame;
        if (_bitmap is null || _bitmap.PixelWidth != frame.Width || _bitmap.PixelHeight != frame.Height)
        {
            // Pbgra32, not Bgra32: Core's surface is already premultiplied, so this is a straight copy.
            _bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Pbgra32, null);
            _surface.Bitmap = _bitmap;
            PlaceView();
        }
        _bitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.Bgra, frame.Width * 4, 0);
        Redraw();
    }

    // ---- the two views -------------------------------------------------------------------------

    private void ZoomToggle_Click(object sender, RoutedEventArgs e)
    {
        _column = !_column;
        ZoomToggle.Content = _column ? "Whole wallpaper" : "Column at 1:1";
        PlaceView();
        Redraw();
    }

    /// <summary>Whole wallpaper: the frame fits the viewport. Column: 1:1, the column centred
    /// horizontally and the stack's top at the top of the viewport.</summary>
    private void PlaceView()
    {
        if (_model is null) return;
        double w = _model.Signature.Width, h = _model.Signature.Height;
        double vw = _surface.ActualWidth, vh = _surface.ActualHeight;
        if (w <= 0 || h <= 0 || vw <= 0 || vh <= 0) return;

        if (_column)
        {
            var col = Arranger.Column((int)w, (int)h);
            _surface.Zoom = 1.0;
            // The top of the column at the top of the pane. A column is 1360 px tall and the pane is
            // not, so the rest is reached with the wheel rather than by shrinking what "1:1" means.
            _surface.Origin = new Point(vw / 2 - (col.X + col.W / 2.0), ColumnMargin - col.Y + _scroll);
            _surface.Band = new CRect(col.X - ColumnMargin, 0, col.W + ColumnMargin * 2, (int)h);
        }
        else
        {
            _scroll = 0;
            _surface.Zoom = Math.Max(0.02, Math.Min((vw - 48) / w, (vh - 48) / h));
            _surface.Origin = new Point((vw - w * _surface.Zoom) / 2, (vh - h * _surface.Zoom) / 2);
            _surface.Band = null;
        }
    }

    /// <summary>In the 1:1 view the column is taller than the pane, so the wheel scrolls it. Clamped
    /// so the stack cannot be scrolled off either end into empty photograph.</summary>
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!_column || _model is null) return;
        var column = Arranger.Column(_model.Signature.Width, _model.Signature.Height);
        var travel = Math.Max(0, column.H + ColumnMargin * 2 - _surface.ActualHeight);
        _scroll = Math.Clamp(_scroll + e.Delta * 0.6, -travel, 0);
        PlaceView();
        Redraw();
        e.Handled = true;
    }

    // ---- what is where -------------------------------------------------------------------------

    private IReadOnlyList<(string Id, CRect Bounds)> Stack()
    {
        if (_model is null) return Array.Empty<(string, CRect)>();
        return WidgetInstance.Instances(_model.Layout)
            .Select(id => (id, WidgetInstance.Bounds(_model.Layout, id)))
            .Where(e => e.Item2.W > 0 && e.Item2.H > 0)
            .OrderBy(e => e.Item2.Y)
            .ToList();
    }

    private string? SelectedInstance()
    {
        if (_model is not { Selection.Count: > 0 }) return null;
        return _model.Find(_model.Selection[0])?.Widget;
    }

    private string? Pick(Point screen)
    {
        var p = _surface.ToCanvas(screen);
        string? best = null;
        foreach (var (id, b) in Stack())
            if (p.X >= b.X && p.X < b.Right && p.Y >= b.Y && p.Y < b.Bottom) best = id;   // later wins
        return best;
    }

    private void SelectInstance(string? id)
    {
        if (_model is null) return;
        if (id is null) { _model.ClearSelection(); return; }
        _model.Select(WidgetInstance.Components(_model.Layout, id).Select(c => c.Id).ToList());
    }

    // ---- pointer -------------------------------------------------------------------------------

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (_model is null || e.ChangedButton != MouseButton.Left) return;
        _downScreen = e.GetPosition(_surface);
        _pressed = Pick(_downScreen);
        _dragging = false;
        SelectInstance(_pressed);
        if (_pressed is not null)
        {
            _downBounds = WidgetInstance.Bounds(_model.Layout, _pressed);
            CaptureMouse();
        }
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_model is null) return;
        var p = e.GetPosition(_surface);

        if (_pressed is null || e.LeftButton != MouseButtonState.Pressed)
        {
            var hover = Pick(p);
            if (hover != _hover) { _hover = hover; Cursor = hover is null ? Cursors.Arrow : Cursors.Hand; Redraw(); }
            return;
        }

        if (!_dragging && Math.Abs(p.Y - _downScreen.Y) < DragSlop && Math.Abs(p.X - _downScreen.X) < DragSlop) return;
        _dragging = true;

        if (IsUnlocked(_pressed))
        {
            _surface.Ghost = _downBounds.Offset(
                (int)Math.Round((p.X - _downScreen.X) / _surface.Zoom),
                (int)Math.Round((p.Y - _downScreen.Y) / _surface.Zoom));
            _surface.InsertAt = null;
        }
        else
        {
            var slots = Stack().Select(s => new Reorder.Slot(s.Id, s.Bounds.Y, s.Bounds.Bottom)).ToList();
            var pointer = (int)Math.Round(_surface.ToCanvas(p).Y);
            var index = Reorder.IndexFor(slots, _pressed, pointer);
            _surface.Ghost = _downBounds.Offset(0, (int)Math.Round((p.Y - _downScreen.Y) / _surface.Zoom));
            _surface.InsertAt = InsertLineY(slots, _pressed, index);
        }
        Redraw();
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (_model is null || _pressed is null) { Reset(); return; }
        var p = e.GetPosition(_surface);

        if (_dragging)
        {
            if (IsUnlocked(_pressed))
            {
                var dx = (int)Math.Round((p.X - _downScreen.X) / _surface.Zoom);
                var dy = (int)Math.Round((p.Y - _downScreen.Y) / _surface.Zoom);
                if (dx != 0 || dy != 0) Moved?.Invoke(_pressed, dx, dy);
            }
            else
            {
                var slots = Stack().Select(s => new Reorder.Slot(s.Id, s.Bounds.Y, s.Bounds.Bottom)).ToList();
                var index = Reorder.IndexFor(slots, _pressed, (int)Math.Round(_surface.ToCanvas(p).Y));
                Reordered?.Invoke(_pressed, index);
            }
        }
        Reset();
    }

    private void Reset()
    {
        _pressed = null; _dragging = false;
        _surface.Ghost = null; _surface.InsertAt = null;
        ReleaseMouseCapture();
        Redraw();
    }

    /// <summary>The canvas y of the line that says "it will go here": the top of the widget that
    /// will follow it, or the bottom of the stack.</summary>
    private static int? InsertLineY(IReadOnlyList<Reorder.Slot> slots, string dragged, int index)
    {
        var rest = slots.Where(s => !string.Equals(s.Id, dragged, StringComparison.Ordinal)).ToList();
        if (rest.Count == 0) return null;
        return index < rest.Count ? rest[index].Top - Arranger.Gap / 2 : rest[^1].Bottom + Arranger.Gap / 2;
    }

    // ---- painting -------------------------------------------------------------------------------

    private void Redraw()
    {
        _surface.CanvasW = _model?.Signature.Width ?? 0;
        _surface.CanvasH = _model?.Signature.Height ?? 0;
        _surface.Column = _model is null ? default : Arranger.Column(_model.Signature.Width, _model.Signature.Height);
        _surface.Empty = _model is not null && Stack().Count == 0;
        var selected = SelectedInstance();
        _surface.Selected = selected is null || _model is null ? null : WidgetInstance.Bounds(_model.Layout, selected);
        _surface.Hover = _hover is null || _model is null || _hover == selected ? null : WidgetInstance.Bounds(_model.Layout, _hover);
        _surface.InvalidateVisual();
    }

    /// <summary>The drawing. Kept as one element rather than a visual tree of shapes: everything on
    /// it is derived from the model on every change, and rebuilding forty Rectangles per mouse move
    /// to say the same thing would be slower and no clearer.</summary>
    private sealed class Surface : FrameworkElement
    {
        public WriteableBitmap? Bitmap { get; set; }
        public double Zoom { get; set; } = 1;
        public Point Origin { get; set; }
        public int CanvasW { get; set; }
        public int CanvasH { get; set; }
        public CRect Column { get; set; }

        /// <summary>In the 1:1 view, the strip of canvas worth looking at. Everything outside it is
        /// dimmed: at 1:1 a 172 px column sits in six hundred pixels of photograph, and without this
        /// the eye has no idea which part of the picture the app is about.</summary>
        public CRect? Band { get; set; }
        public CRect? Selected { get; set; }
        public CRect? Hover { get; set; }
        public CRect? Ghost { get; set; }
        public int? InsertAt { get; set; }
        public bool Empty { get; set; }

        public Point ToCanvas(Point screen) => new((screen.X - Origin.X) / Zoom, (screen.Y - Origin.Y) / Zoom);

        public Rect ToScreen(CRect r) => new(Origin.X + r.X * Zoom, Origin.Y + r.Y * Zoom, r.W * Zoom, r.H * Zoom);

        private Brush Themed(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            if (CanvasW <= 0 || CanvasH <= 0) return;

            var frame = ToScreen(new CRect(0, 0, CanvasW, CanvasH));
            dc.DrawRectangle(Themed("SolidBackgroundFillColorTertiaryBrush", Brushes.DimGray), null, frame);
            if (Bitmap is not null) dc.DrawImage(Bitmap, frame);

            if (Band is { } band)
            {
                var scrim = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0));
                var strip = ToScreen(band);
                dc.DrawRectangle(scrim, null, new Rect(frame.X, frame.Y, Math.Max(0, strip.X - frame.X), frame.Height));
                dc.DrawRectangle(scrim, null, new Rect(strip.Right, frame.Y, Math.Max(0, frame.Right - strip.Right), frame.Height));
            }

            var accent = Themed("AccentFillColorDefaultBrush", Brushes.DodgerBlue);
            var column = ToScreen(Column);

            // The column, always: it is the only part of a 3440-wide photograph this app is about.
            var columnPen = new Pen(new SolidColorBrush(Color.FromArgb(Empty ? (byte)0 : (byte)70, 255, 255, 255)), 1);
            if (!Empty) dc.DrawRoundedRectangle(null, columnPen, column, 6, 6);

            if (Empty)
            {
                var dashed = new Pen(new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)), 1.5)
                { DashStyle = new DashStyle([5, 4], 0) };
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)), dashed, column, 8, 8);

                // Beside the column, not inside it: the column is 172 px of a 3440-wide photo, which
                // at fit-to-window is forty pixels across and will not hold a sentence.
                var text = new FormattedText("Add a widget from the list to start.",
                    CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), 15, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip)
                { TextAlignment = TextAlignment.Center, MaxTextWidth = 260 };
                var at = new Point(Math.Max(frame.X + 8, column.X - 16 - text.Width),
                                   column.Y + Math.Max(0, (column.Height - text.Height) / 2));
                var plate = new Rect(at.X - 12, at.Y - 8, text.Width + 24, text.Height + 16);
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)), null, plate, 6, 6);
                dc.DrawText(text, at);
            }

            if (Hover is { } h) dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)), 1), Inflate(ToScreen(h), 3), 4, 4);
            if (Selected is { } s) dc.DrawRoundedRectangle(null, new Pen(accent, 2), Inflate(ToScreen(s), 3), 4, 4);
            if (Ghost is { } g) dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), new Pen(accent, 1), Inflate(ToScreen(g), 3), 4, 4);
            if (InsertAt is { } y)
            {
                var line = Origin.Y + y * Zoom;
                dc.DrawLine(new Pen(accent, 3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round },
                    new Point(column.X, line), new Point(column.Right, line));
            }
        }

        private static Rect Inflate(Rect r, double by)
            => new(r.X - by, r.Y - by, Math.Max(0, r.Width + by * 2), Math.Max(0, r.Height + by * 2));
    }
}
