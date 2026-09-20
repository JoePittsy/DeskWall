using DeskWall.Core;
using DeskWall.Core.Diagnostics;
using DeskWall.Core.Display;
using DeskWall.Daemon.Host;
using DeskWall.Core.Layout;
using DeskWall.Core.Render;
using DeskWall.Core.Resolve;
using DeskWall.Core.Shortcuts;
using DeskWall.Core.Sources;
using DeskWall.Core.Tick;
using DeskWall.Core.Wallpaper;
using Windows.Win32;

namespace DeskWall.Daemon;

internal static class Program
{
    private static int Main(string[] argv)
    {
        var cmd = argv.Length == 0 ? "run" : argv[0];
        var opts = argv.Skip(1).ToList();
        PInvoke.AttachConsole(PInvoke.ATTACH_PARENT_PROCESS);   // WinExe: borrow the caller's console when there is one
        try
        {
            switch (cmd)
            {
                case "tick":
                    return Tick(opts).GetAwaiter().GetResult();
                case "host-test":
                    return HostTest(opts);
                case "paths":
                    Console.WriteLine(Paths.RuntimeDir);
                    return 0;
                case "calibrate":
                    return Calibrate();
                case "shortcuts":
                    return Shortcuts(opts).GetAwaiter().GetResult();
                default:
                    Console.Error.WriteLine($"deskwall: unknown or not yet implemented command '{cmd}'");
                    return 2;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"deskwall {cmd}: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    /// <summary>Temporary manual harness for the host window, waitable timer and tray icon.
    /// Task 8 replaces it with the real run loop.</summary>
    private static int HostTest(List<string> opts)
    {
        var rounds = opts.Count > 0 && int.TryParse(opts[0], out var n) ? n : 6;
        using var win = new HostWindow();
        using var tray = new TrayIcon(win);
        using var timer = new WaitableTimer();
        tray.Command += c =>
        {
            Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} tray: {c}");
            if (c == TrayCommand.TogglePause) tray.Paused = !tray.Paused;
        };
        Console.WriteLine($"host-test: tray added={tray.Added}. Change resolution, lock/unlock, click the tray icon, or wait 5 s.");
        for (var i = 0; i < rounds; i++)
        {
            tray.SetTooltip($"DeskWall test {i}");
            timer.SetDue(DateTimeOffset.UtcNow.AddSeconds(5));
            foreach (var r in win.WaitAndPump(timer)) Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} wake: {r}");
        }
        Console.WriteLine($"footprint: {Footprint.Current().Short()}");
        return 0;
    }

    /// <summary>deskwall tick [--layout path] [--force] [--measure] [--no-apply] [--no-shortcuts]</summary>
    private static async Task<int> Tick(List<string> opts)
    {
        var layoutPath = OptValue(opts, "--layout") ?? Paths.InRuntime("layout.json");
        if (!File.Exists(layoutPath)) { Console.Error.WriteLine($"no layout at {layoutPath}"); return 3; }
        var layout = LayoutFile.Load(layoutPath);
        var monitor = Monitors.Enumerate().First(m => m.IsPrimary);
        var clock = SystemClock.Instance;
        var sources = layout.Sources.Select(s => SourceFactory.Create(s, clock)).ToList();
        var registry = new SourceRegistry();
        WallpaperSetter.RecordRestorePoint();
        var manager = opts.Contains("--no-shortcuts") ? null : new ShortcutManager(Calibration.Load());
        var runner = new TickRunner(layout, sources, registry, clock, monitor, shortcuts: manager);
        var t = await runner.RunAsync(force: opts.Contains("--force"), apply: !opts.Contains("--no-apply"), CancellationToken.None);
        if (opts.Contains("--measure")) Console.WriteLine(t.ToTable());
        else Console.WriteLine($"{DateTime.Now:HH:mm:ss} total={t.TotalMs} ms cpu={t.CpuMs:N0} ms redrawn={t.Redrawn}{(t.Skipped ? " skipped" : "")}");
        if (runner.LastShortcutOutcome is { } o)
        {
            Console.WriteLine($"shortcuts: written={o.Written} positioned={o.Positioned} removed={o.Removed}");
            foreach (var w in o.Warnings) Console.Error.WriteLine($"shortcuts: {w}");
        }
        return 0;
    }

    /// <summary>deskwall shortcuts [--layout path] - read-only: slot, target, planned position and what
    /// the desktop actually reports. Never writes or moves anything; `tick` is what places icons.</summary>
    private static async Task<int> Shortcuts(List<string> opts)
    {
        var layoutPath = OptValue(opts, "--layout") ?? Paths.InRuntime("layout.json");
        if (!File.Exists(layoutPath)) { Console.Error.WriteLine($"no layout at {layoutPath}"); return 3; }
        var layout = LayoutFile.Load(layoutPath);
        var monitor = Monitors.Enumerate().First(m => m.IsPrimary);
        var clock = SystemClock.Instance;
        var registry = new SourceRegistry();
        foreach (var s in layout.Sources.Select(s => SourceFactory.Create(s, clock)))
        {
            var snap = registry.Get(s.Name);
            try { registry.Set(snap.Succeeded(await s.RefreshAsync(CancellationToken.None), clock.Now)); }
            catch (Exception ex) { registry.Set(snap.Failed(ex.Message)); }
        }

        var resolved = LayoutResolver.Resolve(layout, registry.Tree(), new Rect(0, 0, monitor.Bounds.W, monitor.Bounds.H));
        var shortcuts = ShortcutPlan.Ordered(resolved.OfType<ResolvedShortcut>().ToList());
        if (shortcuts.Count == 0) { Console.WriteLine("no shortcut components in the layout"); return 0; }

        var calibration = Calibration.Load();
        var iconSize = DesktopView.IconSize();
        var scale = monitor.Signature.ScalePercent;
        var arrow = calibration.Get(iconSize, scale);
        if (arrow is null)
        {
            arrow = calibration.Get(48, 100)!;
            Console.WriteLine($"no calibration for {Calibration.Key(iconSize, scale)}; using {Calibration.Key(48, 100)}");
        }
        Console.WriteLine($"{monitor.Signature.Key}, icon size {iconSize} px, arrow ({arrow.Dx},{arrow.Dy},{arrow.Size})");

        var desktop = ShortcutFiles.DesktopDir();
        var offBy = 0;
        foreach (var s in shortcuts)
        {
            var path = Path.Combine(desktop, ShortcutPlan.SlotFileName(s.Slot));
            var (x, y) = ShortcutPlan.IconPosition(s.Rect, arrow);
            var got = DesktopView.GetPosition(path);
            string verdict;
            if (!File.Exists(path)) { verdict = "MISSING"; offBy++; }
            else if (got is null) { verdict = "NO POSITION"; offBy++; }
            else if (got.Value.X == x && got.Value.Y == y) verdict = "VERIFY OK";
            else { verdict = $"OFF BY ({got.Value.X - x},{got.Value.Y - y})"; offBy++; }
            Console.WriteLine($"slot {s.Slot,2}  {s.Target,-40}  planned ({x},{y})  actual " +
                              $"{(got is null ? "-" : $"({got.Value.X},{got.Value.Y})")}  {verdict}");
        }
        return offBy == 0 ? 0 : 4;
    }

    /// <summary>deskwall calibrate - measure where the shell draws the shortcut-arrow overlay for the
    /// current icon size and scale, and store it in calibration.json. Manual command: it borrows the
    /// wallpaper and the desktop for about five seconds and puts both back.</summary>
    private static int Calibrate()
    {
        // MinimizeAll takes the console with it, and a WinExe's redirected stdout does not survive
        // AttachConsole, so the log is teed to a file as well.
        var logFile = Paths.InRuntime("calibrate-log.txt");
        var lines = new List<string>();
        void Say(string s) { lines.Add(s); Console.WriteLine(s); }
        try
        {
            var monitor = Monitors.Enumerate().First(m => m.IsPrimary);
            var wallpaper = WallpaperSetter.Get(monitor.WallpaperMonitorId) ?? "";
            Say($"current wallpaper: {wallpaper}");
            var icon = BlankIcon.Ensure();
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var probe = new ShortcutSpec(Path.Combine(windows, "explorer.exe"), "", windows, "DeskWall calibration probe", icon);
            var result = Calibrator.Run(monitor, wallpaper, lnk => ShortcutFiles.Write(lnk, probe), Say);
            Say($"RESULT icon={result.IconSize} scale={result.ScalePercent} " +
                $"arrow=({result.Arrow.Dx},{result.Arrow.Dy},{result.Arrow.Size}) " +
                $"item=({result.ItemX},{result.ItemY}) pixels={result.Pixels}");
            Say($"calibration.json: {File.ReadAllText(Paths.InRuntime("calibration.json"))}");
            return 0;
        }
        catch (Exception ex)
        {
            Say($"FAILED: {ex.GetType().Name}: {ex.Message}");
            throw;
        }
        finally { File.WriteAllLines(logFile, lines); }
    }

    private static string? OptValue(List<string> opts, string name)
    {
        var i = opts.IndexOf(name);
        return i >= 0 && i + 1 < opts.Count ? opts[i + 1] : null;
    }
}
