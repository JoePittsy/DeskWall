using DeskWall.Core.Display;
using DeskWall.Core.Render;
using DeskWall.Core.Wallpaper;

namespace DeskWall.Core.Shortcuts;

public sealed record CalibrationResult(int IconSize, int ScalePercent, ArrowRect Arrow, int ItemX, int ItemY, int Pixels);

/// <summary>Measures where the shell draws the shortcut-arrow overlay, by painting a flat probe into
/// the wallpaper, putting one transparent-icon shortcut on it at a known position, and diffing a
/// screenshot against the wallpaper it was painted from. The POC did this by hand; this is the same
/// method with the numbers derived rather than typed in.</summary>
public static class Calibrator
{
    private const int ProbeW = 400, ProbeH = 300, ItemInset = 100;

    /// <summary>Fatal, not a warning. Everything this command produces is one measurement of pixels, so
    /// a screenshot of whatever is in front is not a degraded answer, it is a wrong one - and because
    /// the failure was only logged, a MinimizeAll that had never worked in a compiled build went
    /// unnoticed for a whole lane (live-pass report, defect 1 and concern 3).</summary>
    public const string MinimizeFailed = "could not minimise windows; calibration would measure whatever is in front";

    /// <summary>Pure: the bounding box of pixels inside <paramref name="probe"/> that differ from the
    /// reference by more than <paramref name="threshold"/> in any of R, G, B. Null when nothing differs.</summary>
    public static Rect? DiffBounds(Surface shot, Surface reference, Rect probe, int threshold = 60)
        => DiffBounds(shot, reference, probe, threshold, out _);

    internal static Rect? DiffBounds(Surface shot, Surface reference, Rect probe, int threshold, out int pixels)
    {
        ArgumentNullException.ThrowIfNull(shot);
        ArgumentNullException.ThrowIfNull(reference);
        pixels = 0;
        var x0 = Math.Max(0, probe.X);
        var y0 = Math.Max(0, probe.Y);
        var x1 = Math.Min(Math.Min(shot.Width, reference.Width), probe.Right);
        var y1 = Math.Min(Math.Min(shot.Height, reference.Height), probe.Bottom);
        if (x1 <= x0 || y1 <= y0) return null;

        var region = new Rect(x0, y0, x1 - x0, y1 - y0);
        var rowBytes = region.W * 4;
        var a = new byte[rowBytes * region.H];
        var b = new byte[rowBytes * region.H];
        shot.ReadRegion(region, a);
        reference.ReadRegion(region, b);

        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (var y = 0; y < region.H; y++)
        {
            var row = y * rowBytes;
            for (var x = 0; x < region.W; x++)
            {
                var i = row + x * 4;
                if (Math.Abs(a[i] - b[i]) <= threshold &&
                    Math.Abs(a[i + 1] - b[i + 1]) <= threshold &&
                    Math.Abs(a[i + 2] - b[i + 2]) <= threshold) continue;
                pixels++;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }
        if (maxX < 0) return null;
        return new Rect(region.X + minX, region.Y + minY, maxX - minX + 1, maxY - minY + 1);
    }

    /// <summary>Live measurement. Leaves the desktop as it found it: the original wallpaper is put
    /// back, the probe shortcut is deleted and the folder flags are restored.</summary>
    /// <param name="writeProbeShortcut">Writes a .lnk at the given path whose icon is fully
    /// transparent, so the arrow overlay is the only thing the item paints. Task 6 passes
    /// ShortcutFiles/BlankIcon; this lane cannot reference them yet.</param>
    /// <param name="minimizeAll">Exposes the desktop. Defaults to <see cref="ShellDesktop.MinimizeAll"/>;
    /// a test injects one so the pre-flight can fail without a shell.</param>
    public static CalibrationResult Run(MonitorInfo monitor, string currentWallpaperPath,
        Action<string> writeProbeShortcut, Action<string>? log = null, Func<bool>? minimizeAll = null)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(writeProbeShortcut);
        var say = log ?? (_ => { });
        var minimize = minimizeAll ?? ShellDesktop.MinimizeAll;

        // Before anything on this machine is read or written: no folder flags, no probe image, no
        // wallpaper, no .lnk. See MinimizeFailed for why this is fatal rather than a warning; the
        // `calibrate` command surfaces the exception, so saying so loudly costs nothing.
        if (!TryMinimize(minimize, out var minimizeError))
            throw new InvalidOperationException(MinimizeFailed, minimizeError);

        // Nullable, and captured inside the try, so the finally puts back only what was actually
        // changed: the pre-flight above has already minimised every window, and a throw anywhere in
        // the prologue must still reach UndoMinimizeAll.
        DesktopFolderFlags? flagsBefore = null;
        string? originalWallpaper = null;
        var lnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "DeskWallTest-calibrate.lnk");

