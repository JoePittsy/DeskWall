using System.Diagnostics;
using System.Windows.Threading;
using DeskWall.Core;
using DeskWall.Core.Display;
using DeskWall.Core.Layout;
using DeskWall.Core.Render;
using DeskWall.Core.Resolve;
using DeskWall.Core.Values;
using DeskWall.Core.Widgets;

namespace DeskWall.Designer.Model;

/// <summary>One rendered preview: the pixels Core produced, and the resolved components behind
/// them. The resolved list is the canvas's hit map - its rects are canvas (physical) pixels, the
/// same coordinates the model stores. <paramref name="Problems"/> is what the expansion skipped (a
/// missing or broken widget, an orphan override or knob), for the canvas to show.
/// <para><paramref name="View"/> is null for a whole-canvas frame (<paramref name="Width"/> x
/// <paramref name="Height"/> is the canvas, drawn 1:1 and scaled by WPF at zoom 1 or below). Above
/// zoom 1 it is the viewport the frame was rendered for: the pixels are that pane, at that zoom
/// (plan Task 3.1). <paramref name="Resolved"/> is the 1:1 resolve either way;
/// <paramref name="Drawn"/> is what was painted, which above zoom 1 is the same components
/// transformed into the viewport.</para>
/// <para><paramref name="BaseImage"/> is the base photo this frame resolved to (a bound
/// <c>baseImage</c> follows the live tree), empty when there was none.</para></summary>
public sealed record PreviewFrame(int Width, int Height, byte[] Bgra, IReadOnlyList<Resolved> Resolved, TimeSpan RenderTime,
    IReadOnlyList<ExpandProblem> Problems, Viewport? View = null, IReadOnlyList<Resolved>? Drawn = null, string BaseImage = "");

/// <summary>Turns the model into pixels on a background thread and hands the frame to the UI.
/// Coalesces bursts: requests inside 50 ms collapse into one, at most one render is in flight and
/// the latest request wins. Also produces the hit map (component rects in canvas pixels) the canvas
/// uses for picking.
/// <para>
/// <see cref="Request"/> snapshots the document as JSON on the calling thread, so the render thread
/// never reads a <see cref="DesignerModel"/> the UI thread may be mutating. Errors (an unreadable
/// base image, a bad binding, a duplicate id) do not throw at the caller: they come back as a frame
/// with the message drawn into it by Core, so the user reads the fault where the preview should be.
/// </para></summary>
public sealed class PreviewRenderer : IDisposable
{
    /// <summary>Coalescing window. Long enough to swallow a drag's worth of requests, short enough
    /// that a single edit still feels immediate.</summary>
    public const int DebounceMs = 50;

    private readonly Func<RecordValue> _valueTree;
    private readonly Dispatcher? _dispatcher;
    private readonly Timer _debounce;
    private readonly object _gate = new();

    private Snapshot? _latest;
    private bool _rendering;
    private bool _pending;
    private bool _disposed;

    /// <summary>What a render needs, captured at request time. <paramref name="Find"/> is
    /// <see cref="DesignerModel.Finder"/>, which copies the widget overlay when it is called, so the
    /// render thread expands against the widgets as they were at the request.</summary>
    private sealed record Snapshot(string Json, DisplaySignature Signature, Func<string, WidgetTemplate?> Find, Viewport? View);

    private Viewport? _view;

