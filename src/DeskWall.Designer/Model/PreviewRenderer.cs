using System.Diagnostics;
using System.Windows.Threading;
using DeskWall.Core;
using DeskWall.Core.Display;
using DeskWall.Core.Layout;
using DeskWall.Core.Render;
using DeskWall.Core.Resolve;
using DeskWall.Core.Values;

namespace DeskWall.Designer.Model;

/// <summary>One rendered preview: the pixels Core produced, and the resolved components behind
/// them. The resolved list is the canvas's hit map - its rects are canvas (physical) pixels, the
/// same coordinates the model stores.</summary>
public sealed record PreviewFrame(int Width, int Height, byte[] Bgra, IReadOnlyList<Resolved> Resolved, TimeSpan RenderTime);

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

    /// <summary>What a render needs, captured at request time.</summary>
    private sealed record Snapshot(string Json, DisplaySignature Signature);

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

    /// <summary>Ask for a frame. Safe from any thread; cheap enough to call on every mouse move.</summary>
    public void Request(DesignerModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var snap = new Snapshot(model.ToJson(), model.Signature);
        lock (_gate)
        {
            if (_disposed) return;
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

        try { layout = LayoutFile.Parse(snap.Json); }
        catch (Exception ex) { error = Describe("layout", ex); }

        if (layout is not null)
        {
            try { resolved = LayoutResolver.Resolve(layout, _valueTree(), canvas); }
            catch (Exception ex) { error = Describe("resolve", ex); }
        }

        string? baseRaw = null;
        if (layout is not null && error is null)
        {
            try { baseRaw = BaseCache.Ensure(layout.BaseImage, w, h, layout.BaseFit); }
            catch (Exception ex) { error = Describe("base image", ex); }
        }

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
            return new PreviewFrame(w, h, bgra, resolved, sw.Elapsed);
        }
        finally { frame?.Dispose(); }
    }

    /// <summary>Last resort: even producing the error frame failed (out of memory, a COM fault).
    /// Hand back an empty frame of the right size rather than killing the render loop.</summary>
    private static PreviewFrame Fallback(Snapshot snap, Exception ex)
    {
        Debug.WriteLine($"preview render failed: {ex}");
        var w = Math.Max(1, snap.Signature.Width);
        var h = Math.Max(1, snap.Signature.Height);
        return new PreviewFrame(w, h, new byte[w * 4 * h], Array.Empty<Resolved>(), TimeSpan.Zero);
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
