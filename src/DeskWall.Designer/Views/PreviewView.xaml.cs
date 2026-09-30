using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DeskWall.Core.Layout;
using DeskWall.Core.Widgets;
using DeskWall.Designer.Model;
using DeskWall.Designer.Model.Widgets;
using CRect = DeskWall.Core.Rect;

namespace DeskWall.Designer.Views;

/// <summary>
/// The wallpaper, as it will be, with everything on it free to be picked up, moved, lined up and
/// scaled where it stands.
/// <para>
/// Job: let the owner put a widget exactly where he wants it on a picture of his own desktop, and
/// see the answer at once. Nothing on this canvas decides a position for him any more: the old
/// fixed right-hand column has gone, along with the reorder drag and the per-widget unlock switch
/// that existed only to escape it. What is left is direct manipulation - click, drag, rubber-band,
/// align, nudge, scale - and the gallery's landing spot for a brand new widget, which is a hint
/// and not a constraint.
/// </para>
/// <para>
/// It still shows no coordinates, no rulers and no numbers: the canvas is a photograph, and every
/// pixel of chrome over it has to earn its place. The grid is off until asked for, the align strip
/// exists only while two or more things are selected, the grips only while something is, and the
/// smart guides only while a drag is snapping to one.
/// </para>
/// <para>
/// Depth (plan Task 3.4): at layout depth a copy is one thing; double-click it and the canvas goes to
/// copy depth, zoomed to fit it, where its parts are picked, moved and scaled one by one (every change
/// an override); double-click a part there and it goes to widget depth, where the rest of the layout
/// is dimmed and every copy follows. Double-clicking empty wallpaper outside the widget climbs back.
/// Zoom (Task 3.1) is continuous from 1/8 to 16, and above 1 the renderer draws the pane at the zoom
/// rather than this view upscaling a bitmap.
/// </para>
/// </summary>
public partial class PreviewView : UserControl
{
    /// <summary>Hold this while dragging and the gesture lands on the grid. One named constant, used
    /// by moves and by resizes alike.</summary>
    public const ModifierKeys SnapModifier = ModifierKeys.Shift;

    /// <summary>Hold this while dragging and the smart guides let go: the gesture is pixel-exact.
    /// Smart guides are on by default (brief section 3, as in Figma).</summary>
    public const ModifierKeys GuidesOffModifier = ModifierKeys.Alt;

    /// <summary>Hold this and a click adds to or takes away from the selection instead of
    /// replacing it. Not Shift: Shift already means "snap", and a Shift-press that is half a click
    /// and half the start of a drag would have to mean both things at once.</summary>
    public const ModifierKeys AddToSelectionModifier = ModifierKeys.Control;

    /// <summary>Held with an arrow key, one press moves the selection <see cref="CoarseNudge"/>
    /// pixels instead of one. Control, which cannot collide with the snap modifier because one is
    /// a modifier on a click and the other on a key.</summary>
    public const ModifierKeys CoarseNudgeModifier = ModifierKeys.Control;

    /// <summary>Pixels per press with <see cref="CoarseNudgeModifier"/> held.</summary>
    public const int CoarseNudge = 10;

    /// <summary>The most a copy is zoomed to when the canvas goes to its depth, or Shift+2 fits a
    /// selection: an 80 px dial at 16x would fill a monitor with one arc.</summary>
    public const double FitCap = 8;

    /// <summary>Screen pixels of travel before a press becomes a drag rather than a click. Below
    /// this a hand that wobbles would move a widget it only meant to select.</summary>
    private const double DragSlop = 4;

    /// <summary>The grips, in screen pixels. Screen, not canvas: a grip that scaled with the zoom
    /// would be a third of a pixel across at fit-to-window on a 3440-wide wallpaper.</summary>
    private const double HandleSize = 8;

    /// <summary>How near a press has to be to a grip's centre, in screen pixels, to be that grip
    /// rather than the start of a move. A little larger than the grip itself, because the grip is
    /// small on purpose and the pointer is not.</summary>
    private const double HandleReach = 9;

    /// <summary>Outline inflation, in screen pixels: the selection and hover outlines sit this far
    /// outside what they are round, and the grips sit on that outline.</summary>
    private const double OutlineInset = 3;

    /// <summary>The closest two drawn grid lines are allowed to get, in screen pixels, before the
    /// grid is drawn at a coarser multiple instead. Below it a grid is a grey haze, not a grid.</summary>
    private const double MinGridPixels = 6;

    /// <summary>Ctrl+wheel: the zoom factor per notch.</summary>
    private const double WheelZoomStep = 1.2;

    private static readonly Handle[] Corners =
        [Handle.TopLeft, Handle.TopRight, Handle.BottomRight, Handle.BottomLeft];

    private readonly List<Point> _penPoints = [];
    private bool _tracing;
    private void DrawingMode_Click(object sender, RoutedEventArgs e)
    {
        if (_model?.Depth.Kind != DepthKind.Layout)
        {
            PenToggle.IsChecked = TraceToggle.IsChecked = false;
            DrawingHint.Text = "Return to the layout to draw an outline.";
            DrawingHint.Visibility = Visibility.Visible; return;
        }
        if (sender == PenToggle) TraceToggle.IsChecked = false; else PenToggle.IsChecked = false;
        _penPoints.Clear(); _surface.PenPoints = []; _surface.BandRect = null;
        DrawingHint.Text = PenToggle.IsChecked == true ? "Click points. Enter: outline. Double-click: fill. Escape: cancel."
            : "Drag a band around the skyline. Escape cancels.";
        DrawingHint.Visibility = PenToggle.IsChecked == true || TraceToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        Focus(); Redraw();
    }
    private void FinishPen(bool closed)
    {
        if (_penPoints.Count < (closed ? 3 : 2)) return;
        AddOutline(DeskWall.Core.Render.SkylineTrace.FromPoints(_penPoints.Select(p => (p.X, p.Y)).ToArray(), closed));
        CancelDrawing();
    }
    private void AddOutline(BarDef part)
    {
        if (_model is null || _model.Depth.Kind != DepthKind.Layout) return;
        _model.Edit("Draw outline", layout =>
        {
            var id = "outline";
            for (var n = 2; layout.Components.Any(c => c.Id == id); n++) id = "outline-" + n;
            part.Id = id; part.Z = layout.Components.Select(c => c.Z).DefaultIfEmpty(0).Max() + 1;
            layout.Components.Add(part);
        });
    }
    private void CancelDrawing()
    {
        _tracing = false; _penPoints.Clear(); _surface.PenPoints = []; _surface.BandRect = null;
        PenToggle.IsChecked = TraceToggle.IsChecked = false; DrawingHint.Visibility = Visibility.Collapsed;
        ReleaseMouseCapture(); Redraw();
    }
    private void FinishTrace(Point end)
    {
        if (_model is null) return;
        var band = CanvasRect(_downScreen, end);
        try
        {
            // The photo the preview is showing: a bound base follows the live tree, so trace that one.
            var photoPath = _frame?.BaseImage is { Length: > 0 } shown ? shown
                : DeskWall.Core.Resolve.LayoutResolver.BaseImagePath(_model.Layout, DeskWall.Core.Values.ValueTree.Empty);
            using var photo = DeskWall.Core.Render.Surface.Load(photoPath);
            using var canvas = DeskWall.Core.Render.Surface.Create(_model.Signature.Width, _model.Signature.Height);
            canvas.DrawSurface(photo, CanvasRect(), _model.Layout.BaseFit);
            AddOutline(DeskWall.Core.Render.SkylineTrace.Trace(canvas, band));
            CancelDrawing();
        }
        catch (Exception ex)
        {
            CancelDrawing(); DrawingHint.Text = "Trace could not finish: " + ex.Message; DrawingHint.Visibility = Visibility.Visible;
        }
    }

    private readonly Surface _surface = new();
    private readonly AlignBar _alignBar = new();

    private DesignerModel? _model;
    private PreviewRenderer? _renderer;
    private WriteableBitmap? _bitmap;
    private PreviewFrame? _frame;
    private IReadOnlyList<Target>? _targets;

    private Viewport _view = new(1, 0, 0, 0, 0);
    private bool _fitted = true;          // the view is "fit all", and follows the pane when it resizes
    private (Viewport View, bool Fitted)? _layoutView;   // the layout-depth view, while a copy or widget is open
    private int _spacing = Placement.DefaultSpacing;
    private string? _hover;

    // The gesture in progress. Exactly one of _resizing, _moving and _banding is ever set.
    private Point _downScreen;
    private bool _dragging;
    private bool _moving;
    private bool _banding;
    private Handle? _resizing;
    private CRect _gestureBox;                          // the selection's bounds when the press landed
    private IReadOnlyList<Target> _gestureTargets = [];
    private IReadOnlyList<string> _bandBase = [];       // the selection a Ctrl-band adds to

    public PreviewView()
    {
        InitializeComponent();
        Host.Child = _surface;
        _surface.SizeChanged += (_, _) => { PlaceView(); Redraw(); };

        _alignBar.HorizontalAlignment = HorizontalAlignment.Center;
        _alignBar.VerticalAlignment = VerticalAlignment.Top;
        _alignBar.Margin = new Thickness(16);
        _alignBar.Visibility = Visibility.Collapsed;
        _alignBar.Aligned += AlignSelection;
        _alignBar.Distributed += DistributeSelection;
        Root.Children.Add(_alignBar);

        foreach (var spacing in Placement.Spacings)
            GridSpacing.Items.Add(new ComboBoxItem
            {
                Content = string.Format(CultureInfo.CurrentCulture, "{0} px", spacing),
                Tag = spacing,
            });
        GridSpacing.SelectedIndex = Math.Max(0, Placement.Spacings.ToList().IndexOf(Placement.DefaultSpacing));
    }


