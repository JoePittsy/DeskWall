using System.Diagnostics;
using DeskWall.Core.Display;
using DeskWall.Core.Layout;
using DeskWall.Core.Render;
using DeskWall.Core.Resolve;
using DeskWall.Core.Sources;
using DeskWall.Core.Wallpaper;

namespace DeskWall.Core.Tick;

/// <summary>One tick: refresh due sources, resolve, diff against the last frame, render, encode,
/// apply. Spec section 6. Stateless between calls except for the files it writes.</summary>
public sealed class TickRunner(
    LayoutFile layout,
    IReadOnlyList<ISource> sources,
    SourceRegistry registry,
    IClock clock,
    MonitorInfo monitor,
    string? statePath = null,
    string? outPath = null,
    string? framePath = null)
{
    private readonly string _statePath = statePath ?? Paths.InRuntime("frame-state.json");
    private readonly string _outPath = outPath ?? Paths.InRuntime(layout.Encode == "png" ? "deskwall.png" : "deskwall.jpg");
    private readonly string _framePath = framePath ?? Paths.InRuntime("frame.raw");

    /// <summary>Resolved shortcuts from the last run; Phase 3's manager consumes them.</summary>
    public IReadOnlyList<ResolvedShortcut> LastShortcuts { get; private set; } = [];

    public async Task<TickTimings> RunAsync(bool force, bool apply, CancellationToken ct)
    {
        var t = new TickTimings();
        var sw = Stopwatch.StartNew();
        var proc = Process.GetCurrentProcess();
        var cpu0 = proc.TotalProcessorTime;
        var now = clock.Now;

        // 1. refresh due sources
        foreach (var s in sources)
        {
            var snap = registry.Get(s.Name);
            if (!force && s.NextDue(snap.LastRefresh, now) > now) continue;
            try { registry.Set(snap.Succeeded(await s.RefreshAsync(ct).ConfigureAwait(false), now)); }
            catch (Exception ex) { registry.Set(snap.Failed(ex.Message)); }
        }

        // 2. resolve + diff
        var canvas = new Rect(0, 0, monitor.Bounds.W, monitor.Bounds.H);
        var resolved = LayoutResolver.Resolve(layout, registry.Tree(), canvas);
        LastShortcuts = resolved.OfType<ResolvedShortcut>().ToList();
        var state = FrameState.Load(_statePath);
        var changed = resolved.Where(c => force || !state.KeysById.TryGetValue(c.Id, out var k) || k != c.ContentKey).ToList();
        var liveIds = resolved.Select(c => c.Id).ToHashSet();
        var removed = state.KeysById.Keys.Any(id => !liveIds.Contains(id));
        var sameSig = state.SignatureKey == monitor.Signature.Key && File.Exists(_framePath) && File.Exists(_outPath);
        t.ResolveMs = sw.ElapsedMilliseconds;

        if (!force && changed.Count == 0 && !removed && sameSig)
        {
            t.Skipped = true;
            t.TotalMs = sw.ElapsedMilliseconds;
            proc.Refresh();
            t.CpuMs = (proc.TotalProcessorTime - cpu0).TotalMilliseconds;
            return t;
        }

        // 3. draw: full render when forced or the display changed, otherwise only the dirty rects
        var d0 = sw.ElapsedMilliseconds;
        var baseRaw = BaseCache.Ensure(layout.BaseImage, canvas.W, canvas.H, layout.BaseFit);
        var renderer = new FrameRenderer(canvas.W, canvas.H);
        Surface frame;
        if (force || !sameSig)
        {
            frame = renderer.RenderAll(baseRaw, resolved);
            t.Redrawn = resolved.Count(c => c is not ResolvedShortcut);
        }
        else
        {
            var changedIds = changed.Select(c => c.Id).ToHashSet();
            var prevRects = state.RectsById.ToDictionary(kv => kv.Key, kv => new Rect(kv.Value[0], kv.Value[1], kv.Value[2], kv.Value[3]));
            var previous = Surface.LoadRaw(_framePath);
            frame = renderer.RenderIncremental(previous, baseRaw, resolved, changedIds, prevRects);   // mutates previous in place
            t.Redrawn = changedIds.Count;
        }
        using (frame)
        {
            frame.SaveRaw(_framePath);
            t.DrawMs = sw.ElapsedMilliseconds - d0;

            // 4. encode
            var e0 = sw.ElapsedMilliseconds;
            if (layout.Encode == "png") frame.SavePng(_outPath); else frame.SaveJpeg(_outPath, layout.JpegQuality);
            t.EncodeMs = sw.ElapsedMilliseconds - e0;
        }

        // 5. apply
        var a0 = sw.ElapsedMilliseconds;
        if (apply) WallpaperSetter.Set(monitor.WallpaperMonitorId, _outPath);
        t.ApplyMs = sw.ElapsedMilliseconds - a0;

        // 6. shortcuts: Phase 3. Timed so the table shape is final now.
        var s0 = sw.ElapsedMilliseconds;
        t.ShortcutsMs = sw.ElapsedMilliseconds - s0;

        // 7. state
        state.KeysById = resolved.ToDictionary(c => c.Id, c => c.ContentKey);
        state.RectsById = resolved.ToDictionary(c => c.Id, c => new[] { c.Rect.X, c.Rect.Y, c.Rect.W, c.Rect.H });
        state.SignatureKey = monitor.Signature.Key;
        state.FramePath = _framePath;
        state.Save(_statePath);

        t.TotalMs = sw.ElapsedMilliseconds;
        proc.Refresh();
        t.CpuMs = (proc.TotalProcessorTime - cpu0).TotalMilliseconds;
        return t;
    }
}
