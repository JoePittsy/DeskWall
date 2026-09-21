using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
/// exists only while two or more things are selected, and the grips only while something is.
/// </para>
/// </summary>
public partial class PreviewView : UserControl
{
    /// <summary>Hold this while dragging and the gesture lands on the grid; let go of it and the
    /// gesture is pixel-exact. One named constant, used by moves and by resizes alike, because
    /// this is deliberately the opposite way round from Figma (where snapping is the default and a
    /// modifier suspends it) and flipping or rebinding it should be one edit.</summary>
    public const ModifierKeys SnapModifier = ModifierKeys.Shift;

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

    /// <summary>How much of the canvas must stay in the pane, in screen pixels, however far it is
    /// panned at 1:1. A view that can be scrolled into blank grey is a view that gets lost.</summary>
    private const double KeepVisible = 120;

    private static readonly Handle[] Corners =
        [Handle.TopLeft, Handle.TopRight, Handle.BottomRight, Handle.BottomLeft];

    private readonly Surface _surface = new();
    private readonly AlignBar _alignBar = new();

    private DesignerModel? _model;
    private PreviewRenderer? _renderer;
    private WriteableBitmap? _bitmap;
    private IReadOnlyList<Target>? _targets;

    private bool _oneToOne;               // false: the whole wallpaper fitted to the pane; true: 1:1
    private Vector _pan;                  // the 1:1 view's offset from centred
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

    public void Attach(DesignerModel model, PreviewRenderer renderer)
    {
        if (_model is not null) { _model.Changed -= OnModelChanged; _model.SelectionChanged -= OnSelectionChanged; }
        if (_renderer is not null) _renderer.Rendered -= OnRendered;
        _model = model;
        _renderer = renderer;
        _model.Changed += OnModelChanged;
        _model.SelectionChanged += OnSelectionChanged;
        _renderer.Rendered += OnRendered;
        _targets = null;
        PlaceView();
        _renderer.Request(_model);
        Redraw();
    }

    // ---- model and frame -----------------------------------------------------------------------

    private void OnModelChanged()
    {
        _targets = null;
        if (_model is not null) _renderer?.Request(_model);
        Redraw();
    }

    private void OnSelectionChanged() => Redraw();