    /// <summary>Where the canvas is looking. Changes with every zoom and pan.</summary>
    public Viewport View => _view;

    /// <summary>Raised after the zoom or pan changes (for a zoom readout elsewhere in the shell).</summary>
    public event Action? ViewChanged;

    /// <summary>The canvas wants to change depth: a double-click on a copy (to
    /// <see cref="DepthKind.Copy"/>), or on empty wallpaper outside the open widget (to
    /// <see cref="Depth.Layout"/>). With no subscriber the canvas goes there itself
    /// (<see cref="GoToDepth"/>); the shell subscribes when it wants to route depth changes through
    /// its own navigation, and then calls <see cref="GoToDepth"/> (or <c>SetDepth</c>) itself.</summary>
    public event Action<Depth>? DepthRequested;

    /// <summary>The canvas wants to edit a copy's widget: a double-click on a part at copy depth.
    /// The argument is the copy's id. With no subscriber the canvas goes to widget depth itself.</summary>
    public event Action<string>? EditWidgetRequested;

    public void Attach(DesignerModel model, PreviewRenderer renderer)
    {
        if (_model is not null)
        {
            _model.Changed -= OnModelChanged;
            _model.SelectionChanged -= OnSelectionChanged;
            _model.DepthChanged -= OnDepthChanged;
            _model.Previewed -= OnPreviewed;
        }
        if (_renderer is not null) _renderer.Rendered -= OnRendered;
        _model = model;
        _renderer = renderer;
        _model.Changed += OnModelChanged;
        _model.SelectionChanged += OnSelectionChanged;
        _model.DepthChanged += OnDepthChanged;
        _model.Previewed += OnPreviewed;
        _renderer.Rendered += OnRendered;
        _targets = null;
        _fitted = true;
        _layoutView = null;
        PlaceView();
        RequestFrame();
        Redraw();
    }

    // ---- model and frame -----------------------------------------------------------------------

    private void OnModelChanged()
    {
        // Someone else changed the document (an undo, a knob) while a drop's options were open: the
        // drop stays as it is and its popover goes, so a later pick cannot undo their change.
        if (_inserting) _insertChanged = true;
        else ClosePopover();
        _targets = null;
        RequestFrame();
        Redraw();
    }

    private void OnSelectionChanged() => Redraw();

    /// <summary>A render-only edit (a colour being dragged): draw it, nothing else.</summary>
    private void OnPreviewed()
    {
        _targets = null;
        RequestFrame();
    }

    private void RequestFrame()
    {
        if (_model is null || _renderer is null) return;
        // ponytail: the viewport is in DIPs and rendered one pixel per DIP, which is exact at 100%
        // scaling (both of the owner's displays). At 125%+ WPF upscales the frame a little; pass the
        // DPI scale into the zoom and the bitmap's DPI if a scaled display ever needs it.
        _renderer.Request(_model, _view.Zoom > 1 ? _view : null);
    }

