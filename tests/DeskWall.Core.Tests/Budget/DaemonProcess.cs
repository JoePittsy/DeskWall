using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using DeskWall.Core;
using DeskWall.Core.Display;
using DeskWall.Core.Layout;
using DeskWall.Core.Wallpaper;

namespace DeskWall.Core.Tests.Budget;

/// <summary>Where the published native-AOT daemon lives and whether it is there. The budget tests
/// measure the shipped binary, not the JIT-compiled test host, so without a publish they measure
/// nothing and say so instead of passing quietly.</summary>
internal static class Published
{
    public const string RelativeExe =
        @"src\DeskWall.Daemon\bin\Release\net10.0-windows10.0.19041.0\win-x64\publish\deskwall.exe";

    /// <summary>The published exe, or null when it has not been built.</summary>
    internal static string? Exe { get; } = FindExe();

    /// <summary>Why the budget tests cannot run, or null when they can.</summary>
    internal static string? Skip { get; } = Exe is not null ? null
        : $"SKIPPED: no published daemon at <repo>\\{RelativeExe}. " +
          "Run: dotnet publish src/DeskWall.Daemon -c Release -r win-x64. " +
          "Budget numbers from a JIT test host would not mean anything, so nothing was measured.";

    internal static string? RepoRoot { get; } = FindRepoRoot();

    private static string? FindRepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "DeskWall.slnx"))) return d.FullName;
        return null;
    }

    private static string? FindExe()
    {
        var root = FindRepoRoot();
        if (root is null) return null;
        var exe = Path.Combine(root, RelativeExe);
        return File.Exists(exe) ? exe : null;
    }
}

/// <summary>A scratch DESKWALL_HOME with one layout registered for this display, plus the daemon or
/// one-shot ticks run against it.
/// <para>
/// The daemon it starts really does paint the real desktop - there is no "do not apply" for `run` -
/// so the wallpaper in force when the scratch was created is put back on <see cref="Dispose"/>.
/// Nothing else on the machine is touched: <c>--no-shortcuts</c> keeps it away from the desktop
/// icons, and the Run entry is never installed or removed.
/// </para></summary>
internal sealed class DaemonScratch : IDisposable
{
    private readonly string _exe;
    private readonly string? _wallpaperBefore;
    private readonly string _wallpaperMonitorId;
    private Process? _daemon;

    private DaemonScratch(string exe, string home, MonitorInfo monitor)
    {
        _exe = exe;
        Home = home;
        Monitor = monitor;
        _wallpaperMonitorId = monitor.WallpaperMonitorId;
        _wallpaperBefore = WallpaperSetter.Get(monitor.WallpaperMonitorId);
    }

    public string Home { get; }

    public MonitorInfo Monitor { get; }

    public string OutPath => Path.Combine(Home, "deskwall.jpg");

    public string LogPath => Path.Combine(Home, "deskwall.log");

    public string StatePath => Path.Combine(Home, "frame-state.json");

    /// <summary>A fresh scratch home with <paramref name="layoutFile"/> (a name under the repo's
    /// layouts/ directory) registered for the primary display's signature.</summary>
    public static DaemonScratch Create(string exe, string layoutFile)
    {
        var monitor = Monitors.Enumerate().FirstOrDefault(m => m.IsPrimary)
            ?? throw new InvalidOperationException("no primary monitor");
        var home = Path.Combine(Path.GetTempPath(), "deskwall-budget", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        var layout = Path.Combine(Published.RepoRoot!, "layouts", layoutFile);
        if (!File.Exists(layout)) throw new FileNotFoundException("layout not found", layout);
        new LayoutStore(Path.Combine(home, "layouts.json")).Set(monitor.Signature, layout);
        return new DaemonScratch(exe, home, monitor);
    }

    /// <summary>One `deskwall tick` against the scratch home, waited out. Returns stdout.</summary>
    public string Tick(params string[] extra)
    {
        var psi = Psi("tick");
        foreach (var a in extra) psi.ArgumentList.Add(a);
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(120_000);
        if (p.ExitCode != 0) throw new InvalidOperationException($"deskwall tick exited {p.ExitCode}: {stderr}{stdout}");
        return stdout;
    }

    /// <summary>Start the resident daemon. Throws when the run exits at once, which is what happens
    /// when another daemon on this session already holds <c>Local\DeskWall.Daemon</c>: measuring an
    /// instant exit as a cold start would report a wonderful number for nothing.</summary>
    public Process StartDaemon()
    {
        var psi = Psi("run");
        psi.ArgumentList.Add("--no-tray");
        psi.ArgumentList.Add("--no-shortcuts");
        var p = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
        _daemon = p;
        return p;
    }

    /// <summary>Wait for <paramref name="path"/> to exist, polling every 10 ms. Returns the wait in
    /// milliseconds, or null on timeout.</summary>
    public static double? WaitForFile(string path, Process daemon, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (File.Exists(path)) return sw.Elapsed.TotalMilliseconds;
            if (daemon.HasExited) return null;
            Thread.Sleep(10);
        }
        return null;
    }

    /// <summary>The sum of the <c>cpu N ms</c> figures the daemon logged for its own ticks, which is
    /// work the idle budget is not about: the budget asks what the process costs between wakes.</summary>
    public double LoggedTickCpuMs(int fromLine = 0)
    {
        if (!File.Exists(LogPath)) return 0;
        var total = 0d;
        var lines = ReadLogLines();
        for (var i = fromLine; i < lines.Count; i++)
        {
            var m = Regex.Match(lines[i], @"\btick\b.*\bcpu ([0-9,]+) ms");
            if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var ms))
                total += ms;
        }
        return total;
    }

    public List<string> ReadLogLines()
    {
        if (!File.Exists(LogPath)) return [];
        // The daemon holds the log open only for the length of an append, but a wake can land exactly
        // here; a read that loses the race is worth a retry, not a failed budget run.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var fs = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(fs);
                var lines = new List<string>();
                while (reader.ReadLine() is { } line) lines.Add(line);
                return lines;
            }
            catch (IOException) when (attempt < 10) { Thread.Sleep(50); }
        }
    }

    private ProcessStartInfo Psi(string command)
    {
        var psi = new ProcessStartInfo(_exe) { UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("--home");
        psi.ArgumentList.Add(Home);
        psi.ArgumentList.Add(command);
        if (command == "tick") psi.ArgumentList.Add("--no-shortcuts");
        return psi;
    }

    public void Dispose()
    {
        if (_daemon is { } p)
        {
            // Kill, not `uninstall`: uninstall would remove the user's real HKCU Run entry and their
            // desktop shortcuts. The wallpaper is put back below instead.
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            try { p.WaitForExit(10_000); } catch (SystemException) { }
            p.Dispose();
            _daemon = null;
        }
        if (_wallpaperBefore is { Length: > 0 } && File.Exists(_wallpaperBefore))
        {
            try { WallpaperSetter.Set(_wallpaperMonitorId, _wallpaperBefore); }
            catch (Exception) { /* the next POC tick repaints it anyway */ }
        }
        try { Directory.Delete(Home, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
