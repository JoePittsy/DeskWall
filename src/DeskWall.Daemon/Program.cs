using DeskWall.Core;
using DeskWall.Core.Display;
using DeskWall.Core.Layout;
using DeskWall.Core.Render;
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
                case "paths":
                    Console.WriteLine(Paths.RuntimeDir);
                    return 0;
                case "calibrate-test":
                    return CalibrateTest();
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

    /// <summary>TEMPORARY (lane p3-view). Task 6 replaces this with the real `calibrate` command, which
    /// passes ShortcutFiles.Write + BlankIcon.Ensure as the probe writer. Until those types exist this
    /// case supplies its own transparent icon and its own .lnk writer so the measurement can be run.</summary>
    private static int CalibrateTest()
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
            var icon = TransparentIcon(Paths.InRuntime("blank-calibrate.ico"));
            var result = Calibrator.Run(monitor, wallpaper, lnk => WriteShortcut(lnk, icon), Say);
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

    /// <summary>A 256 px fully transparent PNG wrapped in an ICO, exactly as poc/shortcuts.ps1 builds it.</summary>
    private static string TransparentIcon(string path)
    {
        var png = path + ".png";
        using (var s = Surface.Create(256, 256))
        {
            s.Clear(Color.Transparent);
            s.SavePng(png);
        }
        var bytes = File.ReadAllBytes(png);
        File.Delete(png);
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)1);          // ICONDIR: reserved, type=icon, count
        w.Write((byte)0); w.Write((byte)0);                                   // 256x256 is encoded as 0x0
        w.Write((byte)0); w.Write((byte)0);                                   // colours, reserved
        w.Write((ushort)1); w.Write((ushort)32);                              // planes, bit count
        w.Write((uint)bytes.Length); w.Write((uint)22);                       // size, offset
        w.Write(bytes);
        return path;
    }

    private static void WriteShortcut(string lnk, string iconPath)
    {
        var script = $"$s = (New-Object -ComObject WScript.Shell).CreateShortcut('{lnk}'); " +
                     $"$s.TargetPath = '{Environment.GetFolderPath(Environment.SpecialFolder.Windows)}\\explorer.exe'; " +
                     $"$s.IconLocation = '{iconPath},0'; $s.Description = 'DeskWall calibration probe'; $s.Save()";
        var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe") { UseShellExecute = false };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(script);
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.WaitForExit();
    }

    private static string? OptValue(List<string> opts, string name)
    {
        var i = opts.IndexOf(name);
        return i >= 0 && i + 1 < opts.Count ? opts[i + 1] : null;
    }
}