    private void OnRendered(PreviewFrame frame)
    {
        _frame = frame;
        if (_bitmap is null || _bitmap.PixelWidth != frame.Width || _bitmap.PixelHeight != frame.Height)
        {
            // Pbgra32, not Bgra32: Core's surface is already premultiplied, so this is a straight copy.
            _bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Pbgra32, null);
            _surface.Bitmap = _bitmap;
        }
        _bitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.Bgra, frame.Width * 4, 0);
        _surface.FrameView = frame.View;
        Redraw();
    }

    // ---- what is on the canvas ------------------------------------------------------------------

    /// <summary>What a click can land on at the current depth: the layout's copies and loose
    /// components at layout depth; the open copy's (or widget's) parts, one target each, deeper.</summary>
    private IReadOnlyList<Target> AllTargets()
        => _targets ??= _model is null ? []
            : _model.Depth.Kind == DepthKind.Layout ? Targets.All(_model) : Targets.All(_model.Parts);

    private IReadOnlyList<Target> SelectedTargets()
    {
        if (_model is null || _model.Selection.Count == 0) return [];
        var wanted = _model.Selection.ToHashSet(StringComparer.Ordinal);
        return AllTargets().Where(t => wanted.Contains(t.Id) || t.ComponentIds.Any(wanted.Contains)).ToList();
    }

    private void SelectTargets(IEnumerable<Target> targets)
        => _model?.Select(targets.SelectMany(Ids).Distinct(StringComparer.Ordinal).ToList());

    /// <summary>What the model's verbs take for a target: a copy's own id (<see cref="Targets.EditIds"/>).</summary>
    private IReadOnlyList<string> Ids(Target t) => _model is null ? t.ComponentIds : Targets.EditIds(_model.Layout, t);

    /// <summary>The copy open at copy depth, or whose origin widget depth is shown at.</summary>
    private WidgetCopy? OpenCopy()
        => _model is { Depth.CopyId: { } id } ? Copies.Find(_model.Layout, id) : null;

    /// <summary>The open widget's frame on the canvas: its copy's origin and the widget's size, at
    /// copy and widget depth. What smart guides snap a part to besides its siblings.</summary>
    private CRect? WidgetFrame()
    {
        if (_model is null || OpenCopy() is not { } copy) return null;
        return Copies.TryFind(_model.Finder(), copy.Widget) is { } t
            ? new CRect(copy.X, copy.Y, t.Width, t.Height)
            : null;
    }

    /// <summary>Everything the open widget covers: its frame and its parts. What depth zooms to and
    /// what widget depth leaves undimmed.</summary>
    private CRect? OpenBounds()
    {
        if (_model is null || OpenCopy() is not { } copy) return null;
        var parts = Copies.Bounds(copy, _model.Expanded(), _model.Finder());
        return WidgetFrame() is { } frame ? Placement.Bounds([parts, frame]) : parts;
    }

    /// <summary>Where a widget added from the gallery lands, and - while the layout is empty - the
    /// only thing drawn on the canvas besides the photograph. Still the right-hand margin, because
    /// that is the part of the wallpaper windows leave visible (CLAUDE.md); it is a hint about the
    /// next widget, not a box anything has to stay in.</summary>
    private CRect SpawnRegion()
        => _model is null ? default : Arranger.Column(_model.Signature.Width, _model.Signature.Height);

    private CRect CanvasRect() => _model is null ? default : new CRect(0, 0, _model.Signature.Width, _model.Signature.Height);

    // ---- zoom and pan (plan Task 3.1) --------------------------------------------------------------

    /// <summary>Fit the whole wallpaper in the pane (Shift+1). Keeps fitting as the pane resizes.</summary>
    public void FitAll() => SetView(_view.Fit(CanvasRect()), fitted: true);

    /// <summary>Fit the selection in the pane (Shift+2), else the open widget, else the whole
    /// wallpaper. Capped at <see cref="FitCap"/>.</summary>
    public void FitSelection()
    {
        var selected = SelectedTargets();
        if (selected.Count > 0) SetView(_view.Fit(Placement.Bounds(selected.Select(t => t.Bounds)), FitCap));
        else if (OpenBounds() is { } open) SetView(_view.Fit(open, FitCap));
        else FitAll();
    }

    /// <summary>One step in (Ctrl+plus) or out (Ctrl+minus) of <see cref="Viewport.Steps"/>, about
    /// the pane's centre.</summary>
    public void ZoomIn() => SetView(_view.Step(zoomIn: true));

    public void ZoomOut() => SetView(_view.Step(zoomIn: false));

    /// <summary>An exact zoom (1 is 100%, Ctrl+0), about the pane's centre.</summary>
    public void ZoomTo(double zoom) => SetView(_view.ZoomAbout(zoom, _view.Width / 2, _view.Height / 2));

    /// <summary>Go to <paramref name="depth"/> as the canvas does on a double-click: set it on the
    /// model, which zooms the canvas to the open copy (or back to the layout view it left).</summary>
    public void GoToDepth(Depth depth)
    {
        ArgumentNullException.ThrowIfNull(depth);
        _model?.SetDepth(depth);
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => ZoomIn();

    private void ZoomOut_Click(object sender, RoutedEventArgs e) => ZoomOut();

    private void ZoomLabel_Click(object sender, RoutedEventArgs e) => FitAll();

    /// <summary>The one way the view changes: clamp so some of the canvas stays in the pane, round
    /// the origin to whole pixels (a viewport frame drawn at a fractional offset would be resampled,
    /// which is the blur rendering at the zoom exists to avoid), and ask for a frame when the renderer
    /// is drawing viewports (above 1) or was until now.</summary>
    private void SetView(Viewport view, bool fitted = false)
    {
        if (_model is null || view.Width <= 0 || view.Height <= 0) return;
        var clamped = view.Clamp(_model.Signature.Width, _model.Signature.Height);
        _view = clamped with { OriginX = Math.Round(clamped.OriginX), OriginY = Math.Round(clamped.OriginY) };
        _fitted = fitted;
        _surface.Zoom = _view.Zoom;
        _surface.Origin = new Point(_view.OriginX, _view.OriginY);
        ZoomLabel.Content = string.Format(CultureInfo.CurrentCulture, "{0:0}%", _view.Zoom * 100);
        if (_view.Zoom > 1 || _frame?.View is not null) RequestFrame();
        ViewChanged?.Invoke();
        Redraw();
    }

    /// <summary>The pane changed size (or the view is new): refit when fitted, otherwise keep what
    /// is at the centre where it is.</summary>
    private void PlaceView()
    {
        if (_model is null) return;
        double vw = _surface.ActualWidth, vh = _surface.ActualHeight;
        if (vw <= 0 || vh <= 0 || _model.Signature.Width <= 0 || _model.Signature.Height <= 0) return;
        var sized = _view.Width <= 0 ? new Viewport(1, 0, 0, vw, vh) : _view.Resized(vw, vh);
        if (_fitted) SetView(sized.Fit(CanvasRect()), fitted: true);
        else SetView(sized);
    }

    /// <summary>Ctrl+wheel zooms about the pointer. The plain wheel pans the canvas when it is larger
    /// than the pane: vertically, or horizontally with Shift held.</summary>
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_model is null) return;
        var p = e.GetPosition(_surface);
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            SetView(_view.ZoomAbout(_view.Zoom * Math.Pow(WheelZoomStep, e.Delta / 120.0), p.X, p.Y));
            e.Handled = true;
            return;
        }
        var by = e.Delta * 0.6;
        var sideways = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        var extent = sideways ? _model.Signature.Width * _view.Zoom : _model.Signature.Height * _view.Zoom;
        if (extent <= (sideways ? _view.Width : _view.Height)) return;
        SetView(sideways ? _view.Pan(by, 0) : _view.Pan(0, by));
        e.Handled = true;
    }

    // ---- depth (plan Task 3.4) -----------------------------------------------------------------------

    /// <summary>Going into a copy or widget zooms to it, and remembers the layout view; coming back
    /// to layout depth puts that view back. Whatever changed the depth: this canvas, the shell's
    /// keys, or the knobs panel's Details.</summary>
    private void OnDepthChanged()
    {
        ClosePopover();
        CommitTextEdit();
        _targets = null;
        if (_model is null) return;
        if (_model.Depth.Kind == DepthKind.Layout)
        {
            if (_layoutView is { } saved)
            {
                _layoutView = null;
                SetView(saved.Fitted ? saved.View.Fit(CanvasRect()) : saved.View, saved.Fitted);
            }
        }
        else
        {
            _layoutView ??= (_view, _fitted);
            if (OpenBounds() is { } open) SetView(_view.Fit(open, FitCap));
        }
        Redraw();
    }

    private void RequestDepth(Depth depth)
    {
        if (DepthRequested is { } handler) handler(depth);
        else GoToDepth(depth);
    }

    /// <summary>The second press of a double-click, which the first press has already treated as a
    /// select: go down into what it landed on, or up out of the open widget when it landed outside it.</summary>
    private void OnDoubleClick(Point screen)
    {
        if (_model is null) return;
        var (cx, cy) = _view.ToCanvas(screen.X, screen.Y);
        var hit = Targets.Hit(AllTargets(), cx, cy);
        switch (_model.Depth.Kind)
        {
            case DepthKind.Layout when hit is { IsWidget: true } && Copies.Find(_model.Layout, hit.Id) is { } copy:
                RequestDepth(Depth.Copy(copy.Id, copy.Widget));
                // Figma's instance double-click: the part under the pointer is what gets selected.
                if (_model.Depth.Kind == DepthKind.Copy && Targets.Hit(AllTargets(), cx, cy) is { } part) SelectTargets([part]);
                break;
            case DepthKind.Copy when hit is not null && OpenCopy() is { } open:
                if (EditWidgetRequested is { } handler) handler(open.Id);
                else GoToDepth(Depth.Widget(open.Widget, open.Id));
                break;
            case DepthKind.Copy or DepthKind.Widget when hit is null && !Inside(OpenBounds(), cx, cy):
                var was = OpenCopy()?.Id;
                RequestDepth(Depth.Layout);
                if (was is not null && _model.Depth.Kind == DepthKind.Layout) _model.Select([was]);
                break;
        }
    }

    private static bool Inside(CRect? r, double x, double y)
        => r is { } b && x >= b.X && x < b.Right && y >= b.Y && y < b.Bottom;

    // ---- the grid ---------------------------------------------------------------------------------

    private void GridToggle_Click(object sender, RoutedEventArgs e) => Redraw();

    private void GridSpacing_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (GridSpacing.SelectedItem is ComboBoxItem { Tag: int spacing }) _spacing = spacing;
        Redraw();
    }

    /// <summary>The spacing a snapped gesture rounds to, or null when the modifier is not held.
    /// Deliberately not "is the grid drawn": drawing the grid and snapping to it are two
    /// questions, and someone who knows where the lines are should not have to look at them.</summary>
    private int? SnapTo() => (Keyboard.Modifiers & SnapModifier) != 0 ? _spacing : null;

    // ---- smart guides (plan Task 3.2) -------------------------------------------------------------

    /// <summary>Whether this gesture snaps to guides: not while the grid has it (Shift), and not
    /// while Alt is held.</summary>
    private static bool GuidesOn()
        => (Keyboard.Modifiers & (SnapModifier | GuidesOffModifier)) == 0;

    /// <summary><see cref="Snap.Threshold"/> screen pixels, in canvas pixels at this zoom.</summary>
    private int GuideThreshold() => (int)Math.Round(Snap.Threshold / Math.Max(Viewport.MinZoom, _view.Zoom));

    /// <summary>What a gesture snaps to: every target at this depth that is not being dragged.</summary>
    private IReadOnlyList<CRect> Siblings()
    {
        var moving = _gestureTargets.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        return AllTargets().Where(t => !moving.Contains(t.Id)).Select(t => t.Bounds).ToList();
    }

    /// <summary>The frame whose edges a gesture snaps to: the wallpaper at layout depth, the widget's
    /// frame deeper.</summary>
    private CRect GuideCanvas() => WidgetFrame() ?? CanvasRect();

    // ---- grips -------------------------------------------------------------------------------------

    /// <summary>The selection's box on screen, outline and all - what the grips sit on.</summary>
    private Rect? SelectionScreenBox()
    {
        var targets = SelectedTargets();
        if (targets.Count == 0) return null;
        var box = Placement.Bounds(targets.Select(t => t.Bounds));
        if (box.W <= 0 || box.H <= 0) return null;
        return Inflate(_surface.ToScreen(box), OutlineInset);
    }

    /// <summary>Which grips are worth showing at this zoom. All eight round a widget shrunk to
    /// forty screen pixels would be a row of touching squares with no box left between them, so
    /// the edge grips appear only when their side is long enough to hold one clear of the corners.
    /// A box thinner than about twice a grip (a bar, a few pixels tall at any zoom that shows the
    /// layout) has no corners, but keeps the grips at its two ends, so its length still scales on
    /// the canvas; its thickness is the properties panel's, since a grip across it would sit on the
    /// bar and take the move. A box small both ways gets none and has to be zoomed in on.</summary>
    private static IEnumerable<Handle> VisibleHandles(Rect box)
    {
        var wide = box.Width >= HandleSize * 2;
        var tall = box.Height >= HandleSize * 2;
        if (wide && tall) foreach (var corner in Corners) yield return corner;
        if (tall && (box.Width >= HandleSize * 5 || !wide)) { yield return Handle.Top; yield return Handle.Bottom; }
        if (wide && (box.Height >= HandleSize * 5 || !tall)) { yield return Handle.Left; yield return Handle.Right; }
    }

    private static Point HandleCentre(Rect box, Handle handle) => handle switch
    {
        Handle.TopLeft => new Point(box.Left, box.Top),
        Handle.Top => new Point(box.Left + box.Width / 2, box.Top),
        Handle.TopRight => new Point(box.Right, box.Top),
        Handle.Right => new Point(box.Right, box.Top + box.Height / 2),
        Handle.BottomRight => new Point(box.Right, box.Bottom),
        Handle.Bottom => new Point(box.Left + box.Width / 2, box.Bottom),
        Handle.BottomLeft => new Point(box.Left, box.Bottom),
        _ => new Point(box.Left, box.Top + box.Height / 2),
    };

    /// <summary>The grip under a screen point, or null. Nearest centre wins, so grips that crowd
    /// each other when the box is small still resolve to one answer rather than to whichever
    /// happened to be tested first.</summary>
    private Handle? HandleUnder(Point screen)
    {
        if (SelectionScreenBox() is not { } box) return null;
        Handle? best = null;
        var bestDistance = HandleReach;
        foreach (var handle in VisibleHandles(box))
        {
            var centre = HandleCentre(box, handle);
            var distance = Math.Sqrt(Sq(centre.X - screen.X) + Sq(centre.Y - screen.Y));
            if (distance > bestDistance) continue;
            bestDistance = distance;
            best = handle;
        }
        return best;

        static double Sq(double v) => v * v;
    }

    private static Cursor CursorFor(Handle handle) => handle switch
    {
        Handle.TopLeft or Handle.BottomRight => Cursors.SizeNWSE,
        Handle.TopRight or Handle.BottomLeft => Cursors.SizeNESW,
        Handle.Top or Handle.Bottom => Cursors.SizeNS,
        _ => Cursors.SizeWE,
    };

    // ---- pointer ---------------------------------------------------------------------------------

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        ClosePopover();
        Focus();
        if (_model is null || e.ChangedButton != MouseButton.Left) return;
        _downScreen = e.GetPosition(_surface);
        e.Handled = true;
        if (PenToggle.IsChecked == true)
        {
            // The double-click's second press lands on the point its first press already added.
            if (e.ClickCount == 2) { FinishPen(true); return; }
            _penPoints.Add(_surface.ToCanvas(_downScreen)); _surface.PenPoints = _penPoints.ToArray();
            Redraw();
            return;
        }
        if (TraceToggle.IsChecked == true) { _tracing = true; CaptureMouse(); return; }
        if (e.ClickCount == 2)
        {
            Reset();
            OnDoubleClick(_downScreen);
            return;
        }
        BeginGesture(_downScreen);
    }

    /// <summary>A right-click on something not selected selects it first, so the canvas menu acts on
    /// what is under the pointer (Figma's rule). On empty wallpaper the selection stays.</summary>
    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        Focus();
        if (_model is null) return;
        var canvas = _surface.ToCanvas(e.GetPosition(_surface));
        if (Targets.Hit(AllTargets(), canvas.X, canvas.Y) is { } hit && !SelectedTargets().Any(t => t.Id == hit.Id))
            SelectTargets([hit]);
    }

    /// <summary>Where a menu opened from the keyboard goes: just below the selection's bottom-left, in
    /// this view's coordinates; null when nothing is selected.</summary>
    public Point? SelectionAnchor()
        => SelectionScreenBox() is { } box ? new Point(Math.Clamp(box.Left, 0, Math.Max(0, ActualWidth - 40)), Math.Clamp(box.Bottom + 4, 0, Math.Max(0, ActualHeight - 40))) : null;

    /// <summary>A press at <paramref name="screen"/>: a grip starts a resize, a target a move (and
    /// selects it), empty wallpaper a rubber band. Public for the harness and the shell, which drive
    /// the canvas without a real pointer; <see cref="DragTo"/> and <see cref="EndGesture"/> finish it.</summary>
    public void BeginGesture(Point screen)
    {
        if (_model is null) return;
        _downScreen = screen;
        _dragging = false;
        _moving = false;
        _banding = false;
        _resizing = null;

        // A grip first: it sits outside the thing it scales, so a press there would otherwise be
        // read as a press on whatever happens to be behind it.
        if (HandleUnder(_downScreen) is { } handle)
        {
            _resizing = handle;
            _gestureTargets = SelectedTargets();
            _gestureBox = Placement.Bounds(_gestureTargets.Select(t => t.Bounds));
            CaptureMouse();
            return;
        }

        var canvas = _surface.ToCanvas(_downScreen);
        var hit = Targets.Hit(AllTargets(), canvas.X, canvas.Y);
        var adding = (Keyboard.Modifiers & AddToSelectionModifier) != 0;

        // Empty wallpaper outside the open copy or widget climbs one depth, as a click outside an
        // instance does in Figma. A double-click there then finishes the climb (OnDoubleClick).
        if (hit is null && _model.Depth.Kind != DepthKind.Layout && !Inside(OpenBounds(), canvas.X, canvas.Y))
        {
            _model.Climb();
            return;
        }

        if (hit is null)
        {
            // Empty wallpaper: a rubber band. Ctrl keeps what is already selected, so a second
            // band can add to the first one's answer.
            _banding = true;
            _bandBase = adding ? _model.Selection.ToList() : [];
            if (!adding) _model.ClearSelection();
            CaptureMouse();
            return;
        }

        var selected = SelectedTargets();
        if (adding)
        {
            SelectTargets(selected.Any(t => t.Id == hit.Id)
                ? selected.Where(t => t.Id != hit.Id)
                : selected.Append(hit));
        }
        else if (!selected.Any(t => t.Id == hit.Id))
        {
            SelectTargets([hit]);
        }
        // Otherwise the press landed inside a selection that already holds it: leave the selection
        // alone, or clicking one of three selected widgets to drag the three would drop the other
        // two on the way.

        _moving = true;
        _gestureTargets = SelectedTargets();
        _gestureBox = Placement.Bounds(_gestureTargets.Select(t => t.Bounds));
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_model is null) return;
        var p = e.GetPosition(_surface);
        if (_tracing) { _surface.BandRect = CanvasRect(_downScreen, p); Redraw(); return; }

        if (e.LeftButton != MouseButtonState.Pressed || (!_moving && !_banding && _resizing is null))
        {
            Track(p);
            return;
        }
        DragTo(p);
    }

    /// <summary>The gesture in progress has reached <paramref name="p"/>: ghosts, the band and the
    /// smart guides follow it. Nothing is written until <see cref="EndGesture"/>.</summary>
    public void DragTo(Point p)
    {
        if (_model is null || (!_moving && !_banding && _resizing is null)) return;
        if (!_dragging
            && Math.Abs(p.X - _downScreen.X) < DragSlop
            && Math.Abs(p.Y - _downScreen.Y) < DragSlop) return;
        _dragging = true;

        if (_resizing is { } handle)
        {
            var to = ResizedBox(handle, p, out var guides);
            _surface.Ghosts = _gestureTargets.Select(t => Resize.Map(t.Bounds, _gestureBox, to)).ToList();
            _surface.GhostBox = to;
            _surface.Guides = guides;
        }
        else if (_moving)
        {
            var (dx, dy) = MoveOffset(p, out var guides);
            _surface.Ghosts = _gestureTargets.Select(t => t.Bounds.Offset(dx, dy)).ToList();
            _surface.GhostBox = _gestureBox.Offset(dx, dy);
            _surface.Guides = guides;
        }
        else
        {
            var band = CanvasRect(_downScreen, p);
            _surface.BandRect = band;
            var caught = Targets.Within(AllTargets(), band).Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
            var baseIds = _bandBase.ToHashSet(StringComparer.Ordinal);
            var keep = AllTargets().Where(t => baseIds.Contains(t.Id) || t.ComponentIds.Any(baseIds.Contains)).ToList();
            SelectTargets(keep.Concat(AllTargets().Where(t => caught.Contains(t.Id) && keep.All(k => k.Id != t.Id))));
        }
        Redraw();
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.ChangedButton != MouseButton.Left) return;
        if (_tracing) { FinishTrace(e.GetPosition(_surface)); e.Handled = true; return; }
        EndGesture(e.GetPosition(_surface));
    }

    /// <summary>Finish the gesture at <paramref name="p"/>: a move or a resize is written as one undo
    /// entry, snapped as it was drawn.</summary>
    public void EndGesture(Point p)
    {
        if (_model is not null && _dragging)
        {
            if (_resizing is { } handle)
            {
                var to = ResizedBox(handle, p, out _);
                var corner = Resize.IsCorner(handle);
                _model.Scale(corner ? "Scale" : "Resize",
                    _gestureTargets.SelectMany(Ids).ToList(), _gestureBox, to, corner);
            }
            else if (_moving)
            {
                var (dx, dy) = MoveOffset(p, out _);
                _model.MoveGroups(_gestureTargets.Count > 1 ? "Move widgets" : "Move", Offsets(_gestureTargets, dx, dy));
            }
        }
        Reset();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (IsMouseCaptured) return;
        Reset();
    }

    private void Track(Point p)
    {
        if (HandleUnder(p) is { } handle)
        {
            if (_hover is not null) { _hover = null; Redraw(); }
            Cursor = CursorFor(handle);
            return;
        }
        var canvas = _surface.ToCanvas(p);
        var hover = Targets.Hit(AllTargets(), canvas.X, canvas.Y)?.Id;
        Cursor = hover is null ? Cursors.Arrow : Cursors.SizeAll;
        if (hover == _hover) return;
        _hover = hover;
        Redraw();
    }

    /// <summary>The offset this move has reached: on the grid with Shift, onto the nearest sibling
    /// or frame line (<see cref="Snap.Apply"/>) by default, exact with Alt. Measured from where the
    /// press landed, so the thing being dragged stays under the pointer.</summary>
    private (int Dx, int Dy) MoveOffset(Point p, out IReadOnlyList<Snap.Guide> guides)
    {
        guides = [];
        var dx = (int)Math.Round((p.X - _downScreen.X) / _surface.Zoom);
        var dy = (int)Math.Round((p.Y - _downScreen.Y) / _surface.Zoom);
        if (SnapTo() is { } spacing) return Placement.DragOffset(_gestureBox, dx, dy, spacing);
        if (!GuidesOn()) return (dx, dy);
        var moving = _gestureBox.Offset(dx, dy);
        var (snapped, found) = Snap.Apply(moving, Siblings(), GuideCanvas(), GuideThreshold());
        guides = found;
        return (dx + snapped.X - moving.X, dy + snapped.Y - moving.Y);
    }

    private CRect ResizedBox(Handle handle, Point p, out IReadOnlyList<Snap.Guide> guides)
    {
        guides = [];
        var box = Resize.Box(_gestureBox, handle,
            (int)Math.Round((p.X - _downScreen.X) / _surface.Zoom),
            (int)Math.Round((p.Y - _downScreen.Y) / _surface.Zoom),
            SnapTo());
        if (!GuidesOn()) return box;
        var (snapped, found) = Snap.Resize(_gestureBox, box, handle, Siblings(), GuideCanvas(), GuideThreshold());
        guides = found;
        return snapped;
    }

    private CRect CanvasRect(Point a, Point b)
    {
        var p1 = _surface.ToCanvas(a);
        var p2 = _surface.ToCanvas(b);
        var x = (int)Math.Round(Math.Min(p1.X, p2.X));
        var y = (int)Math.Round(Math.Min(p1.Y, p2.Y));
        return new CRect(x, y, (int)Math.Round(Math.Abs(p2.X - p1.X)), (int)Math.Round(Math.Abs(p2.Y - p1.Y)));
    }

    private void Reset()
    {
        _dragging = false;
        _moving = false;
        _banding = false;
        _resizing = null;
        _gestureTargets = [];
        _bandBase = [];
        _surface.Ghosts = [];
        _surface.GhostBox = null;
        _surface.BandRect = null;
        _surface.Guides = [];
        ReleaseMouseCapture();
        Redraw();
    }

    // ---- inserting from the Insert panel (plan Task 4.4) --------------------------------------------

    /// <summary>A drop or an insert the canvas could not do, or the one thing worth knowing about one,
    /// in a sentence for the shell's status line.</summary>
    public event Action<string>? Status;

    private const string CopyCannotGainParts = "A copy cannot gain parts: Ctrl+Alt+K edits its widget.";

    private DropPlan? _plan;          // the drop whose options the popover shows
    private int _planIndex;           // which of them is on the canvas now
    private Border? _popover;
    private TextBox? _editor;         // typing into a text part just inserted
    private string? _editing;
    private bool _inserting;          // this canvas is editing the document itself
    private bool _insertChanged;      // ... and the edit changed it (one undo entry to take back)

    protected override void OnDragEnter(DragEventArgs e) { base.OnDragEnter(e); DragEffect(e); }

    protected override void OnDragOver(DragEventArgs e) { base.OnDragOver(e); DragEffect(e); }

    private void DragEffect(DragEventArgs e)
    {
        var at = e.GetPosition(_surface);
        var payload = e.Data.GetData(InsertPanel.DataFormat);
        e.Effects = payload is not null && CanDrop(payload, at) ? DragDropEffects.Copy : DragDropEffects.None;
        // A refused drop is never delivered (OLE sends DragLeave, not Drop), so the reason goes up
        // while the pointer is outside, once each time it leaves the frame.
        var outside = payload is not null && _model is not null && !InFrame(at);
        if (outside && !_saidOutside) Status?.Invoke(Insert.OutsideFrame);
        _saidOutside = outside;
        e.Handled = true;
    }

    private bool _saidOutside;

    private bool InFrame(Point screen)
    {
        var (cx, cy) = _view.ToCanvas(screen.X, screen.Y);
        return _model is null || Insert.InFrame(_model, cx, cy);
    }

    protected override void OnDrop(DragEventArgs e)
    {
        base.OnDrop(e);
        if (e.Data.GetData(InsertPanel.DataFormat) is not { } payload) return;
        DropAt(payload, e.GetPosition(_surface));
        e.Handled = true;
    }

    /// <summary>Whether <paramref name="payload"/> would do anything dropped at <paramref name="screen"/>
    /// (pane coordinates): a part anywhere but at copy depth, a widget at layout depth, a value
    /// wherever its <see cref="DropPlan"/> has an option.</summary>
    public bool CanDrop(object payload, Point screen)
    {
        if (_model is null) return false;
        return payload switch
        {
            PartKind => Insert.CanAddPart(_model) && InFrame(screen),
            WidgetTemplate => _model.Depth.Kind == DepthKind.Layout,
            ValueEntry value => Plan(value, screen).Options.Count > 0,
            _ => false,
        };
    }

    /// <summary>The keyboard's insert (Enter in the Insert panel): as if dropped at the middle of the
    /// pane, or at widget depth at the middle of the widget's frame, which is where it has to go.</summary>
    public bool InsertAtCentre(object payload)
    {
        if (_model is not null && Insert.Frame(_model) is { } frame)
        {
            var (sx, sy, sw, sh) = _view.ToScreen(frame);
            return DropAt(payload, new Point(sx + sw / 2, sy + sh / 2));
        }
        return DropAt(payload, new Point(_view.Width / 2, _view.Height / 2));
    }

    /// <summary>Put <paramref name="payload"/> (an <see cref="InsertPanel.DataFormat"/> payload) on the
    /// canvas at <paramref name="screen"/>, as one undo entry, and select it:
    /// <list type="bullet">
    /// <item>a part, centred there; a text part then takes what is typed (Enter keeps it, Esc leaves "Text");</item>
    /// <item>a copy of a widget, centred there (layout depth only);</item>
    /// <item>a value, through <see cref="DropPlan"/>: the default option goes on at once, and when
    /// there are others a popover lists them beside it. Picking another swaps it (still one entry),
    /// Enter keeps the default, Esc takes the drop back, and anything else done meanwhile keeps
    /// it.</item>
    /// </list>
    /// Public so the harness can drive a drop without a real pointer. Returns false when nothing was done.</summary>
    public bool DropAt(object payload, Point screen)
    {
        if (_model is null) return false;
        ClosePopover();
        CommitTextEdit();
        var (cx, cy) = _view.ToCanvas(screen.X, screen.Y);
        int x = (int)Math.Round(cx), y = (int)Math.Round(cy);
        switch (payload)
        {
            case WidgetTemplate template:
                if (Insert.Widget(_model, template, x, y) is not { } copy)
                {
                    Status?.Invoke("A widget goes on the layout: Esc climbs back to it.");
                    return false;
                }
                _model.Select([copy]);
                Focus();
                return true;

            case PartKind kind:
                if (Insert.Part(_model, kind, x, y) is not { } id)
                {
                    Status?.Invoke(Insert.InFrame(_model, cx, cy) ? CopyCannotGainParts : Insert.OutsideFrame);
                    return false;
                }
                _model.Select([id]);
                if (kind == PartKind.Text) BeginTextEdit(id);
                else Focus();
                return true;

            case ValueEntry value:
                var plan = Plan(value, screen);
                if (plan.Options.Count == 0)
                {
                    Status?.Invoke(!Insert.InFrame(_model, cx, cy) ? Insert.OutsideFrame
                        : _model.Depth.Kind == DepthKind.Copy ? CopyCannotGainParts : $"Nothing here can show {value.Label}.");
                    return false;
                }
                var applied = ApplyOption(plan, 0);
                if (plan.Options.Count > 1) OpenPopover(plan, applied);
                else Focus();
                return true;
        }
        return false;
    }

    private DropPlan Plan(ValueEntry value, Point screen)
    {
        var (cx, cy) = _view.ToCanvas(screen.X, screen.Y);
        return DropPlan.For(_model!, value, new DropTarget((int)Math.Round(cx), (int)Math.Round(cy), Insert.PartAt(_model!, cx, cy)));
    }

    /// <summary>Apply one option as the canvas's own edit, and select what it made or bound.</summary>
    private string ApplyOption(DropPlan plan, int index)
    {
        _insertChanged = false;
        string id;
        _inserting = true;
        try { id = plan.Apply(_model!, plan.Options[index]); }
        finally { _inserting = false; }
        _model!.Select([id]);
        // Every override a drop writes is said out loud: it changes this copy and nothing else.
        if (plan.Note is { } note) Status?.Invoke(note);
        return id;
    }

    /// <summary>Take back the option on the canvas, if it changed anything (a bind to what a copy
    /// already shows makes no undo entry, and then there is nothing to take back).</summary>
    private void TakeBack()
    {
        if (!_insertChanged || _model is null) return;
        _insertChanged = false;
        _inserting = true;
        try { _model.Undo(); }
        finally { _inserting = false; }
    }

    // ---- the options popover ---------------------------------------------------------------------

    private void OpenPopover(DropPlan plan, string appliedId)
    {
        _plan = plan;
        _planIndex = 0;
        var panel = new StackPanel { MinWidth = 150 };
        KeyboardNavigation.SetDirectionalNavigation(panel, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetTabNavigation(panel, KeyboardNavigationMode.Cycle);
        var title = new TextBlock { Text = plan.Value.Label, Margin = new Thickness(8, 4, 8, 6), FontSize = 12 };
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        panel.Children.Add(title);
        Button? first = null;
        for (var i = 0; i < plan.Options.Count; i++)
        {
            var index = i;
            var b = new Button
            {
                Content = LiveLabel(plan.Value, plan.Options[i]),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(10, 4, 10, 5),
                Margin = new Thickness(0, 0, 0, 2),
            };
            // The one on the canvas now is the accent: at first the default, which Enter keeps.
            if (i == 0) b.SetResourceReference(StyleProperty, "AccentButtonStyle");
            System.Windows.Automation.AutomationProperties.SetName(b, $"{plan.Value.Label} as {LiveLabel(plan.Value, plan.Options[i])}");
            b.Click += (_, _) => Pick(index);
            panel.Children.Add(b);
            first ??= b;
        }
        var popover = new Border
        {
            Child = panel,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(4),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        popover.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorBaseAltBrush");
        popover.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
        System.Windows.Automation.AutomationProperties.SetName(popover, $"Show {plan.Value.Label} as");
        popover.KeyDown += Popover_KeyDown;
        popover.IsKeyboardFocusWithinChanged += (_, e) => { if (e.NewValue is false) ClosePopover(); };
        _popover = popover;
        Root.Children.Add(popover);
        PlacePopover(popover, appliedId);
        InputManager.Current.PreProcessInput += OnPreProcessInput;
        first!.Loaded += (_, _) => first.Focus();
    }

    /// <summary>A format option on the value as it is now ("Show as 464 GB free"), not on the preset's
    /// fixed example ("64 GB free"), which reads as a wrong number next to a drive with 464 free.</summary>
    private static string LiveLabel(ValueEntry value, DropOption option)
    {
        foreach (var prefix in new[] { "Text ", "Show as " })
            if (option.Label.StartsWith(prefix, StringComparison.Ordinal) && option.Value.Binding is { } b)
                return prefix + value.Sample.ToText(b.Format);
        return option.Label;
    }

    /// <summary>Beside what the drop made or bound, on the right unless that runs off the pane.</summary>
    private void PlacePopover(Border popover, string id)
    {
        popover.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = popover.DesiredSize;
        var near = _model?.Find(id) is { } part ? _surface.ToScreen(part.Rect) : new Rect(_view.Width / 2, _view.Height / 2, 0, 0);
        var left = near.Right + 8;
        if (left + size.Width > _view.Width - 8) left = near.Left - size.Width - 8;
        var top = Math.Clamp(near.Top, 8, Math.Max(8, _view.Height - size.Height - 8));
        popover.Margin = new Thickness(Math.Max(8, left), top, 0, 0);
    }

    /// <summary>Another option: the one on the canvas is taken back and this one applied, still one
    /// undo entry. The default (or the one already on) just closes.</summary>
    private void Pick(int index)
    {
        if (_plan is not { } plan) return;
        if (index != _planIndex)
        {
            TakeBack();
            ApplyOption(plan, index);
        }
        ClosePopover();
        Focus();
    }

    /// <summary>Esc, and Tab kept inside: the window's own key handling runs before anything on the
    /// canvas sees a key (Esc climbs a depth, Tab picks a sibling), so while the popover has the
    /// focus those two are taken before they are routed at all.</summary>
    private void OnPreProcessInput(object sender, PreProcessInputEventArgs e)
    {
        if (_popover is not { IsKeyboardFocusWithin: true } popover
            || e.StagingItem.Input is not KeyEventArgs { RoutedEvent: var routed } key
            || routed != Keyboard.PreviewKeyDownEvent) return;
        if (key.Key == Key.Escape)
        {
            e.Cancel();
            Dispatcher.BeginInvoke(new Action(() =>
            {
                TakeBack();
                ClosePopover();
                Focus();
            }));
        }
        else if (key.Key == Key.Tab)
        {
            e.Cancel();
            var back = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            (Keyboard.FocusedElement as UIElement)?.MoveFocus(new TraversalRequest(back ? FocusNavigationDirection.Previous : FocusNavigationDirection.Next));
            if (!popover.IsKeyboardFocusWithin) popover.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        }
    }

    /// <summary>The arrow keys move between the options (they would otherwise nudge the selection).</summary>
    private void Popover_KeyDown(object sender, KeyEventArgs e)
    {
        var direction = e.Key switch
        {
            Key.Up or Key.Left => FocusNavigationDirection.Up,
            Key.Down or Key.Right => FocusNavigationDirection.Down,
            _ => (FocusNavigationDirection?)null,
        };
        if (direction is not { } d) return;
        (Keyboard.FocusedElement as UIElement)?.MoveFocus(new TraversalRequest(d));
        e.Handled = true;
    }

    /// <summary>Close the popover, keeping what is on the canvas.</summary>
    private void ClosePopover()
    {
        if (_popover is not { } popover) return;
        _popover = null;
        _plan = null;
        InputManager.Current.PreProcessInput -= OnPreProcessInput;
        Root.Children.Remove(popover);
    }

    /// <summary>The options open after the last drop, as their labels (the harness reads it).</summary>
    public IReadOnlyList<string> OpenOptions => _popover is null || _plan is null ? [] : _plan.Options.Select(o => LiveLabel(_plan.Value, o)).ToList();

    // ---- typing into a new text part -------------------------------------------------------------

    private void BeginTextEdit(string id)
    {
        if (_model?.Find(id) is not TextDef part) return;
        var box = _surface.ToScreen(part.Rect);
        var size = double.TryParse(part.Size.LiteralText, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : 16;
        var editor = new TextBox
        {
            Text = part.Text.LiteralText ?? "",
            FontSize = Math.Clamp(size * _view.Zoom, 12, 48),
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Width = Math.Max(120, box.Width),
            Margin = new Thickness(Math.Max(0, box.Left + box.Width / 2 - Math.Max(120, box.Width) / 2), Math.Max(0, box.Top), 0, 0),
        };
        System.Windows.Automation.AutomationProperties.SetName(editor, "Text of the new part");
        editor.ToolTip = "Enter keeps it, Esc leaves it as it is";
        editor.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { CommitTextEdit(); Focus(); e.Handled = true; }
            else if (e.Key == Key.Escape) { CloseTextEdit(); Focus(); e.Handled = true; }
        };
        editor.LostKeyboardFocus += (_, _) => CommitTextEdit();
        _editor = editor;
        _editing = id;
        Root.Children.Add(editor);
        editor.Loaded += (_, _) => { editor.Focus(); editor.SelectAll(); };
    }

    private void CommitTextEdit()
    {
        if (_editor is not { } editor || _editing is not { } id || _model is null) return;
        CloseTextEdit();
        Insert.SetText(_model, id, editor.Text);
    }

    private void CloseTextEdit()
    {
        if (_editor is not { } editor) return;
        _editor = null;
        _editing = null;
        Root.Children.Remove(editor);
    }

    /// <summary>Whether a new text part is waiting for its text (the harness reads it).</summary>
    public bool IsTyping => _editor is not null;

    // ---- keyboard ------------------------------------------------------------------------------

    /// <summary>Arrow keys nudge whatever is selected, one pixel a press and
    /// <see cref="CoarseNudge"/> with <see cref="CoarseNudgeModifier"/> held; Shift+1 fits all,
    /// Shift+2 fits the selection, Ctrl+plus and Ctrl+minus step the zoom and Ctrl+0 is 100%.
    /// Handled here rather than on the window so that arrow keys still belong to the gallery list
    /// and the knobs when the focus is in them; clicking the canvas is what gives them to the canvas.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        // The text box laid over the canvas keeps its own arrow keys.
        if (e.Handled || _model is null || e.OriginalSource is TextBox) return;
        if (PenToggle.IsChecked == true && e.Key == Key.Enter) { FinishPen(false); e.Handled = true; return; }
        if ((PenToggle.IsChecked == true || TraceToggle.IsChecked == true) && e.Key == Key.Escape)
        { CancelDrawing(); e.Handled = true; return; }
        var mods = Keyboard.Modifiers;
        var ctrl = (mods & ModifierKeys.Control) != 0;
        var step = (mods & CoarseNudgeModifier) != 0 ? CoarseNudge : 1;
        switch (e.Key)
        {
            case Key.Left: MoveSelection("Nudge", -step, 0); e.Handled = true; break;
            case Key.Right: MoveSelection("Nudge", step, 0); e.Handled = true; break;
            case Key.Up: MoveSelection("Nudge", 0, -step); e.Handled = true; break;
            case Key.Down: MoveSelection("Nudge", 0, step); e.Handled = true; break;
            case Key.D1 when mods == ModifierKeys.Shift: FitAll(); e.Handled = true; break;
            case Key.D2 when mods == ModifierKeys.Shift: FitSelection(); e.Handled = true; break;
            case Key.OemPlus or Key.Add when ctrl: ZoomIn(); e.Handled = true; break;
            case Key.OemMinus or Key.Subtract when ctrl: ZoomOut(); e.Handled = true; break;
            case Key.D0 or Key.NumPad0 when ctrl: ZoomTo(1); e.Handled = true; break;
            case Key.Escape when _dragging: Reset(); e.Handled = true; break;
            default: break;
        }
    }

    // ---- the verbs, as the rest of the window can reach them -------------------------------------

    /// <summary>Move everything selected by one offset, as one undo entry. The canvas's own drags
    /// and the arrow keys both come through here so they cannot drift apart. At copy depth that is
    /// the selected parts (overrides on the copy); at layout depth whole copies.</summary>
    public void MoveSelection(string label, int dx, int dy)
    {
        if (_model is null || (dx == 0 && dy == 0)) return;
        var targets = SelectedTargets();
        if (targets.Count == 0) return;
        _model.MoveGroups(label, Offsets(targets, dx, dy));
    }

    /// <summary>Line the selection up on one line of its own bounding box, as one undo entry.</summary>
    public void AlignSelection(AlignOp op)
    {
        if (_model is null) return;
        var targets = SelectedTargets();
        if (targets.Count < 2) return;
        var offsets = Placement.Align(targets.Select(t => t.Bounds).ToList(), op);
        _model.MoveGroups(Label(op), Offsets(targets, offsets));
    }

    /// <summary>Even out the space between the selected things, as one undo entry.</summary>
    public void DistributeSelection(DistributeAxis axis)
    {
        if (_model is null) return;
        var targets = SelectedTargets();
        if (targets.Count < 3) return;
        var offsets = Placement.Distribute(targets.Select(t => t.Bounds).ToList(), axis);
        _model.MoveGroups(axis == DistributeAxis.Horizontal ? "Distribute horizontally" : "Distribute vertically",
            Offsets(targets, offsets));
    }

    private static string Label(AlignOp op) => op switch
    {
        AlignOp.Left => "Align left",
        AlignOp.CentreX => "Align centres",
        AlignOp.Right => "Align right",
        AlignOp.Top => "Align tops",
        AlignOp.MiddleY => "Align middles",
        _ => "Align bottoms",
    };

    private IReadOnlyList<(IReadOnlyList<string> Ids, int Dx, int Dy)> Offsets(
        IReadOnlyList<Target> targets, int dx, int dy)
        => targets.Select(t => (Ids(t), dx, dy)).ToList();

    private IReadOnlyList<(IReadOnlyList<string> Ids, int Dx, int Dy)> Offsets(
        IReadOnlyList<Target> targets, IReadOnlyList<(int Dx, int Dy)> offsets)
        => targets.Select((t, i) => (Ids(t), offsets[i].Dx, offsets[i].Dy)).ToList();

    /// <summary>The canvas has the keys (arrows nudge, Tab picks the next thing, Delete removes): an
    /// accent frame round the pane says so, since its own focus rectangle is suppressed (it would sit
    /// round the whole photograph and read as a selection).</summary>
    protected override void OnIsKeyboardFocusWithinChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnIsKeyboardFocusWithinChanged(e);
        _surface.Focused = IsKeyboardFocusWithin;
        _surface.InvalidateVisual();
    }

    // ---- the flash on something just added ----------------------------------------------------------

    private System.Windows.Threading.DispatcherTimer? _flashTimer;
    private DateTime _flashStart;

    /// <summary>How long a just-added copy pulses: long enough to catch the eye at fit-to-window,
    /// where a 172 px widget is 30 screen pixels across, short enough not to be in the way.</summary>
    private static readonly TimeSpan FlashFor = TimeSpan.FromMilliseconds(1400);

    /// <summary>Pulse <paramref name="id"/>'s box a few times, so a copy that landed in the margin
    /// is findable at once. Its selection outline stays afterwards.</summary>
    public void Flash(string id)
    {
        if (AllTargets().FirstOrDefault(t => t.Id == id) is not { } target) return;
        _surface.Flash = target.Bounds;
        _flashStart = DateTime.UtcNow;
        _flashTimer ??= new System.Windows.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(40), System.Windows.Threading.DispatcherPriority.Render, (_, _) => FlashStep(), Dispatcher);
        _flashTimer.Start();
        FlashStep();
    }

    private void FlashStep()
    {
        var t = (DateTime.UtcNow - _flashStart).TotalMilliseconds / FlashFor.TotalMilliseconds;
        if (t >= 1)
        {
            _flashTimer?.Stop();
            _surface.Flash = null;
        }
        else _surface.FlashLevel = 0.5 - 0.5 * Math.Cos(t * Math.PI * 6);   // three pulses
        _surface.InvalidateVisual();
    }

    // ---- painting -------------------------------------------------------------------------------

    private void Redraw()
    {
        var selected = SelectedTargets();
        var depth = _model?.Depth.Kind ?? DepthKind.Layout;
        _surface.CanvasW = _model?.Signature.Width ?? 0;
        _surface.CanvasH = _model?.Signature.Height ?? 0;
        _surface.Grid = GridToggle.IsChecked == true ? _spacing : 0;
        // Empty counts loose components too: a layout carried over from before widgets has plenty
        // on it, and telling its owner to "add a widget to start" over the top of it would be a lie.
        _surface.Empty = depth == DepthKind.Layout && _model is not null && AllTargets().Count == 0;
        _surface.Hint = _surface.Empty ? SpawnRegion() : null;
        _surface.Selected = selected.Select(t => t.Bounds).ToList();
        _surface.SelectionBox = selected.Count > 1 ? Placement.Bounds(selected.Select(t => t.Bounds)) : null;
        _surface.Hover = _hover is null || selected.Any(t => t.Id == _hover)
            ? null
            : AllTargets().FirstOrDefault(t => t.Id == _hover)?.Bounds;
        _surface.Handles = _resizing is null && !_dragging && SelectionScreenBox() is { } box
            ? VisibleHandles(box).Select(h => HandleCentre(box, h)).ToList()
            : [];
        _surface.OpenFrame = depth == DepthKind.Layout ? null : OpenBounds();
        _surface.Dimmed = depth == DepthKind.Widget;
        _surface.Broken = BrokenLinks();

        _alignBar.Visibility = selected.Count >= 2 ? Visibility.Visible : Visibility.Collapsed;
        _alignBar.SetSelectionCount(selected.Count);
        _surface.InvalidateVisual();
    }

    /// <summary>Every copy whose widget the last frame could not expand, as a box and a sentence
    /// (brief section 5: a visible broken link, never a silent blank).</summary>
    private IReadOnlyList<(CRect Box, string Text)> BrokenLinks()
    {
        if (_model is null || _frame is null) return [];
        var list = new List<(CRect, string)>();
        foreach (var p in _frame.Problems)
        {
            if (p.Kind is not (ExpandProblemKind.MissingWidget or ExpandProblemKind.BrokenWidget)) continue;
            if (Copies.Find(_model.Layout, p.CopyId) is not { } copy) continue;
            var box = Copies.Bounds(copy, _model.Expanded(), _model.Finder());
            list.Add((box, p.Kind == ExpandProblemKind.MissingWidget
                ? $"Widget '{copy.Widget}' is missing"
                : $"Widget '{copy.Widget}' could not be read"));
        }
        return list;
    }

    private static Rect Inflate(Rect r, double by)
        => new(r.X - by, r.Y - by, Math.Max(0, r.Width + by * 2), Math.Max(0, r.Height + by * 2));

    /// <summary>The drawing. Kept as one element rather than a visual tree of shapes: everything on
    /// it is derived from the model on every change, and rebuilding forty Rectangles per mouse move
    /// to say the same thing would be slower and no clearer.</summary>
    private sealed class Surface : FrameworkElement
    {
        public WriteableBitmap? Bitmap { get; set; }

        /// <summary>The viewport <see cref="Bitmap"/> was rendered for, or null for a whole-canvas frame.</summary>
        public Viewport? FrameView { get; set; }
        public double Zoom { get; set; } = 1;
        public Point Origin { get; set; }
        public int CanvasW { get; set; }
        public int CanvasH { get; set; }

        /// <summary>Grid spacing in canvas pixels, or 0 for no grid.</summary>
        public int Grid { get; set; }

        public IReadOnlyList<Point> PenPoints { get; set; } = [];
        public IReadOnlyList<CRect> Selected { get; set; } = [];
        public CRect? SelectionBox { get; set; }
        public CRect? Hover { get; set; }
        public IReadOnlyList<CRect> Ghosts { get; set; } = [];
        public CRect? GhostBox { get; set; }
        public CRect? BandRect { get; set; }
        public IReadOnlyList<Point> Handles { get; set; } = [];
        public CRect? Hint { get; set; }
        public bool Empty { get; set; }
        public IReadOnlyList<Snap.Guide> Guides { get; set; } = [];

        /// <summary>The open copy's or widget's bounds at copy and widget depth; null at layout depth.</summary>
        public CRect? OpenFrame { get; set; }

        /// <summary>The canvas has keyboard focus: an accent frame just inside the pane.</summary>
        public bool Focused { get; set; }

        /// <summary>A just-added copy, pulsing (<see cref="PreviewView.Flash"/>), and how bright now (0..1).</summary>
        public CRect? Flash { get; set; }
        public double FlashLevel { get; set; }

        /// <summary>Widget depth: everything outside <see cref="OpenFrame"/> is dimmed.</summary>
        public bool Dimmed { get; set; }
        public IReadOnlyList<(CRect Box, string Text)> Broken { get; set; } = [];

        private void DrawPen(DrawingContext dc)
        {
            var pen = new Pen(Brushes.LightSkyBlue, 1.5);
            Point Screen(Point p) => new(Origin.X + p.X * Zoom, Origin.Y + p.Y * Zoom);
            for (var i = 0; i < PenPoints.Count; i++)
            {
                var p = Screen(PenPoints[i]);
                if (i > 0) dc.DrawLine(pen, Screen(PenPoints[i - 1]), p);
                dc.DrawEllipse(Brushes.White, pen, p, 3, 3);
            }
        }

        public Point ToCanvas(Point screen) => new((screen.X - Origin.X) / Zoom, (screen.Y - Origin.Y) / Zoom);

        public Rect ToScreen(CRect r) => new(Origin.X + r.X * Zoom, Origin.Y + r.Y * Zoom, r.W * Zoom, r.H * Zoom);

        private Brush Themed(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            if (CanvasW <= 0 || CanvasH <= 0) return;

            var frame = ToScreen(new CRect(0, 0, CanvasW, CanvasH));
            dc.DrawRectangle(Themed("SolidBackgroundFillColorTertiaryBrush", Brushes.DimGray), null, frame);
            if (Bitmap is not null) dc.DrawImage(Bitmap, BitmapRect(frame));

            DrawGrid(dc, frame);

            var accent = Themed("AccentFillColorDefaultBrush", Brushes.DodgerBlue);
            // Every outline on this canvas is drawn twice, dark under light. A single stroke
            // disappears wherever the photograph behind it happens to be the same colour, and a
            // photograph is the one thing this surface is guaranteed to be.
            var halo = Stroke(Color.FromArgb(150, 0, 0, 0), 3.5);
            var outline = new Pen(accent, 1.75);

            foreach (var (box, text) in Broken) DrawBroken(dc, ToScreen(box), text);

            if (OpenFrame is { } open)
            {
                var box = ToScreen(open);
                if (Dimmed)
                {
                    var outside = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(frame), new RectangleGeometry(box));
                    dc.DrawGeometry(DimBrush, null, outside);
                }
                var dashed = new Pen(new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)), 1) { DashStyle = new DashStyle([4, 4], 0) };
                dc.DrawRectangle(null, Stroke(Color.FromArgb(120, 0, 0, 0), 2.5), Inflate(box, OutlineInset + 6));
                dc.DrawRectangle(null, dashed, Inflate(box, OutlineInset + 6));
            }

            if (Empty && Hint is { } hint) DrawEmptyState(dc, frame, hint);

            if (Hover is { } h)
            {
                dc.DrawRoundedRectangle(null, Stroke(Color.FromArgb(110, 0, 0, 0), 3), Inflate(ToScreen(h), OutlineInset), 4, 4);
                dc.DrawRoundedRectangle(null, Stroke(Color.FromArgb(190, 255, 255, 255), 1.25), Inflate(ToScreen(h), OutlineInset), 4, 4);
            }

            foreach (var s in Selected)
            {
                var box = Inflate(ToScreen(s), OutlineInset);
                dc.DrawRoundedRectangle(null, halo, box, 4, 4);
                dc.DrawRoundedRectangle(null, outline, box, 4, 4);
            }

            if (SelectionBox is { } group)
            {
                var box = Inflate(ToScreen(group), OutlineInset + 4);
                var dashed = new Pen(accent, 1.25) { DashStyle = new DashStyle([4, 3], 0) };
                dc.DrawRectangle(null, Stroke(Color.FromArgb(120, 0, 0, 0), 2.5), box);
                dc.DrawRectangle(null, dashed, box);
            }

            foreach (var g in Ghosts)
            {
                var box = Inflate(ToScreen(g), OutlineInset);
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), null, box, 4, 4);
                dc.DrawRoundedRectangle(null, Stroke(Color.FromArgb(150, 0, 0, 0), 3), box, 4, 4);
                dc.DrawRoundedRectangle(null, new Pen(accent, 1.5), box, 4, 4);
            }

            if (GhostBox is { } gb && Ghosts.Count > 1)
                dc.DrawRectangle(null, new Pen(accent, 1) { DashStyle = new DashStyle([4, 3], 0) },
                    Inflate(ToScreen(gb), OutlineInset + 4));

            DrawGuides(dc, frame);

            if (BandRect is { } band)
            {
                var box = ToScreen(band);
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(46, 255, 255, 255)), null, box);
                dc.DrawRectangle(null, Stroke(Color.FromArgb(150, 0, 0, 0), 2.5), box);
                dc.DrawRectangle(null, new Pen(accent, 1.25), box);
            }

            DrawPen(dc);
            DrawHandles(dc);

            if (Focused)
                dc.DrawRectangle(null, new Pen(accent, 2), new Rect(1, 1, Math.Max(0, ActualWidth - 2), Math.Max(0, ActualHeight - 2)));

            if (Flash is { } flash)
            {
                var box = Inflate(ToScreen(flash), OutlineInset + 6);
                var level = FlashLevel;
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb((byte)(90 * level), 255, 255, 255)), null, box, 6, 6);
                dc.DrawRoundedRectangle(null, Stroke(Color.FromArgb((byte)(200 * level), 0, 0, 0), 4), box, 6, 6);
                var ring = new Pen(accent.Clone(), 2.5);
                ring.Brush.Opacity = level;
                dc.DrawRoundedRectangle(null, ring, box, 6, 6);
            }
        }

        /// <summary>Where the frame's bitmap goes on screen. A whole-canvas frame covers the canvas.
        /// A viewport frame covers the canvas area it was rendered for, mapped through the current
        /// view: normally the pane exactly (1:1, no resampling), and during a pan or zoom that has not
        /// re-rendered yet, the old pixels stretched into place until the new frame lands.</summary>
        private Rect BitmapRect(Rect frame)
        {
            if (FrameView is not { } fv || Bitmap is null) return frame;
            // The renderer rounds the origin; the view does too, so this is whole pixels when they agree.
            var left = -Math.Round(fv.OriginX) / fv.Zoom;
            var top = -Math.Round(fv.OriginY) / fv.Zoom;
            return new Rect(Origin.X + left * Zoom, Origin.Y + top * Zoom,
                Bitmap.PixelWidth / fv.Zoom * Zoom, Bitmap.PixelHeight / fv.Zoom * Zoom);
        }

        /// <summary>The smart guides mid-drag: full-length lines across the wallpaper in a colour no
        /// photograph or selection outline uses, so a snap is visible at any zoom.</summary>
        private void DrawGuides(DrawingContext dc, Rect frame)
        {
            if (Guides.Count == 0) return;
            var halo = Stroke(Color.FromArgb(140, 0, 0, 0), 3);
            var line = Stroke(Color.FromRgb(255, 64, 160), 1);
            foreach (var g in Guides)
            {
                if (g.Vertical)
                {
                    var x = Math.Round(Origin.X + g.Position * Zoom) + 0.5;
                    dc.DrawLine(halo, new Point(x, frame.Top), new Point(x, frame.Bottom));
                    dc.DrawLine(line, new Point(x, frame.Top), new Point(x, frame.Bottom));
                }
                else
                {
                    var y = Math.Round(Origin.Y + g.Position * Zoom) + 0.5;
                    dc.DrawLine(halo, new Point(frame.Left, y), new Point(frame.Right, y));
                    dc.DrawLine(line, new Point(frame.Left, y), new Point(frame.Right, y));
                }
            }
        }

        /// <summary>A copy whose widget is missing or unreadable: an amber-hatched box where it sits,
        /// with the reason in it when the box is big enough on screen to hold a line of text.</summary>
        private void DrawBroken(DrawingContext dc, Rect box, string text)
        {
            dc.DrawRectangle(BrokenFill, null, box);
            dc.DrawRectangle(HatchBrush, null, box);
            dc.DrawRectangle(null, Stroke(Color.FromArgb(230, 0, 0, 0), 3), box);
            dc.DrawRectangle(null, Stroke(Color.FromRgb(255, 176, 32), 1.5), box);
            if (box.Width < 40 || box.Height < 14) return;
            var label = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                Math.Clamp(box.Height * 0.3, 11, 16), Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip)
            { MaxTextWidth = Math.Max(1, box.Width - 12), MaxTextHeight = Math.Max(1, box.Height - 6), Trimming = TextTrimming.CharacterEllipsis };
            var at = new Point(box.X + 6, box.Y + Math.Max(3, (box.Height - label.Height) / 2));
            var plate = new Rect(at.X - 3, at.Y - 1, label.Width + 6, label.Height + 2);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)), null, plate, 3, 3);
            dc.DrawText(label, at);
        }

        private static readonly Brush DimBrush = Frozen(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)));
        private static readonly Brush BrokenFill = Frozen(new SolidColorBrush(Color.FromArgb(150, 40, 24, 0)));

        /// <summary>Diagonal amber stripes, 8 screen pixels apart whatever the zoom.</summary>
        private static readonly Brush HatchBrush = Frozen(new DrawingBrush(new GeometryDrawing(null,
            new Pen(new SolidColorBrush(Color.FromArgb(170, 255, 176, 32)), 2),
            Geometry.Parse("M0,8 L8,0 M-2,2 L2,-2 M6,10 L10,6")))
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 8, 8),
            ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, 8, 8),
            ViewboxUnits = BrushMappingMode.Absolute,
        });

        private static Brush Frozen(Brush b) { b.Freeze(); return b; }

        /// <summary>The grid, in canvas pixels, at a coarser multiple when the zoom would otherwise
        /// pack the lines into a haze. Only how many lines are drawn changes - what snapping rounds
        /// to is the spacing the owner chose, whatever the zoom.</summary>
        private void DrawGrid(DrawingContext dc, Rect frame)
        {
            if (Grid <= 1 || Zoom <= 0) return;
            var step = Grid;
            while (step * Zoom < MinGridPixels) step *= 2;

            var pen = Stroke(Color.FromArgb(40, 255, 255, 255), 1);
            var pane = new Rect(0, 0, ActualWidth, ActualHeight);
            dc.PushClip(new RectangleGeometry(frame));
            for (var x = step; x < CanvasW; x += step)
            {
                var at = Math.Round(Origin.X + x * Zoom) + 0.5;
                if (at >= Math.Max(frame.X, pane.X) && at <= Math.Min(frame.Right, pane.Right)) dc.DrawLine(pen, new Point(at, frame.Y), new Point(at, frame.Bottom));
            }
            for (var y = step; y < CanvasH; y += step)
            {
                var at = Math.Round(Origin.Y + y * Zoom) + 0.5;
                if (at >= Math.Max(frame.Y, pane.Y) && at <= Math.Min(frame.Bottom, pane.Bottom)) dc.DrawLine(pen, new Point(frame.X, at), new Point(frame.Right, at));
            }
            dc.Pop();
        }

        /// <summary>Nothing on the layout yet: say so, and show where the first widget will land.
        /// Beside the region, not inside it - it is 172 px of a 3440-wide photo, which at
        /// fit-to-window is forty pixels across and will not hold a sentence.</summary>
        private void DrawEmptyState(DrawingContext dc, Rect frame, CRect hint)
        {
            var region = ToScreen(hint);
            var dashed = new Pen(new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)), 1.5)
            { DashStyle = new DashStyle([5, 4], 0) };
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)), dashed, region, 8, 8);

            var text = new FormattedText("Add a widget from the list to start. It lands here, and you can drag it anywhere.",
                CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 15, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip)
            { TextAlignment = TextAlignment.Center, MaxTextWidth = 280 };
            var at = new Point(Math.Max(frame.X + 8, region.X - 16 - text.Width),
                               region.Y + Math.Max(0, (region.Height - text.Height) / 2));
            var plate = new Rect(at.X - 12, at.Y - 8, text.Width + 24, text.Height + 16);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)), null, plate, 6, 6);
            dc.DrawText(text, at);
        }

        /// <summary>The grips: constant size in screen pixels whatever the zoom, white on a dark
        /// stroke so they survive being drawn over a photograph of anything.</summary>
        private void DrawHandles(DrawingContext dc)
        {
            if (Handles.Count == 0) return;
            var edge = Stroke(Color.FromArgb(220, 0, 0, 0), 1);
            var fill = Brushes.White;
            var half = HandleSize / 2;
            foreach (var centre in Handles)
                dc.DrawRectangle(fill, edge, new Rect(centre.X - half, centre.Y - half, HandleSize, HandleSize));
        }

        private static Pen Stroke(Color colour, double thickness)
        {
            var pen = new Pen(new SolidColorBrush(colour), thickness);
            pen.Freeze();
            return pen;
        }

        private static Rect Inflate(Rect r, double by)
            => new(r.X - by, r.Y - by, Math.Max(0, r.Width + by * 2), Math.Max(0, r.Height + by * 2));
    }
}
