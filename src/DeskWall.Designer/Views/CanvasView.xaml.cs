using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DeskWall.Core.Layout;
using DeskWall.Designer.Model;
using CRect = DeskWall.Core.Rect;

namespace DeskWall.Designer.Views;

/// <summary>
/// The editing surface. Shows the Core render of the layout at the display's real pixel size,
/// scaled to fit, and lets the pointer and the keyboard put components where they belong.
/// <para>
/// Job: place a component exactly where the wallpaper needs it, against the wallpaper it will
/// actually be. Everything the canvas draws over the preview either identifies what is selected or
/// explains an alignment the drag just made; anything else belongs to a panel.
/// </para>
/// </summary>
public partial class CanvasView : UserControl
{
    private const double MinZoom = 0.25, MaxZoom = 4.0, ZoomStep = 1.15;
    private const int MinSize = 4;
    private const int DuplicateOffset = 16;
    /// <summary>Screen pixels within which a press counts as grabbing a handle.</summary>
    private const double HandleGrab = 7;

    private enum Mode { None, Pan, Marquee, Move, Resize }

    private readonly CanvasAdorner _adorner = new();
    private DesignerModel? _model;
    private PreviewRenderer? _renderer;
    private PreviewFrame? _frame;
    private WriteableBitmap? _bitmap;

    /// <summary>Top-level component id to the rect a click must land in. Built from the frame's
    /// resolved rects (a repeater's expanded cells union into their repeater), unioned with the
    /// declared rect so a component that resolves to nothing at all - an empty repeater, a shortcut
    /// with a blank target - is still selectable and therefore still fixable.</summary>
    private readonly Dictionary<string, CRect> _hit = new(StringComparer.Ordinal);

    private bool _fit = true;
    private bool _space;
    private Mode _mode;
    private Point _downScreen;
    private CRect _startRect;
    private Handle _handle = Handle.None;
    private string? _resizeId;
    private string? _clicked;
    private readonly Dictionary<string, CRect> _startRects = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CRect> _ghost = new(StringComparer.Ordinal);
    private (int X, int Y) _cursor;
    private CRect? _committed;
    private string? _highlightId;

    public CanvasView()
    {
        InitializeComponent();
        Host.Child = _adorner;
        _adorner.SizeChanged += (_, _) => { if (_fit) Fit(); Redraw(); };
        ContextMenuOpening += OnContextMenuOpening;
    }

    public DesignerModel? Model => _model;

    /// <summary>Point the canvas at a document and its renderer. Idempotent; call again to swap
    /// either. Task 8's shell owns both objects.</summary>
    public void Attach(DesignerModel model, PreviewRenderer renderer)
    {
        if (_model is not null) { _model.Changed -= OnModelChanged; _model.SelectionChanged -= OnSelectionChanged; }
        if (_renderer is not null) _renderer.Rendered -= OnRendered;
        _model = model;
        _renderer = renderer;
        _model.Changed += OnModelChanged;
        _model.SelectionChanged += OnSelectionChanged;
        _renderer.Rendered += OnRendered;
        _fit = true;
        _highlightId = null;
        Fit();
        _renderer.Request(_model);
        Redraw();
    }

    /// <summary>Outline a component without selecting it. The shell calls this when the layers panel
    /// activates a repeater's template child: a template child has no rect of its own on the canvas
    /// (the resolver expands it per item and DesignerModel.Find cannot even see it), so the honest
    /// answer to "where is that" is the repeater it lives in. Null clears it.</summary>
    public void HighlightComponent(string? id)
    {
        if (_highlightId == id) return;
        _highlightId = id;
        Redraw();
    }

    // ---- model and frame ---------------------------------------------------------------------

    private void OnModelChanged()
    {
        if (_model is not null) _renderer?.Request(_model);
        RebuildHit();
        Redraw();
    }

    private void OnSelectionChanged()
    {
        // Selecting anything on the canvas answers "where is that" for real, so the borrowed
        // template-child highlight has nothing left to say.
        if (_model is { Selection.Count: > 0 }) _highlightId = null;
        Redraw();
    }

