using System.Globalization;
using DeskWall.Core;
using DeskWall.Core.Diagnostics;
using DeskWall.Core.Display;
using DeskWall.Daemon.Host;
using DeskWall.Core.Layout;
using DeskWall.Core.Render;
using DeskWall.Core.Resolve;
using DeskWall.Core.Scheduling;
using DeskWall.Core.Shortcuts;
using DeskWall.Core.Sources;
using DeskWall.Core.Tick;
using DeskWall.Core.Verify;
using DeskWall.Core.Wallpaper;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace DeskWall.Daemon;

internal static class Program
{
    /// <summary>The hidden host window's class name, which is also how a second process finds a
    /// running daemon: there is no other handle on it (no taskbar entry, no main window).</summary>
    private const string HostClass = "DeskWallHost";

    private static int Main(string[] argv)
    {
        var args = argv.ToList();
        // --home is consumed before anything reads Paths.RuntimeDir, which caches its answer for the
        // life of the process: the budget test and any scratch run depend on winning that race.
        var home = TakeOption(args, "--home");
        if (home is not null) Environment.SetEnvironmentVariable("DESKWALL_HOME", Path.GetFullPath(home));

        var cmd = args.Count == 0 ? "run" : args[0];
        var opts = args.Skip(1).ToList();
        PInvoke.AttachConsole(PInvoke.ATTACH_PARENT_PROCESS);   // WinExe: borrow the caller's console when there is one
        try
        {
            switch (cmd)
            {
                case "run":
                    return Run(opts);
                case "tick":
                    return Tick(opts).GetAwaiter().GetResult();
                case "install":
                    return Install();
                case "uninstall":
                    return Uninstall();
                case "layouts":
                    return Layouts(opts);
                case "paths":
                    Console.WriteLine(Paths.RuntimeDir);
                    return 0;
                case "calibrate":
                    return Calibrate();
                case "verify":
                    return Verify(opts);
                case "shortcuts":
                    return Shortcuts(opts).GetAwaiter().GetResult();
                case "help":
                case "--help":
                case "-h":
                    Usage(Console.Out);
                    return 0;
                default:
                    Console.Error.WriteLine($"deskwall: unknown command '{cmd}'");
                    Usage(Console.Error);
                    return 2;
            }
        }
        catch (Exception ex)
        {
            // A Run-key-launched daemon has no console to print to; the log is the only witness.
            RollingLog.Default().Error($"deskwall {cmd} failed", ex);
            Console.Error.WriteLine($"deskwall {cmd}: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static void Usage(TextWriter w)
    {
        w.WriteLine("deskwall [--home <dir>] <command>");
        w.WriteLine("  run [--no-tray] [--no-shortcuts]   resident daemon (the default with no command)");
        w.WriteLine("                             --no-tray wins; otherwise settings.json trayIcon decides");
        w.WriteLine("  tick [--layout <path>] [--force] [--measure] [--no-apply] [--no-shortcuts]");
        w.WriteLine("  install                    start at sign-in, and start now");
        w.WriteLine("  uninstall                  stop, remove the Run entry, restore the wallpaper");
        w.WriteLine("  layouts list               registered layouts, and what this display resolves to");
        w.WriteLine("  layouts set <path>         register a layout for this display");
        w.WriteLine("  paths                      the runtime directory");
        w.WriteLine("  calibrate                  measure the shell's shortcut-arrow overlay");
        w.WriteLine("  shortcuts [--layout <path>]  planned vs actual icon positions (read-only)");
        w.WriteLine("  verify [--pad N] [--threshold N] [--json]");
        w.WriteLine("                             screenshot the desktop and measure the arrow padding per slot");
    }

    /// <summary>deskwall run [--no-tray] [--no-shortcuts]. One daemon per session: a second one hands the
    /// running daemon a Manual wake (so `run` doubles as "refresh now" from a script) and exits happy.
    /// --no-shortcuts leaves the desktop alone: the wallpaper still updates, no .lnk is written or moved.
    /// <para>--no-tray is the override, so it is decided here; with it absent the daemon asks
    /// DaemonSettings (the designer's settings.json) at start.</para></summary>
    private static int Run(List<string> opts)
    {
        using var single = new Mutex(initiallyOwned: true, @"Local\DeskWall.Daemon", out var mine);
        if (!mine)
        {
            // The winner may still be between `new Mutex` and CreateWindowEx, so give the window a
            // second to appear rather than claim a refresh nobody was asked for (finding 10).
            var hwnd = HWND.Null;
            for (var i = 0; i < 20 && (hwnd = PInvoke.FindWindow(HostClass, null)).IsNull; i++) Thread.Sleep(50);
            if (hwnd.IsNull)
            {
                Console.Error.WriteLine("deskwall: another instance holds the lock but has no window yet; nothing refreshed");
                return 1;
            }
            PInvoke.PostMessage(hwnd, HostWindow.WM_APP_WAKE, (nuint)(int)WakeKind.Manual, 0);
            Console.WriteLine("deskwall: already running; asked it to refresh");
            return 0;
        }
        var log = RollingLog.Default();
        // Error, not Warn: a layout that cannot be read means no new frame, and RollingLog.LastError
        // is what puts ". ERROR see log" in the tray tooltip (finding 4).
        var store = LayoutStore.Default(m => log.Error(m));
        return new DaemonLoop(log, store, SystemClock.Instance, tray: !opts.Contains("--no-tray"))
        { Shortcuts = !opts.Contains("--no-shortcuts") }.Run();
    }

    /// <summary>deskwall install: HKCU Run entry, a restore point for the wallpaper we are about to
    /// replace, and the daemon started now so the user does not have to sign out to see it work.</summary>
    private static int Install()
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("cannot determine this executable's path");
        Startup.Install(exe);
        WallpaperSetter.RecordRestorePoint();
        Console.WriteLine($"installed: {Startup.Installed()}");
        if (!PInvoke.FindWindow(HostClass, null).IsNull)
        {
            Console.WriteLine("already running");
            return 0;
        }
        // UseShellExecute, deliberately: with it false the daemon inherits this process's std handles
        // and holds them open for its whole life, so `deskwall install` from any redirected caller
        // (a pipe, a test harness, a script capturing output) hangs on a pipe that never closes.
        var psi = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true };
        psi.ArgumentList.Add("run");
        using var p = System.Diagnostics.Process.Start(psi);
        Console.WriteLine($"started pid {p?.Id}");
        return 0;
    }

    /// <summary>deskwall uninstall: stop the daemon, drop the Run entry, put the old wallpaper back.
    /// Also removes the slot shortcuts and restores the desktop folder flags (Phase 3).</summary>
    private static int Uninstall()
    {
        var hwnd = PInvoke.FindWindow(HostClass, null);
        if (!hwnd.IsNull)
        {
            PInvoke.PostMessage(hwnd, PInvoke.WM_CLOSE, 0, 0);
            // Restoring the wallpaper while the daemon is still awake would just get overwritten by
            // the tick it is in the middle of, so give it a moment to actually go.
            for (var i = 0; i < 50 && !PInvoke.FindWindow(HostClass, null).IsNull; i++) Thread.Sleep(100);
            Console.WriteLine(PInvoke.FindWindow(HostClass, null).IsNull ? "stopped the running daemon" : "the running daemon did not stop; restoring anyway");
        }
        Startup.Uninstall();
        try
        {
            // Phase 3: drop the slot shortcuts recorded in shortcuts-owned.json and put the desktop
            // folder flags back. Scoped to what we wrote: the v0 PowerShell POC owns slots 0..3 with
            // the same file names and must survive `deskwall uninstall`.
            var removed = new ShortcutManager(Calibration.Load()).RemoveOwned();
            DesktopFlags.Restore();
            Console.WriteLine($"removed {removed} desktop shortcut(s); desktop flags restored");
        }
        catch (Exception ex) { Console.Error.WriteLine($"shortcut cleanup failed: {ex.Message}"); }
        WallpaperSetter.Restore();
        Console.WriteLine("uninstalled; previous wallpaper restored");
        return 0;
    }

    /// <summary>deskwall layouts list | layouts set &lt;path&gt;.</summary>
    private static int Layouts(List<string> opts)
    {
        var store = LayoutStore.Default(Console.Error.WriteLine);
        var sub = opts.Count == 0 ? "list" : opts[0];
        var monitor = Monitors.Enumerate().FirstOrDefault(m => m.IsPrimary);
        switch (sub)
        {
            case "set":
                if (opts.Count < 2) { Console.Error.WriteLine("usage: deskwall layouts set <path>"); return 2; }
                if (monitor is null) { Console.Error.WriteLine("no primary monitor"); return 3; }
                var path = Path.GetFullPath(opts[1]);
                if (!File.Exists(path)) { Console.Error.WriteLine($"no layout at {path}"); return 3; }
                LayoutFile.Load(path);   // fail here, with the parse error, rather than silently at the next tick
                store.Set(monitor.Signature, path);
                Console.WriteLine($"{monitor.Signature.Key} -> {path}");
                return 0;
            case "list":
                foreach (var (key, file) in store.Entries) Console.WriteLine($"{key} -> {file}");
                if (store.Entries.Count == 0) Console.WriteLine("(no layouts registered)");
                if (monitor is not null)
                {
                    var res = store.Resolve(monitor.Signature);
                    Console.WriteLine($"this display: {monitor.Signature.Key}");
                    Console.WriteLine(res is null ? "  resolves to: nothing"
                        : $"  resolves to: {res.SourcePath}{(res.Scaled ? $" (scaled from {res.SourceSignature.Key})" : "")}");
                }
                return 0;
            default:
                Console.Error.WriteLine($"deskwall layouts: unknown subcommand '{sub}'");
                return 2;
        }
    }

    /// <summary>deskwall tick [--layout path] [--force] [--measure] [--no-apply] [--no-shortcuts]. Without --layout it
    /// resolves the layout exactly as the daemon does, through the store, so a scripted one-shot tick
    /// and the resident one draw the same thing. --layout still bypasses the store, for scripting.</summary>
    private static async Task<int> Tick(List<string> opts)
    {
        var monitor = Monitors.Enumerate().FirstOrDefault(m => m.IsPrimary);
        if (monitor is null) { Console.Error.WriteLine("no primary monitor"); return 3; }
        LayoutFile layout;
        var layoutPath = OptValue(opts, "--layout");
        if (layoutPath is not null)
        {
            if (!File.Exists(layoutPath)) { Console.Error.WriteLine($"no layout at {layoutPath}"); return 3; }
            layout = LayoutFile.Load(layoutPath);
        }
        else
        {
            var res = LayoutStore.Default(Console.Error.WriteLine).Resolve(monitor.Signature);
            if (res is null)
            {
                Console.Error.WriteLine($"no layout for {monitor.Signature.Key}; use: deskwall layouts set <path>");
                return 3;
            }
            if (res.Scaled) Console.WriteLine($"scaled layout {res.SourcePath} from {res.SourceSignature.Key}");
            layout = res.Layout;
        }
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
            catch (Exception ex) { registry.Set(snap.Failed(ex.Message, clock.Now)); }
        }

        var resolved = LayoutResolver.Resolve(layout, registry.Tree());
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

    /// <summary>deskwall verify [--pad N] [--threshold N] [--json] - screenshot the live desktop, diff
    /// it against the frame the last tick composed and measure the shortcut-arrow padding for every
    /// slot. Exit 0 when every slot is at the wanted pad, 4 when any is not. Read-only: it minimises the
    /// windows for about a second and puts them back, and writes only into the runtime dir.</summary>
    private static int Verify(List<string> opts)
    {
        var json = opts.Contains("--json");
        var pad = IntOption(opts, "--pad") ?? ShortcutPlan.DefaultPad;
        var threshold = IntOption(opts, "--threshold") ?? 60;
        // MinimizeAll takes the console with it and a WinExe's redirected stdout does not survive
        // AttachConsole, so the progress lines are teed to a file exactly as `calibrate` does.
        var logFile = Paths.InRuntime("verify-log.txt");
        var lines = new List<string>();
        try
        {
            var report = Verifier.Run(pad, threshold, lines.Add);
            var text = json ? report.ToJson() : report.ToText();
            lines.Add(text);
            Console.WriteLine(text);
            return report.Ok ? 0 : 4;
        }
        catch (Exception ex)
        {
            lines.Add($"FAILED: {ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine($"verify log: {logFile}");
            throw;
        }
        finally { File.WriteAllLines(logFile, lines); }
    }

    private static int? IntOption(List<string> opts, string name)
    {
        if (OptValue(opts, name) is not { } v) return null;
        if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            throw new ArgumentException($"{name} needs a whole number, not '{v}'");
        return n;
    }

    private static string? OptValue(List<string> opts, string name)
    {
        var i = opts.IndexOf(name);
        return i >= 0 && i + 1 < opts.Count ? opts[i + 1] : null;
    }

    /// <summary>Remove "--name value" from anywhere in the argument list and return the value.</summary>
    private static string? TakeOption(List<string> args, string name)
    {
        var i = args.IndexOf(name);
        if (i < 0 || i + 1 >= args.Count) return null;
        var value = args[i + 1];
        args.RemoveRange(i, 2);
        return value;
    }
}
