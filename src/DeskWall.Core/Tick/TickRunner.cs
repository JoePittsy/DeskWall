using System.Diagnostics;
using DeskWall.Core.Display;
using DeskWall.Core.Layout;
using DeskWall.Core.Render;
using DeskWall.Core.Resolve;
using DeskWall.Core.Scheduling;
using DeskWall.Core.Shortcuts;
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
    string? framePath = null,
    RemoteImageCache? images = null,
    ShortcutManager? shortcuts = null)
{
    private readonly string _statePath = statePath ?? Paths.InRuntime("frame-state.json");
    private readonly string _outPath = outPath ?? Paths.InRuntime(layout.Encode == "png" ? "deskwall.png" : "deskwall.jpg");
    private readonly string _framePath = framePath ?? Paths.InRuntime("frame.raw");

    /// <summary>Resolved shortcuts from the last run; Phase 3's manager consumes them.</summary>
    public IReadOnlyList<ResolvedShortcut> LastShortcuts { get; private set; } = [];

    /// <summary>What stage 7 did on the last run, or null when there is no manager or nothing changed.</summary>
    public ShortcutOutcome? LastShortcutOutcome { get; private set; }

    public async Task<TickTimings> RunAsync(bool force, bool apply, CancellationToken ct)
    {
        var t = new TickTimings();
        var sw = Stopwatch.StartNew();
        // Environment.CpuUsage reads the process times without opening a kernel handle; the old
        // Process.GetCurrentProcess() leaked one SafeProcessHandle per tick (spec 1.2: under 100 handles).
        var cpu0 = Environment.CpuUsage.TotalTime;
        var now = clock.Now;

        // 1. refresh due sources
        foreach (var s in sources)
        {
            var snap = registry.Get(s.Name);
            // Scheduler.IsDue, not NextDue: a failing source is on a backed-off schedule and the wake
            // maths and this gate must agree exactly (finding 1).
            if (!force && !Scheduler.IsDue(s, snap, now)) continue;
            try { registry.Set(snap.Succeeded(await s.RefreshAsync(ct).ConfigureAwait(false), now)); }
            catch (Exception ex) { registry.Set(snap.Failed(ex.Message, now)); }
        }

        // 2. resolve + diff
        var canvas = new Rect(0, 0, monitor.Bounds.W, monitor.Bounds.H);
        // Tree(sources, now) drops a source that has missed registry.StaleAfter of its own schedules
        // (finding 15); with StaleAfter left at its default 0 it is exactly the old Tree().
        var resolved = MapRemoteImages(LayoutResolver.Resolve(layout, registry.Tree(sources, now), images is null ? null : images.Lookup));
        LastShortcuts = resolved.OfType<ResolvedShortcut>().ToList();
        var state = FrameState.Load(_statePath);
        var changed = resolved.Where(c => force || !state.KeysById.TryGetValue(c.Id, out var k) || k != c.ContentKey).ToList();
        var liveIds = resolved.Select(c => c.Id).ToHashSet();
        var removed = state.KeysById.Keys.Any(id => !liveIds.Contains(id));
        // Finding 12: BaseCache.KeyFor only stats the file (no decode), so this stays cheap enough
        // to sit before the skip gate, which must run before any drawing - a replaced base image
        // must never be treated as "same signature".
        var baseKey = BaseCache.KeyFor(layout.BaseImage, canvas.W, canvas.H, layout.BaseFit);
        var sameSig = state.SignatureKey == monitor.Signature.Key && state.BaseKey == baseKey && File.Exists(_framePath) && File.Exists(_outPath);
        t.ResolveMs = sw.ElapsedMilliseconds;

        if (!force && changed.Count == 0 && !removed && sameSig)
        {
            t.Skipped = true;
            t.TotalMs = sw.ElapsedMilliseconds;
            t.CpuMs = (Environment.CpuUsage.TotalTime - cpu0).TotalMilliseconds;
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
            int drawn;
            try
            {
                frame = renderer.RenderIncremental(previous, baseRaw, resolved, changedIds, prevRects, out drawn);   // mutates previous in place
            }
            catch
            {
                // Finding 7: RenderIncremental can throw before returning (a size mismatch against
                // a stale frame.raw at a different canvas size under the same signature key); the
                // 19.8 MB (at 3440x1440) bitmap LoadRaw just produced must not leak.
                previous.Dispose();
                throw;
            }
            t.Redrawn = drawn;   // finding 13: what was painted, not what changed
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

        // 6. shortcuts: make the desktop icons match. Skipped unless the fingerprint moved, because
        // Reconcile talks to Explorer and costs far more than the rest of a tick put together.
        var s0 = sw.ElapsedMilliseconds;
        LastShortcutOutcome = null;
        if (shortcuts is not null)
        {
            try
            {
                // Inside the try: Fingerprint throws on a duplicate slot (two shortcut components, or a
                // standalone one colliding with a repeater's base), and a layout mistake must not make
                // every tick throw after the wallpaper is applied and before state.Save.
                var fingerprint = shortcuts.Fingerprint(LastShortcuts, monitor.Signature.ScalePercent);
                if (force || fingerprint != state.ShortcutsFingerprint)
                {
                    var outcome = shortcuts.Reconcile(LastShortcuts, monitor.Signature.ScalePercent);
                    LastShortcutOutcome = outcome;
                    // A slot that could not be written or positioned leaves the fingerprint unstored,
                    // so the next tick reconciles again instead of the icon staying missing until the
                    // game list or the layout happens to change.
                    state.ShortcutsFingerprint = outcome.SlotFailed ? "" : fingerprint;
                }
            }
            catch (Exception ex)
            {
                // The desktop view can be gone (Explorer restarting). Record it and retry next tick.
                LastShortcutOutcome = new ShortcutOutcome(0, 0, 0, [$"{ex.GetType().Name}: {ex.Message}"]) { SlotFailed = true };
                state.ShortcutsFingerprint = "";
            }
        }
        t.ShortcutsMs = sw.ElapsedMilliseconds - s0;

        // 7. state
        state.KeysById = resolved.ToDictionary(c => c.Id, c => c.ContentKey);
        // PaintBounds, not Rect: the next tick restores the base over these, and text paints outside its rect.
        state.RectsById = resolved.ToDictionary(c => c.Id, c => new[] { c.PaintBounds.X, c.PaintBounds.Y, c.PaintBounds.W, c.PaintBounds.H });
        state.SignatureKey = monitor.Signature.Key;
        state.FramePath = _framePath;
        state.BaseKey = baseKey;
        state.Save(_statePath);

        t.TotalMs = sw.ElapsedMilliseconds;
        t.CpuMs = (Environment.CpuUsage.TotalTime - cpu0).TotalMilliseconds;
        return t;
    }

    /// <summary>Replace every ResolvedImage whose Path is a remote URL with the cache's local file
    /// (or "" on a miss, so FrameRenderer draws the missing-image plate until the download lands).</summary>
    private IReadOnlyList<Resolved> MapRemoteImages(IReadOnlyList<Resolved> all)
    {
        if (images is null) return all;
        var outList = new List<Resolved>(all.Count);
        foreach (var c in all)
        {
            if (c is ResolvedImage img && RemoteImageCache.IsRemote(img.Path))
            {
                var local = images.Lookup(img.Path) ?? "";
                var mapped = img with { Path = local };
                outList.Add(mapped with { ContentKey = ContentKey.Of(mapped) });
            }
            else outList.Add(c);
        }
        return outList;
    }
}