    private void OnRendered(PreviewFrame frame)
    {
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

    // ---- what is on the canvas ------------------------------------------------------------------

    private IReadOnlyList<Target> AllTargets()
        => _targets ??= _model is null ? [] : Targets.All(_model.Layout);

    private IReadOnlyList<Target> SelectedTargets()
        => _model is null ? [] : Targets.From(_model.Layout, _model.Selection);

    private void SelectTargets(IEnumerable<Target> targets)
        => _model?.Select(Targets.ComponentIds(targets));

    /// <summary>Where a widget added from the gallery lands, and - while the layout is empty - the
    /// only thing drawn on the canvas besides the photograph. Still the right-hand margin, because
    /// that is the part of the wallpaper windows leave visible (CLAUDE.md); it is a hint about the
    /// next widget, not a box anything has to stay in.</summary>
    private CRect SpawnRegion()
        => _model is null ? default : Arranger.Column(_model.Signature.Width, _model.Signature.Height);

    // ---- the two views -------------------------------------------------------------------------

    private void ZoomToggle_Click(object sender, RoutedEventArgs e)
    {
        _oneToOne = !_oneToOne;
        ZoomToggle.Content = _oneToOne ? "Fit" : "100%";
        ZoomToggle.ToolTip = _oneToOne ? "Fit the whole wallpaper in the pane" : "Show the wallpaper at its real size";
        if (_oneToOne) CentreOnSelection();
        PlaceView();
        Redraw();
    }

    /// <summary>Whole wallpaper: the frame fits the pane. 1:1: real size, panned with the wheel.</summary>
    private void PlaceView()
    {
        if (_model is null) return;
        double w = _model.Signature.Width, h = _model.Signature.Height;
        double vw = _surface.ActualWidth, vh = _surface.ActualHeight;
        if (w <= 0 || h <= 0 || vw <= 0 || vh <= 0) return;

        if (_oneToOne)
        {
            ClampPan();
            _surface.Zoom = 1.0;
            _surface.Origin = new Point((vw - w) / 2 + _pan.X, (vh - h) / 2 + _pan.Y);
        }
        else
        {
            _pan = default;
            _surface.Zoom = Math.Max(0.02, Math.Min((vw - 48) / w, (vh - 48) / h));
            _surface.Origin = new Point((vw - w * _surface.Zoom) / 2, (vh - h * _surface.Zoom) / 2);
        }
    }

    /// <summary>Switching to 1:1 puts what the owner is working on in the middle of the pane, or
    /// the margin the widgets live in when nothing is selected. Landing on the middle of a
    /// 3440-wide photograph instead would show him a patch of sky.</summary>
    private void CentreOnSelection()
    {
        if (_model is null) return;
        var focus = Placement.Bounds(SelectedTargets().Select(t => t.Bounds));
        if (focus.W == 0 && focus.H == 0) focus = SpawnRegion();
        _pan = new Vector(
            _model.Signature.Width / 2.0 - (focus.X + focus.W / 2.0),
            _model.Signature.Height / 2.0 - (focus.Y + focus.H / 2.0));
    }

    private void ClampPan()
    {
        if (_model is null) return;
        double w = _model.Signature.Width, h = _model.Signature.Height;
        double vw = _surface.ActualWidth, vh = _surface.ActualHeight;
        if (vw <= 0 || vh <= 0) return;
        _pan = new Vector(Clamp(_pan.X, (vw - w) / 2, w, vw), Clamp(_pan.Y, (vh - h) / 2, h, vh));

        static double Clamp(double pan, double centred, double canvas, double viewport)
        {
            var lo = KeepVisible - canvas - centred;
            var hi = viewport - KeepVisible - centred;
            return Math.Clamp(pan, Math.Min(lo, hi), Math.Max(lo, hi));
        }
    }

    /// <summary>At 1:1 the wallpaper is several times the size of the pane, so the wheel pans it:
    /// vertically, or horizontally with Shift held. Fitted, there is nothing off screen to reach.</summary>
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!_oneToOne || _model is null) return;
        var by = e.Delta * 0.6;
        _pan += (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? new Vector(by, 0) : new Vector(0, by);
        PlaceView();
        Redraw();
        e.Handled = true;
    }

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
    /// the edge grips appear only when their side is long enough to hold one clear of the corners,
    /// and below about twice a grip the box gets none at all and has to be zoomed in on to scale.
    /// The corners are the ones that matter, so they are the ones that survive longest.</summary>
    private static IEnumerable<Handle> VisibleHandles(Rect box)
    {
        if (box.Width < HandleSize * 2 || box.Height < HandleSize * 2) yield break;
        foreach (var corner in Corners) yield return corner;
        if (box.Width >= HandleSize * 5) { yield return Handle.Top; yield return Handle.Bottom; }
        if (box.Height >= HandleSize * 5) { yield return Handle.Left; yield return Handle.Right; }
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
        Focus();
        if (_model is null || e.ChangedButton != MouseButton.Left) return;
        _downScreen = e.GetPosition(_surface);
        _dragging = false;
        _moving = false;
        _banding = false;
        _resizing = null;
        e.Handled = true;

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

        if (e.LeftButton != MouseButtonState.Pressed || (!_moving && !_banding && _resizing is null))
        {
            Track(p);
            return;
        }

        if (!_dragging
            && Math.Abs(p.X - _downScreen.X) < DragSlop
            && Math.Abs(p.Y - _downScreen.Y) < DragSlop) return;
        _dragging = true;

        if (_resizing is { } handle)
        {
            var to = ResizedBox(handle, p);
            _surface.Ghosts = _gestureTargets.Select(t => Resize.Map(t.Bounds, _gestureBox, to)).ToList();
            _surface.GhostBox = to;
        }
        else if (_moving)
        {
            var (dx, dy) = MoveOffset(p);
            _surface.Ghosts = _gestureTargets.Select(t => t.Bounds.Offset(dx, dy)).ToList();
            _surface.GhostBox = _gestureBox.Offset(dx, dy);
        }
        else
        {
            var band = CanvasRect(_downScreen, p);
            _surface.BandRect = band;
            var caught = Targets.Within(AllTargets(), band).Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
            var keep = Targets.From(_model.Layout, _bandBase);
            SelectTargets(keep.Concat(AllTargets().Where(t => caught.Contains(t.Id) && keep.All(k => k.Id != t.Id))));
        }
        Redraw();
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.ChangedButton != MouseButton.Left) return;
        if (_model is not null && _dragging)
        {
            var p = e.GetPosition(_surface);
            if (_resizing is { } handle)
            {
                var to = ResizedBox(handle, p);
                var corner = Resize.IsCorner(handle);
                _model.Scale(corner ? "Scale" : "Resize",
                    _gestureTargets.SelectMany(t => t.ComponentIds).ToList(), _gestureBox, to, corner);
            }
            else if (_moving)
            {
                var (dx, dy) = MoveOffset(p);
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

    /// <summary>The offset this move has reached, snapped if the modifier is down. Measured from
    /// where the press landed, so the thing being dragged stays under the pointer.</summary>
    private (int Dx, int Dy) MoveOffset(Point p)
    {
        var dx = (int)Math.Round((p.X - _downScreen.X) / _surface.Zoom);
        var dy = (int)Math.Round((p.Y - _downScreen.Y) / _surface.Zoom);
        return Placement.DragOffset(_gestureBox, dx, dy, SnapTo());
    }

    private CRect ResizedBox(Handle handle, Point p)
        => Resize.Box(_gestureBox, handle,
            (int)Math.Round((p.X - _downScreen.X) / _surface.Zoom),
            (int)Math.Round((p.Y - _downScreen.Y) / _surface.Zoom),
            SnapTo());

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
        ReleaseMouseCapture();
        Redraw();
    }

    // ---- keyboard ------------------------------------------------------------------------------

    /// <summary>Arrow keys nudge whatever is selected, one pixel a press and
    /// <see cref="CoarseNudge"/> with <see cref="CoarseNudgeModifier"/> held. Handled here rather
    /// than on the window so that arrow keys still belong to the gallery list and the knobs when
    /// the focus is in them; clicking the canvas is what gives them to the canvas.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || _model is null) return;
        var step = (Keyboard.Modifiers & CoarseNudgeModifier) != 0 ? CoarseNudge : 1;
        switch (e.Key)
        {
            case Key.Left: MoveSelection("Nudge", -step, 0); e.Handled = true; break;
            case Key.Right: MoveSelection("Nudge", step, 0); e.Handled = true; break;
            case Key.Up: MoveSelection("Nudge", 0, -step); e.Handled = true; break;
            case Key.Down: MoveSelection("Nudge", 0, step); e.Handled = true; break;
            case Key.Escape when _dragging: Reset(); e.Handled = true; break;
            default: break;
        }
    }