    /// <param name="valueTree">the live values to resolve bindings against; ValueTree.Empty before
    /// any source has run.</param>
    public PreviewRenderer(Func<RecordValue> valueTree)
    {
        _valueTree = valueTree;
        // FromThread, not CurrentDispatcher: CurrentDispatcher would manufacture a dispatcher on a
        // test thread that has no message loop, and every frame would then be queued and lost.
        _dispatcher = Dispatcher.FromThread(Thread.CurrentThread);
        _debounce = new Timer(_ => Kick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Raised on the thread that constructed the renderer when one has a dispatcher
    /// (the UI thread), otherwise on the render thread.</summary>
    public event Action<PreviewFrame>? Rendered;

    /// <summary>Ask for a frame at the viewport last passed to <see cref="Request(DesignerModel, Viewport?)"/>
    /// (none: the whole canvas). Safe from any thread; cheap enough to call on every mouse move.</summary>
    public void Request(DesignerModel model)
    {
        Viewport? view;
        lock (_gate) view = _view;
        Request(model, view);
    }

    /// <summary>Ask for a frame for <paramref name="view"/>, and remember it for later
    /// <see cref="Request(DesignerModel)"/> calls. At zoom 1 or below (or null) the frame is the whole
    /// canvas; above, it is the viewport rendered at its zoom.</summary>
    public void Request(DesignerModel model, Viewport? view)
    {
        ArgumentNullException.ThrowIfNull(model);
        var snap = new Snapshot(model.ToJson(), model.Signature, model.Finder(), view);
        lock (_gate)
        {
            if (_disposed) return;
            _view = view;
            _latest = snap;
            _debounce.Change(DebounceMs, Timeout.Infinite);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _debounce.Dispose();
        }
    }

    // ---- the one-at-a-time render loop -----------------------------------------------------

    private void Kick()
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_rendering) { _pending = true; return; }   // the loop will pick the latest up
            _rendering = true;
        }
        Task.Run(Loop);
    }

    private void Loop()
    {
        while (true)
        {
            Snapshot snap;
            lock (_gate)
            {
                if (_disposed || _latest is null) { _rendering = false; return; }
                snap = _latest;
            }

            PreviewFrame frame;
            try { frame = Render(snap); }
            catch (Exception ex) { frame = Fallback(snap, ex); }
            Raise(frame);

            lock (_gate)
            {
                if (_disposed || !_pending) { _rendering = false; return; }
                _pending = false;
            }
        }
    }

    private void Raise(PreviewFrame frame)
    {
        var handler = Rendered;
        if (handler is null) return;
        if (_dispatcher is not null && !_dispatcher.HasShutdownStarted)
            _dispatcher.BeginInvoke(new Action(() => handler(frame)));
        else
            handler(frame);
    }

    // ---- the render itself -----------------------------------------------------------------

    private PreviewFrame Render(Snapshot snap)
    {
        var sw = Stopwatch.StartNew();
        var w = Math.Max(1, snap.Signature.Width);
        var h = Math.Max(1, snap.Signature.Height);
        var canvas = new Rect(0, 0, w, h);

        LayoutFile? layout = null;
        IReadOnlyList<Resolved> resolved = Array.Empty<Resolved>();
        string? error = null;

        IReadOnlyList<ExpandProblem> problems = Array.Empty<ExpandProblem>();

        try { layout = LayoutFile.Parse(snap.Json); }
        catch (Exception ex) { error = Describe("layout", ex); }

        // Before resolve, as the daemon does at load (plan D4): resolve and render only ever see
        // components. A missing or broken widget is a problem in the frame, never an exception.
        if (layout is not null)
        {
            try
            {
                var expansion = WidgetExpander.Expand(layout, snap.Find);
                layout = expansion.Layout;
                problems = expansion.Problems;
            }
            catch (Exception ex) { error = Describe("widgets", ex); }
        }

        var tree = _valueTree();
        if (layout is not null && error is null)
        {
            try { resolved = LayoutResolver.Resolve(layout, tree); }
            catch (Exception ex) { error = Describe("resolve", ex); }
        }

        string? baseRaw = null;
        // Resolved against the same tree as the components, so a bound base (one photo per
        // time.phase) swaps in the preview as the live values move, exactly as the tick swaps it.
        var basePath = layout is null ? "" : LayoutResolver.BaseImagePath(layout, tree);
        // No base image at all is not a fault: a widget document has none on purpose, and it draws
        // on the same flat grey a broken one falls back to. Only a path that was given and did not
        // work is worth a message painted over the canvas, once per render.
        if (layout is not null && error is null && basePath.Length > 0)
        {
            try { baseRaw = BaseCache.Ensure(basePath, w, h, layout.BaseFit); }
            catch (Exception ex) { error = Describe("base image", ex); }
        }

        if (snap.View is { Zoom: > 1 } view && error is null && layout is not null)
            return RenderView(snap, view, layout, baseRaw, resolved, problems, sw) with { BaseImage = basePath };

        Surface? frame = null;
        try
        {
            if (baseRaw is not null)
            {
                try { frame = new FrameRenderer(w, h).RenderAll(baseRaw, resolved); }
                catch (Exception ex) { error = Describe("render", ex); }
            }
            // No base, or the full render threw: draw what resolved onto flat grey so the layout is
            // still editable while the user fixes whatever is wrong.
            frame ??= Flat(w, h, resolved);
            if (error is not null) DrawError(frame, error);
            var bgra = new byte[w * 4 * h];
            frame.CopyTo(bgra);
            return new PreviewFrame(w, h, bgra, resolved, sw.Elapsed, problems, null, resolved, basePath);
        }
        finally { frame?.Dispose(); }
    }

    /// <summary>Above zoom 1: the layout transformed into the viewport (<see cref="LayoutScaler.Transform"/>),
    /// resolved, and painted onto a viewport-sized surface over the base stretched to the zoomed
    /// canvas, which Direct2D clips to the surface. Text, arcs and bars are drawn at the zoom, so
    /// they stay crisp; only the photograph is upscaled. The hit map stays the 1:1 resolve.</summary>
    private PreviewFrame RenderView(Snapshot snap, Viewport view, LayoutFile layout, string? baseRaw,
        IReadOnlyList<Resolved> resolved, IReadOnlyList<ExpandProblem> problems, Stopwatch sw)
    {
        var w = Math.Max(1, (int)Math.Ceiling(view.Width));
        var h = Math.Max(1, (int)Math.Ceiling(view.Height));
        int ox = (int)Math.Round(view.OriginX), oy = (int)Math.Round(view.OriginY);
        var canvas = new Rect(ox, oy, (int)Math.Round(snap.Signature.Width * view.Zoom), (int)Math.Round(snap.Signature.Height * view.Zoom));
        var pane = new Rect(0, 0, w, h);
        string? error = null;
        IReadOnlyList<Resolved> drawn = Array.Empty<Resolved>();
        try { drawn = LayoutResolver.Resolve(LayoutScaler.Transform(layout, view.Zoom, ox, oy), _valueTree()); }
        catch (Exception ex) { error = Describe("resolve", ex); }

        using var frame = Surface.Create(w, h);
        if (baseRaw is not null)
        {
            try
            {
                using var photo = Surface.LoadRaw(baseRaw);
                frame.DrawSurface(photo, canvas, Fit.Stretch, resample: Resample.Fast);
            }
            catch (Exception ex) { error ??= Describe("base image", ex); }
        }
        else frame.FillRect(canvas, new Color(255, 32, 32, 32));

        foreach (var c in drawn.OrderBy(c => c.Z))
        {
            // Off the pane entirely: skip it. The margin is the rect's own height, which covers a
            // text shadow ring and a trailing run wider than its box without measuring the text.
            var r = c.Rect;
            if (!new Rect(r.X - r.H, r.Y - r.H, r.W + 2 * r.H, r.H * 3).Intersects(pane)) continue;
            try { FrameRenderer.Draw(frame, c); }
            catch (Exception ex) { Debug.WriteLine($"preview: component '{c.Id}' failed: {ex.Message}"); }
        }
        if (error is not null) DrawError(frame, error);
        var bgra = new byte[w * 4 * h];
        frame.CopyTo(bgra);
        return new PreviewFrame(w, h, bgra, resolved, sw.Elapsed, problems, view, drawn);
    }

    /// <summary>Last resort: even producing the error frame failed (out of memory, a COM fault).
    /// Hand back an empty frame of the right size rather than killing the render loop.</summary>
    private static PreviewFrame Fallback(Snapshot snap, Exception ex)
    {
        Debug.WriteLine($"preview render failed: {ex}");
        var w = Math.Max(1, snap.Signature.Width);
        var h = Math.Max(1, snap.Signature.Height);
        return new PreviewFrame(w, h, new byte[w * 4 * h], Array.Empty<Resolved>(), TimeSpan.Zero, Array.Empty<ExpandProblem>());
    }

    private static Surface Flat(int w, int h, IReadOnlyList<Resolved> resolved)
    {
        var s = Surface.Create(w, h);
        try
        {
            s.Clear(new Color(255, 32, 32, 32));
            foreach (var c in resolved.OrderBy(c => c.Z))
            {
                // One bad component (a missing image, an unparseable colour) must not cost the
                // user the rest of the preview.
                try { FrameRenderer.Draw(s, c); }
                catch (Exception ex) { Debug.WriteLine($"preview: component '{c.Id}' failed: {ex.Message}"); }
            }
            return s;
        }
        catch { s.Dispose(); throw; }
    }

    private static void DrawError(Surface frame, string message)
    {
        var style = new TextStyle("Segoe UI", 20, 600, Color.White, Align.Left, TextEffect.Plate, 4, new Color(230, 140, 20, 20));
        var box = new Rect(32, 32, Math.Max(64, frame.Width - 64), Math.Min(frame.Height - 64, 160));
        if (box.W <= 0 || box.H <= 0) return;
        frame.DrawText(message, style, box);
    }

    private static string Describe(string stage, Exception ex) => $"{stage}: {ex.Message}";
}