        try
        {
            DesktopFlags.EnsurePlacementAllowed();
            var iconSize = DesktopView.IconSize();
            var scale = monitor.Signature.ScalePercent;
            say($"display {monitor.Signature.Key}, icon size {iconSize} px, spacing {DesktopView.Spacing()}, flags {DesktopView.Flags()}");

            int w = monitor.Bounds.W, h = monitor.Bounds.H;
            var probe = new Rect(w / 2 - ProbeW / 2, h / 2 - ProbeH / 2, ProbeW, ProbeH);
            int itemX = probe.X + ItemInset, itemY = probe.Y + ItemInset;

            // The probe must land on screen where we painted it, so build the image at exactly the
            // monitor's size: DWPOS_FILL then maps it one to one.
            var calibrateJpg = Paths.InRuntime("calibrate.jpg");
            using var reference = Surface.Create(w, h);
            if (File.Exists(currentWallpaperPath))
            {
                using var current = Surface.Load(currentWallpaperPath);
                reference.DrawSurface(current, new Rect(0, 0, w, h), Fit.Cover);
            }
            else
            {
                reference.Clear(new Color(255, 16, 16, 16));
            }
            reference.FillRect(probe, new Color(255, 128, 128, 128));
            reference.SaveJpeg(calibrateJpg, 92);

            flagsBefore = DesktopView.Flags();
            originalWallpaper = WallpaperSetter.Get(monitor.WallpaperMonitorId);
            WallpaperSetter.Set(monitor.WallpaperMonitorId, calibrateJpg);
            // Hide every icon's label for the measurement: a label would land in the diff box and
            // swamp the 13 px arrow. Restored in the finally below.
            DesktopView.SetFlags(DesktopFolderFlags.HideFileNames, DesktopFolderFlags.HideFileNames);

            writeProbeShortcut(lnk);
            if (!File.Exists(lnk)) throw new InvalidOperationException($"the probe shortcut was not written to {lnk}");
            Thread.Sleep(1500);                       // Explorer notices the new file asynchronously
            DesktopView.Position([(lnk, itemX, itemY)]);
            Thread.Sleep(700);
            say($"probe at ({itemX},{itemY}); placed at {DesktopView.GetPosition(lnk)}");

            // Minimised at the top; a couple of seconds of Explorer settling have passed since, so ask
            // once more. Best effort this time - the pre-flight already proved the call reaches the
            // shell, and the windows it minimised have had nothing to bring them back.
            if (!TryMinimize(minimize, out _))
                say("note: the second minimise attempt failed; the pre-flight one is what exposed the desktop");
            Thread.Sleep(800);
            using var shot = Screenshot.Capture(monitor.Bounds);
            shot.SavePng(Paths.InRuntime("calibrate-shot.png"));

            // 2 px inset to dodge JPEG ringing at the probe's edges, as poc/verify.ps1 does.
            var inset = new Rect(probe.X + 2, probe.Y + 2, probe.W - 4, probe.H - 4);
            var bounds = DiffBounds(shot, reference, inset, 60, out var pixels)
                ?? throw new InvalidOperationException(
                    $"nothing differs inside the probe {inset}: the shortcut did not appear (is the icon really transparent?)");

            var arrow = new ArrowRect(bounds.X - itemX, bounds.Y - itemY, Math.Max(bounds.W, bounds.H));
            say($"diff bbox {bounds.W}x{bounds.H} at ({bounds.X},{bounds.Y}), {pixels} px -> arrow {arrow}");
            if (arrow.Size is < 8 or > 32)
                throw new InvalidOperationException(
                    $"arrow size {arrow.Size} is not plausible: bbox {bounds.W}x{bounds.H} at ({bounds.X},{bounds.Y}), " +
                    $"item ({itemX},{itemY}), {pixels} differing px");

            var calibration = Calibration.Load();
            calibration.Set(iconSize, scale, arrow);
            calibration.Save();
            return new CalibrationResult(iconSize, scale, arrow, itemX, itemY, pixels);
        }
        finally
        {
            if (flagsBefore is { } before)
                DesktopView.SetFlags(DesktopFolderFlags.HideFileNames, before & DesktopFolderFlags.HideFileNames);
            if (originalWallpaper is not null) WallpaperSetter.Set(monitor.WallpaperMonitorId, originalWallpaper);
            if (File.Exists(lnk)) File.Delete(lnk);
            ShellDesktop.UndoMinimizeAll();
        }
    }

    /// <summary>True when the shell really was asked to minimise. A delegate that throws is a failure
    /// like any other, and the exception is handed back rather than swallowed so it can travel as the
    /// inner exception of the one the caller raises.</summary>
    private static bool TryMinimize(Func<bool> minimize, out Exception? error)
    {
        error = null;
        try { return minimize(); }
        catch (Exception ex) { error = ex; return false; }
    }
}