    // ---- the verbs, as the rest of the window can reach them -------------------------------------

    /// <summary>Move everything selected by one offset, as one undo entry. The canvas's own drags
    /// and the arrow keys both come through here so they cannot drift apart.</summary>
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

    private static IReadOnlyList<(IReadOnlyList<string> Ids, int Dx, int Dy)> Offsets(
        IReadOnlyList<Target> targets, int dx, int dy)
        => targets.Select(t => (t.ComponentIds, dx, dy)).ToList();

    private static IReadOnlyList<(IReadOnlyList<string> Ids, int Dx, int Dy)> Offsets(
        IReadOnlyList<Target> targets, IReadOnlyList<(int Dx, int Dy)> offsets)
        => targets.Select((t, i) => (t.ComponentIds, offsets[i].Dx, offsets[i].Dy)).ToList();

    // ---- painting -------------------------------------------------------------------------------

    private void Redraw()
    {
        var selected = SelectedTargets();
        _surface.CanvasW = _model?.Signature.Width ?? 0;
        _surface.CanvasH = _model?.Signature.Height ?? 0;
        _surface.Grid = GridToggle.IsChecked == true ? _spacing : 0;
        // Empty counts loose components too: a layout carried over from before widgets has plenty
        // on it, and telling its owner to "add a widget to start" over the top of it would be a lie.
        _surface.Empty = _model is not null && AllTargets().Count == 0;
        _surface.Hint = _surface.Empty ? SpawnRegion() : null;
        _surface.Selected = selected.Select(t => t.Bounds).ToList();
        _surface.SelectionBox = selected.Count > 1 ? Placement.Bounds(selected.Select(t => t.Bounds)) : null;
        _surface.Hover = _hover is null || selected.Any(t => t.Id == _hover)
            ? null
            : AllTargets().FirstOrDefault(t => t.Id == _hover)?.Bounds;
        _surface.Handles = _resizing is null && !_dragging && SelectionScreenBox() is { } box
            ? VisibleHandles(box).Select(h => HandleCentre(box, h)).ToList()
            : [];

        _alignBar.Visibility = selected.Count >= 2 ? Visibility.Visible : Visibility.Collapsed;
        _alignBar.SetSelectionCount(selected.Count);
        _surface.InvalidateVisual();
    }

