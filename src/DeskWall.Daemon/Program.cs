using DeskWall.Core;
using DeskWall.Core.Diagnostics;
using DeskWall.Core.Display;
using DeskWall.Daemon.Host;
using DeskWall.Core.Layout;
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
                    return HostTest();
                case "paths":
                    Console.WriteLine(Paths.RuntimeDir);
                    return 0;
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
    private static int HostTest()
    {
        using var win = new HostWindow();
        using var timer = new WaitableTimer();
        Console.WriteLine("host-test: change resolution, lock/unlock, or wait 5 s. Ctrl+C to stop.");
        for (var i = 0; i < 6; i++)
        {
            timer.SetDue(DateTimeOffset.UtcNow.AddSeconds(5));
            foreach (var r in win.WaitAndPump(timer)) Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} wake: {r}");
        }
        Console.WriteLine($"footprint: {Footprint.Current().Short()}");
        return 0;
    }

    /// <summary>deskwall tick [--layout path] [--force] [--measure] [--no-apply]</summary>
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
        var runner = new TickRunner(layout, sources, registry, clock, monitor);
        var t = await runner.RunAsync(force: opts.Contains("--force"), apply: !opts.Contains("--no-apply"), CancellationToken.None);
        if (opts.Contains("--measure")) Console.WriteLine(t.ToTable());
        else Console.WriteLine($"{DateTime.Now:HH:mm:ss} total={t.TotalMs} ms cpu={t.CpuMs:N0} ms redrawn={t.Redrawn}{(t.Skipped ? " skipped" : "")}");
        return 0;
    }

    private static string? OptValue(List<string> opts, string name)
    {
        var i = opts.IndexOf(name);
        return i >= 0 && i + 1 < opts.Count ? opts[i + 1] : null;
    }
}