    private void OnRendered(PreviewFrame frame)
    {
        _frame = frame;
        if (_bitmap is null || _bitmap.PixelWidth != frame.Width || _bitmap.PixelHeight != frame.Height)
        {
            // Pbgra32, not Bgra32: Core's surface is already premultiplied, so this is a straight copy.
            _bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Pbgra32, null);
            _adorner.Bitmap = _bitmap;
            if (_fit) Fit();
        }
        _bitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.Bgra, frame.Width * 4, 0);
        RebuildHit();
        Redraw();
    }

    private void RebuildHit()
    {
        _hit.Clear();
        if (_frame is not null)
            foreach (var r in _frame.Resolved)
            {
                var owner = TopLevelId(r.Id);
                _hit[owner] = _hit.TryGetValue(owner, out var cur) ? Union(cur, r.Rect) : r.Rect;
            }
        if (_model is not null)
            foreach (var c in _model.Layout.Components)
                _hit[c.Id] = _hit.TryGetValue(c.Id, out var cur) ? Union(cur, c.Rect) : c.Rect;
    }

    /// <summary>"drives[2].letter" belongs to "drives": a repeater selects as a whole.</summary>
    private static string TopLevelId(string resolvedId)
    {
        var i = resolvedId.IndexOf('[');
        return i < 0 ? resolvedId : resolvedId[..i];
    }

    private static CRect Union(CRect a, CRect b)
    {
        var x = Math.Min(a.X, b.X); var y = Math.Min(a.Y, b.Y);
        return new CRect(x, y, Math.Max(a.Right, b.Right) - x, Math.Max(a.Bottom, b.Bottom) - y);
    }

    // ---- view transform ------------------------------------------------------------------------

    private CRect CanvasRect => _model is null ? default : new CRect(0, 0, _model.Signature.Width, _model.Signature.Height);

    private void Fit()
    {
        if (_model is null) return;
        double w = _model.Signature.Width, h = _model.Signature.Height;
        double vw = _adorner.ActualWidth, vh = _adorner.ActualHeight;
        if (w <= 0 || h <= 0 || vw <= 0 || vh <= 0) return;
        _adorner.Zoom = Math.Max(0.02, Math.Min(vw / w, vh / h));
        _adorner.Origin = new Point((vw - w * _adorner.Zoom) / 2, (vh - h * _adorner.Zoom) / 2);
    }

    private (int X, int Y) ToCanvas(Point p) => (
        (int)Math.Floor((p.X - _adorner.Origin.X) / _adorner.Zoom),
        (int)Math.Floor((p.Y - _adorner.Origin.Y) / _adorner.Zoom));

    // ---- painting ------------------------------------------------------------------------------

    private void Redraw()
    {
        _adorner.CanvasW = _model?.Signature.Width ?? 0;
        _adorner.CanvasH = _model?.Signature.Height ?? 0;
        _adorner.Cells = BuildCells();
        _adorner.Outlines = SelectedRects();
        _adorner.Handles = _model is { Selection.Count: 1 } ? SelectedRects().FirstOrDefault() : null;
        _adorner.Highlight = _highlightId is { } hid && _hit.TryGetValue(hid, out var hr) ? hr : null;
        _adorner.Refresh();
        UpdateStatus();
    }

    /// <summary>The rect of each selected component: the live drag rect while a gesture is in
    /// flight, the model's otherwise. The model is only written at mouse-up, so this is where the
    /// feedback during a drag comes from.</summary>
    private List<CRect> SelectedRects()
    {
        var list = new List<CRect>();
        if (_model is null) return list;
        foreach (var id in _model.Selection)
            if (_ghost.TryGetValue(id, out var g)) list.Add(g);
            else if (_model.Find(id) is { } c) list.Add(c.Rect);
        return list;
    }

    /// <summary>One outline per expanded repeater cell, first cell of each repeater at full
    /// opacity. Built from the resolved ids, which is the only place the expansion exists.</summary>
    private List<(CRect Rect, bool First)> BuildCells()
    {
        var cells = new List<(CRect, bool)>();
        if (_frame is null) return cells;
        var byCell = new Dictionary<(string Owner, int Index), CRect>();
        var order = new List<(string Owner, int Index)>();
        foreach (var r in _frame.Resolved)
        {
            var open = r.Id.IndexOf('[');
            if (open < 0) continue;
            var close = r.Id.IndexOf(']', open);
            if (close < 0 || !int.TryParse(r.Id.AsSpan(open + 1, close - open - 1), out var idx)) continue;
            var key = (r.Id[..open], idx);
            if (byCell.TryGetValue(key, out var cur)) byCell[key] = Union(cur, r.Rect);
            else { byCell[key] = r.Rect; order.Add(key); }
        }
        foreach (var key in order) cells.Add((byCell[key], key.Index == 0));
        return cells;
    }

    private void UpdateStatus()
    {
        var inv = CultureInfo.InvariantCulture;
        var sel = "no selection";
        if (_model is { Selection.Count: > 0 } m)
        {
            if (m.Selection.Count == 1 && SelectedRects().FirstOrDefault() is var r && r.W >= 0)
                sel = string.Format(inv, "{0}  {1},{2}  {3}x{4}", m.Selection[0], r.X, r.Y, r.W, r.H);
            else sel = string.Format(inv, "{0} selected", m.Selection.Count);
        }
        var ms = _frame is null ? "-" : string.Format(inv, "{0:0} ms", _frame.RenderTime.TotalMilliseconds);
        Status.Text = string.Format(inv, "{0}, {1}     {2}     {3:0}%     {4}",
            _cursor.X, _cursor.Y, sel, _adorner.Zoom * 100, ms);
    }

    // ---- picking -------------------------------------------------------------------------------

    /// <summary>Topmost component whose hit rect contains the point: highest Z wins, then the one
    /// later in the file, which is what the renderer paints last.</summary>
    private string? Pick(int x, int y)
    {
        if (_model is null) return null;
        string? best = null; var bestZ = int.MinValue; var bestIndex = -1;
        for (var i = 0; i < _model.Layout.Components.Count; i++)
        {
            var c = _model.Layout.Components[i];
            if (!_hit.TryGetValue(c.Id, out var r)) continue;
            if (x < r.X || x >= r.Right || y < r.Y || y >= r.Bottom) continue;
            if (c.Z < bestZ || (c.Z == bestZ && i < bestIndex)) continue;
            best = c.Id; bestZ = c.Z; bestIndex = i;
        }
        return best;
    }

    private Handle HandleAt(Point screen)
    {
        if (_model is not { Selection.Count: 1 }) return Handle.None;
        var r = SelectedRects().FirstOrDefault();
        var pts = CanvasAdorner.HandlePoints(_adorner.ToScreen(r));
        for (var i = 0; i < pts.Length; i++)
            if (Math.Abs(pts[i].X - screen.X) <= HandleGrab && Math.Abs(pts[i].Y - screen.Y) <= HandleGrab)
                return (Handle)i;
        return Handle.None;
    }

    private List<CRect> OtherRects(IEnumerable<string> moving)
    {
        var set = moving.ToHashSet(StringComparer.Ordinal);
        return _model is null ? new List<CRect>()
            : _model.Layout.Components.Where(c => !set.Contains(c.Id)).Select(c => c.Rect).ToList();
    }

    // ---- mouse ---------------------------------------------------------------------------------

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (_model is null) return;
        var p = e.GetPosition(_adorner);
        _downScreen = p;

        if (e.ChangedButton == MouseButton.Middle || (e.ChangedButton == MouseButton.Left && _space))
        {
            _mode = Mode.Pan; _fit = false; CaptureMouse(); Cursor = Cursors.SizeAll; e.Handled = true;
            return;
        }
        if (e.ChangedButton != MouseButton.Left) return;

        var (cx, cy) = ToCanvas(p);
        var handle = HandleAt(p);
        if (handle != Handle.None)
        {
            _mode = Mode.Resize; _handle = handle; _resizeId = _model.Selection[0];
            _startRect = _model.Find(_resizeId)?.Rect ?? default;
            CaptureMouse(); e.Handled = true;
            return;
        }

        var pick = Pick(cx, cy);
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        if (pick is null)
        {
            if (!shift) _model.ClearSelection();
            _mode = Mode.Marquee; _adorner.Marquee = new CRect(cx, cy, 0, 0);
        }
        else
        {
            if (shift)
            {
                var next = _model.Selection.ToList();
                if (!next.Remove(pick)) next.Add(pick);
                _model.Select(next);
            }
            else if (!_model.Selection.Contains(pick)) _model.Select([pick]);
            _mode = Mode.Move;
            _clicked = pick;
            _startRects.Clear();
            foreach (var id in _model.Selection) if (_model.Find(id) is { } c) _startRects[id] = c.Rect;
        }
        CaptureMouse();
        Redraw();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_model is null) return;
        var p = e.GetPosition(_adorner);
        _cursor = ToCanvas(p);

        switch (_mode)
        {
            case Mode.Pan:
                _adorner.Origin = new Point(_adorner.Origin.X + (p.X - _downScreen.X), _adorner.Origin.Y + (p.Y - _downScreen.Y));
                _downScreen = p;
                break;

            case Mode.Move:
            {
                var (dx, dy) = Delta(p);
                var primary = _model.Selection.FirstOrDefault();
                var guides = new List<Snap.Guide>();
                if (primary is not null && _startRects.TryGetValue(primary, out var start))
                {
                    var (snapped, g) = Snap.Apply(start.Offset(dx, dy), OtherRects(_startRects.Keys), CanvasRect);
                    dx = snapped.X - start.X; dy = snapped.Y - start.Y;
                    guides.AddRange(g);
                }
                _ghost.Clear();
                foreach (var (id, r) in _startRects) _ghost[id] = r.Offset(dx, dy);
                _adorner.Guides = guides;
                _committed = null;
                break;
            }

            case Mode.Resize:
            {
                var (dx, dy) = Delta(p);
                var guides = new List<Snap.Guide>();
                var (xs, ys) = Snap.Lines(OtherRects([_resizeId!]), CanvasRect);
                var rect = Resized(_startRect, _handle, dx, dy, xs, ys, guides);
                _ghost.Clear();
                _ghost[_resizeId!] = rect;
                _adorner.Guides = guides;
                _committed = rect;
                break;
            }

            case Mode.Marquee:
            {
                var (sx, sy) = ToCanvas(_downScreen);
                _adorner.Marquee = Normalise(sx, sy, _cursor.X, _cursor.Y);
                break;
            }

            default:
                Cursor = _space ? Cursors.SizeAll : CanvasAdorner.CursorFor(HandleAt(p));
                break;
        }
        Redraw();
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (_model is null) { _mode = Mode.None; ReleaseMouseCapture(); return; }

        switch (_mode)
        {
            case Mode.Move:
            {
                // One Move for the whole gesture, so the undo stack gets one entry per drag.
                var primary = _model.Selection.FirstOrDefault();
                var moved = primary is not null && _startRects.TryGetValue(primary, out var s0)
                    && _ghost.TryGetValue(primary, out var g0) && (g0.X != s0.X || g0.Y != s0.Y);
                if (moved) _model.Move(_startRects.Keys.ToList(), _ghost[primary!].X - _startRects[primary!].X,
                                                                  _ghost[primary!].Y - _startRects[primary!].Y);
                // A press on something already in a multi-selection has to keep the whole selection
                // (so the group can be dragged); a click that turns out not to be a drag means the
                // user wanted just that one.
                else if (_clicked is not null && (Keyboard.Modifiers & ModifierKeys.Shift) == 0 && _model.Selection.Count > 1)
                    _model.Select([_clicked]);
                break;
            }

            case Mode.Resize:
                if (_resizeId is not null && _committed is { } r && !r.Equals(_startRect)) _model.Resize(_resizeId, r);
                break;

            case Mode.Marquee:
                if (_adorner.Marquee is { } m)
                {
                    var hits = _model.Layout.Components
                        .Where(c => _hit.TryGetValue(c.Id, out var hr) && hr.Intersects(m))
                        .Select(c => c.Id);
                    var keep = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? _model.Selection.ToList() : new List<string>();
                    foreach (var id in hits) if (!keep.Contains(id)) keep.Add(id);
                    _model.Select(keep);
                }
                break;
        }

        _mode = Mode.None; _handle = Handle.None; _resizeId = null; _committed = null; _clicked = null;
        _ghost.Clear(); _startRects.Clear();
        _adorner.Marquee = null; _adorner.Guides = Array.Empty<Snap.Guide>();
        Cursor = Cursors.Arrow;
        ReleaseMouseCapture();
        Redraw();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        var p = e.GetPosition(_adorner);
        var factor = e.Delta > 0 ? ZoomStep : 1 / ZoomStep;
        var next = Math.Clamp(_adorner.Zoom * factor, MinZoom, MaxZoom);
        if (Math.Abs(next - _adorner.Zoom) < 1e-6) { e.Handled = true; return; }
        // Keep the canvas pixel under the pointer under the pointer.
        _adorner.Origin = new Point(p.X - (p.X - _adorner.Origin.X) * next / _adorner.Zoom,
                                    p.Y - (p.Y - _adorner.Origin.Y) * next / _adorner.Zoom);
        _adorner.Zoom = next;
        _fit = false;
        Redraw();
        e.Handled = true;
    }

    private (int Dx, int Dy) Delta(Point p) => (
        (int)Math.Round((p.X - _downScreen.X) / _adorner.Zoom),
        (int)Math.Round((p.Y - _downScreen.Y) / _adorner.Zoom));

    private static CRect Normalise(int x0, int y0, int x1, int y1)
        => new(Math.Min(x0, x1), Math.Min(y0, y1), Math.Abs(x1 - x0), Math.Abs(y1 - y0));

    /// <summary>The rect a handle drag produces: only the dragged edges move, each snaps on its own
    /// (Snap.Apply would shift the whole rect), and the result is never smaller than 4x4.</summary>
    private static CRect Resized(CRect start, Handle h, int dx, int dy,
        IReadOnlyList<int> xs, IReadOnlyList<int> ys, List<Snap.Guide> guides)
    {
        int x0 = start.X, y0 = start.Y, x1 = start.Right, y1 = start.Bottom;
        var left = h is Handle.TopLeft or Handle.Left or Handle.BottomLeft;
        var right = h is Handle.TopRight or Handle.Right or Handle.BottomRight;
        var top = h is Handle.TopLeft or Handle.Top or Handle.TopRight;
        var bottom = h is Handle.BottomLeft or Handle.Bottom or Handle.BottomRight;

        if (left) { x0 = Snap.Edge(x0 + dx, xs, Snap.Threshold, out var g); if (g is { } v) guides.Add(new Snap.Guide(true, v)); }
        if (right) { x1 = Snap.Edge(x1 + dx, xs, Snap.Threshold, out var g); if (g is { } v) guides.Add(new Snap.Guide(true, v)); }
        if (top) { y0 = Snap.Edge(y0 + dy, ys, Snap.Threshold, out var g); if (g is { } v) guides.Add(new Snap.Guide(false, v)); }
        if (bottom) { y1 = Snap.Edge(y1 + dy, ys, Snap.Threshold, out var g); if (g is { } v) guides.Add(new Snap.Guide(false, v)); }

        if (x1 - x0 < MinSize) { if (left) x0 = x1 - MinSize; else x1 = x0 + MinSize; }
        if (y1 - y0 < MinSize) { if (top) y0 = y1 - MinSize; else y1 = y0 + MinSize; }
        return new CRect(x0, y0, x1 - x0, y1 - y0);
    }

    // ---- keyboard --------------------------------------------------------------------------------

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (_model is null) return;
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        var step = shift ? 10 : 1;

        switch (e.Key)
        {
            case Key.Space when _mode == Mode.None:
                _space = true; Cursor = Cursors.SizeAll; e.Handled = true; return;
            case Key.Escape:
                _model.ClearSelection(); e.Handled = true; return;
            case Key.Delete when _model.Selection.Count > 0:
                _model.Remove(_model.Selection.ToList()); e.Handled = true; return;
            case Key.Left when _model.Selection.Count > 0 && !ctrl:
                _model.Move(_model.Selection.ToList(), -step, 0); e.Handled = true; return;
            case Key.Right when _model.Selection.Count > 0 && !ctrl:
                _model.Move(_model.Selection.ToList(), step, 0); e.Handled = true; return;
            case Key.Up when _model.Selection.Count > 0 && !ctrl:
                _model.Move(_model.Selection.ToList(), 0, -step); e.Handled = true; return;
            case Key.Down when _model.Selection.Count > 0 && !ctrl:
                _model.Move(_model.Selection.ToList(), 0, step); e.Handled = true; return;
            case Key.Z when ctrl:
                _model.Undo(); e.Handled = true; return;
            case Key.Y when ctrl:
                _model.Redo(); e.Handled = true; return;
            case Key.D when ctrl:
                Duplicate(); e.Handled = true; return;
            case Key.OemCloseBrackets when ctrl:
                foreach (var id in _model.Selection.ToList()) _model.BringToFront(id);
                e.Handled = true; return;
            case Key.OemOpenBrackets when ctrl:
                foreach (var id in _model.Selection.ToList()) _model.SendToBack(id);
                e.Handled = true; return;
            case Key.D0 when ctrl:
                _fit = true; Fit(); Redraw(); e.Handled = true; return;
        }
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        base.OnPreviewKeyUp(e);
        if (e.Key != Key.Space) return;
        _space = false;
        if (_mode == Mode.None) Cursor = Cursors.Arrow;
        e.Handled = true;
    }

    // ---- context menu ------------------------------------------------------------------------------

    private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (_model is null) { e.Handled = true; return; }
        // Right-clicking something unselected selects it first, so the menu always acts on what
        // the pointer is over.
        var p = Mouse.GetPosition(_adorner);
        var (cx, cy) = ToCanvas(p);
        if (Pick(cx, cy) is { } id && !_model.Selection.Contains(id)) _model.Select([id]);

        var any = _model.Selection.Count > 0;
        MenuFront.IsEnabled = any; MenuBack.IsEnabled = any;
        MenuDuplicate.IsEnabled = any; MenuDelete.IsEnabled = any;
        MenuAlign.IsEnabled = _model.Selection.Count >= 2;
    }

    private void OnBringToFront(object sender, RoutedEventArgs e)
    {
        if (_model is null) return;
        foreach (var id in _model.Selection.ToList()) _model.BringToFront(id);
    }

    private void OnSendToBack(object sender, RoutedEventArgs e)
    {
        if (_model is null) return;
        foreach (var id in _model.Selection.ToList()) _model.SendToBack(id);
    }

    private void OnDuplicate(object sender, RoutedEventArgs e) => Duplicate();

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (_model is { Selection.Count: > 0 }) _model.Remove(_model.Selection.ToList());
    }

    private void OnAlign(object sender, RoutedEventArgs e)
    {
        if (_model is null || sender is not MenuItem { Tag: string tag }) return;
        if (Enum.TryParse<AlignEdge>(tag, out var edge)) _model.Align(_model.Selection.ToList(), edge);
    }

    private void Duplicate()
    {
        if (_model is not { Selection.Count: > 0 }) return;
        var made = _model.Duplicate(_model.Selection.ToList(), DuplicateOffset, DuplicateOffset);
        if (made.Count > 0) _model.Select(made);
    }
}