    private static Rect Inflate(Rect r, double by)
        => new(r.X - by, r.Y - by, Math.Max(0, r.Width + by * 2), Math.Max(0, r.Height + by * 2));

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

        /// <summary>Grid spacing in canvas pixels, or 0 for no grid.</summary>
        public int Grid { get; set; }

        public IReadOnlyList<CRect> Selected { get; set; } = [];
        public CRect? SelectionBox { get; set; }
        public CRect? Hover { get; set; }
        public IReadOnlyList<CRect> Ghosts { get; set; } = [];
        public CRect? GhostBox { get; set; }
        public CRect? BandRect { get; set; }
        public IReadOnlyList<Point> Handles { get; set; } = [];
        public CRect? Hint { get; set; }
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

            DrawGrid(dc, frame);

            var accent = Themed("AccentFillColorDefaultBrush", Brushes.DodgerBlue);
            // Every outline on this canvas is drawn twice, dark under light. A single stroke
            // disappears wherever the photograph behind it happens to be the same colour, and a
            // photograph is the one thing this surface is guaranteed to be.
            var halo = Stroke(Color.FromArgb(150, 0, 0, 0), 3.5);
            var outline = new Pen(accent, 1.75);

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

            if (BandRect is { } band)
            {
                var box = ToScreen(band);
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(46, 255, 255, 255)), null, box);
                dc.DrawRectangle(null, Stroke(Color.FromArgb(150, 0, 0, 0), 2.5), box);
                dc.DrawRectangle(null, new Pen(accent, 1.25), box);
            }

            DrawHandles(dc);
        }

        /// <summary>The grid, in canvas pixels, at a coarser multiple when the zoom would otherwise
        /// pack the lines into a haze. Only how many lines are drawn changes - what snapping rounds
        /// to is the spacing the owner chose, whatever the zoom.</summary>
        private void DrawGrid(DrawingContext dc, Rect frame)
        {
            if (Grid <= 1 || Zoom <= 0) return;
            var step = Grid;
            while (step * Zoom < MinGridPixels) step *= 2;

            var pen = Stroke(Color.FromArgb(40, 255, 255, 255), 1);
            dc.PushClip(new RectangleGeometry(frame));
            for (var x = step; x < CanvasW; x += step)
            {
                var at = Math.Round(Origin.X + x * Zoom) + 0.5;
                if (at >= frame.X && at <= frame.Right) dc.DrawLine(pen, new Point(at, frame.Y), new Point(at, frame.Bottom));
            }
            for (var y = step; y < CanvasH; y += step)
            {
                var at = Math.Round(Origin.Y + y * Zoom) + 0.5;
                if (at >= frame.Y && at <= frame.Bottom) dc.DrawLine(pen, new Point(frame.X, at), new Point(frame.Right, at));
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
