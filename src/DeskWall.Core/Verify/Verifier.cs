using DeskWall.Core.Display;
using DeskWall.Core.Layout;
using DeskWall.Core.Render;
using DeskWall.Core.Resolve;
using DeskWall.Core.Shortcuts;
using DeskWall.Core.Sources;

namespace DeskWall.Core.Verify;

/// <summary>poc/verify.ps1 grown up: screenshot the live desktop, diff it against the frame the tick
/// composed, and measure where the shell actually drew each shortcut-arrow overlay inside its cover.
/// <para>
/// The measurement is the point. Nothing here trusts what the shell reported through
/// <see cref="DesktopView.GetPosition"/>: that is recorded beside the pixels and never used to decide
/// whether a slot passed. A command, never on the tick path - it borrows the desktop for a second or two.
/// </para></summary>
public static class Verifier
{
    /// <summary>The right-hand strip saved for eyeballing, in canvas pixels. Windows sit centred on the
    /// owner's ultrawide and leave about 440 px each side, so 400 is the whole widget column.</summary>
    public const int ColumnWidth = 400;

    /// <summary>The clock crop is saved at this magnification: the question it answers is whether thin
    /// light text stays legible over the photo, which 1:1 pixels do not settle.</summary>
    public const int ClockZoom = 8;

    /// <summary>JPEG ringing follows every hard edge in the composed frame, so the cover's own border is
    /// never compared. The POC used the same 2 px.</summary>
    private const int Inset = 2;

    private const int SettleMs = 800;

    private static readonly TimeSpan RefreshTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Pure: find the arrow overlay for one cover and measure its padding.
    /// <para>
    /// The arrow is whatever differs between the screenshot and the composed frame inside the cover,
    /// inset by 2 px. A fully transparent icon paints nothing else, so the diff box is the arrow. Pads
    /// are -1 and <see cref="SlotCheck.Ok"/> is false when nothing differs: a missing icon must not read
    /// as a pad of zero.
    /// </para></summary>
    public static SlotCheck CheckSlot(Surface shot, Surface composed, ResolvedShortcut s,
        (int X, int Y) wanted, (int X, int Y)? got, int wantPad, int threshold)
    {
        ArgumentNullException.ThrowIfNull(shot);
        ArgumentNullException.ThrowIfNull(composed);
        ArgumentNullException.ThrowIfNull(s);

        var cover = s.Rect;
        var probe = new Rect(cover.X + Inset, cover.Y + Inset, cover.W - 2 * Inset, cover.H - 2 * Inset);
        var box = Calibrator.DiffBounds(shot, composed, probe, threshold);
        if (box is not { } arrow)
            return new SlotCheck(s.Slot, s.Id, cover, wanted, got, null, -1, -1, false, "NO ICON FOUND");

        var left = arrow.X - cover.X;
        var bottom = cover.Bottom - arrow.Bottom;
        var ok = left == wantPad && bottom == wantPad;
        var note = ok ? null : $"PAD OFF BY ({left - wantPad},{bottom - wantPad})";
        return new SlotCheck(s.Slot, s.Id, cover, wanted, got, arrow, left, bottom, ok, note);
    }

    /// <summary>Live. Minimises every window, captures the primary monitor, compares it with
    /// <c>frame.raw</c>, checks every shortcut in the resolved layout, writes the right-hand column and
    /// the zoomed clock to the runtime dir and un-minimises. Leaves the desktop as it found it: nothing
    /// here writes a shortcut, moves an icon or touches the wallpaper.</summary>
    public static VerifyReport Run(int wantPad = ShortcutPlan.DefaultPad, int threshold = 60, Action<string>? log = null)
    {
        var say = log ?? (_ => { });
        var monitor = Monitors.Enumerate().FirstOrDefault(m => m.IsPrimary)
            ?? throw new InvalidOperationException("no primary monitor");
        var resolution = LayoutStore.Default(say).Resolve(monitor.Signature)
            ?? throw new InvalidOperationException(
                $"no layout for {monitor.Signature.Key}; use: deskwall layouts set <path>");
        var layout = resolution.Layout;
        say($"display {monitor.Signature.Key}, layout {resolution.SourcePath}{(resolution.Scaled ? " (scaled)" : "")}");

        var resolved = ResolveNow(layout, say);
        var shortcuts = ShortcutPlan.Ordered(resolved.OfType<ResolvedShortcut>().ToList());

        var framePath = Paths.InRuntime("frame.raw");
        if (!File.Exists(framePath))
            throw new InvalidOperationException($"{framePath} is missing: run a tick before verifying");

        var calibration = Calibration.Load();
        var iconSize = DesktopView.IconSize();
        var scale = monitor.Signature.ScalePercent;
        var arrowRect = calibration.Get(iconSize, scale);
        if (arrowRect is null)
        {
            arrowRect = calibration.Get(48, 100)!;
            say($"no calibration for {Calibration.Key(iconSize, scale)}; using {Calibration.Key(48, 100)}. Run 'deskwall calibrate'.");
        }

        // Read the shell's own idea of each position BEFORE minimising: GetPosition re-acquires the
        // folder view, and doing that while the desktop is coming forward is a needless race.
        var desktop = ShortcutFiles.DesktopDir();
        var reported = new Dictionary<int, (int X, int Y)?>();
        foreach (var s in shortcuts)
            reported[s.Slot] = DesktopView.GetPosition(Path.Combine(desktop, ShortcutPlan.SlotFileName(s.Slot)));

        using var composed = Surface.LoadRaw(framePath);
        if (composed.Width != monitor.Bounds.W || composed.Height != monitor.Bounds.H)
            throw new InvalidOperationException(
                $"{framePath} is {composed.Width}x{composed.Height} but the primary monitor is " +
                $"{monitor.Bounds.W}x{monitor.Bounds.H}: run a tick at this resolution first");

        Surface shot;
        ShellDesktop.MinimizeAll();
        try
        {
            Thread.Sleep(SettleMs);
            shot = Screenshot.Capture(monitor.Bounds);
        }
        finally { ShellDesktop.UndoMinimizeAll(); }

        using (shot)
        {
            var checks = new List<SlotCheck>(shortcuts.Count);
            foreach (var s in shortcuts)
            {
                var wanted = ShortcutPlan.IconPosition(s.Rect, arrowRect, wantPad);
                var check = CheckSlot(shot, composed, s, wanted, reported[s.Slot], wantPad, threshold);
                say(check.ToLine());
                checks.Add(check);
            }

            var columnPath = SaveColumn(shot);
            var clockPath = SaveClockCrop(shot, resolved);
            return new VerifyReport(monitor.Signature, checks, columnPath, clockPath,
                checks.Count > 0 && checks.All(c => c.Ok));
        }
    }

    /// <summary>The layout resolved against values read right now. The running daemon's registry is in
    /// another process, so every source is refreshed once here, each bounded to five seconds; a source
    /// that fails or times out simply contributes nothing, exactly as it would in a tick.</summary>
    private static IReadOnlyList<Resolved> ResolveNow(LayoutFile layout, Action<string> say)
    {
        var clock = SystemClock.Instance;
        var registry = new SourceRegistry();
        foreach (var source in layout.Sources.Select(d => SourceFactory.Create(d, clock)))
        {
            var snap = registry.Get(source.Name);
            using var cts = new CancellationTokenSource(RefreshTimeout);
            try
            {
                var values = source.RefreshAsync(cts.Token).AsTask().GetAwaiter().GetResult();
                registry.Set(snap.Succeeded(values, clock.Now));
            }
            catch (Exception ex)
            {
                registry.Set(snap.Failed(ex.Message, clock.Now));
                say($"source {source.Name} failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
        return LayoutResolver.Resolve(layout, registry.Tree());
    }

    private static string SaveColumn(Surface shot)
    {
        var w = Math.Min(ColumnWidth, shot.Width);
        var path = Paths.InRuntime("verify-desktop.png");
        using var column = Zoom(shot, new Rect(shot.Width - w, 0, w, shot.Height), 1);
        column.SavePng(path);
        return path;
    }

    /// <summary>An 8x nearest-neighbour crop of the component whose id is "clock", so thin light text
    /// over the photo can be judged. Nearest, not linear: the question is what the renderer produced,
    /// and a resampler that invents intermediate pixels answers a different one.</summary>
    private static string SaveClockCrop(Surface shot, IReadOnlyList<Resolved> resolved)
    {
        var clock = resolved.OfType<ResolvedText>()
            .FirstOrDefault(t => t.Id.Equals("clock", StringComparison.OrdinalIgnoreCase));
        if (clock is null) return "";
        var r = Intersect(clock.PaintBounds, new Rect(0, 0, shot.Width, shot.Height));
        if (r.W <= 0 || r.H <= 0) return "";
        var path = Paths.InRuntime("clock-now.png");
        using var crop = Zoom(shot, r, ClockZoom);
        crop.SavePng(path);
        return path;
    }

    private static Rect Intersect(Rect a, Rect b)
    {
        var x = Math.Max(a.X, b.X);
        var y = Math.Max(a.Y, b.Y);
        return new Rect(x, y, Math.Min(a.Right, b.Right) - x, Math.Min(a.Bottom, b.Bottom) - y);
    }

    /// <summary>Crop (factor 1) or nearest-neighbour magnify a region of a surface.</summary>
    private static Surface Zoom(Surface src, Rect r, int factor)
    {
        var rowBytes = r.W * 4;
        var pixels = new byte[rowBytes * r.H];
        src.ReadRegion(r, pixels);
        if (factor == 1) return Surface.FromBgra(r.W, r.H, pixels);

        int dw = r.W * factor, dh = r.H * factor;
        var big = new byte[dw * dh * 4];
        for (var y = 0; y < dh; y++)
        {
            var srcRow = y / factor * rowBytes;
            var dstRow = y * dw * 4;
            for (var x = 0; x < dw; x++)
                Buffer.BlockCopy(pixels, srcRow + x / factor * 4, big, dstRow + x * 4, 4);
        }
        return Surface.FromBgra(dw, dh, big);
    }
}
