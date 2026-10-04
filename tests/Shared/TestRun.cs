using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

/// <summary>The one scratch folder a test run owns: <c>%TEMP%\deskwall-tests\&lt;project&gt;-&lt;pid&gt;-&lt;id&gt;</c>,
/// with the run's DESKWALL_HOME at <see cref="Home"/> inside it. Every test writes its temp files under
/// <see cref="Root"/>, never straight into %TEMP% or a folder another run could share, so concurrent runs
/// (several worktrees testing on one machine) cannot delete each other's files. Linked into both test
/// projects.</summary>
internal static class TestRun
{
    /// <summary>%TEMP%\deskwall-tests: holds one folder per run, nothing else once the sweep has run.</summary>
    public static readonly string Parent = Path.Combine(Path.GetTempPath(), "deskwall-tests");

    /// <summary>This run's folder, deleted when the test host exits.</summary>
    public static string Root { get; private set; } = "";

    /// <summary>This run's runtime dir (DESKWALL_HOME), inside <see cref="Root"/>.</summary>
    public static string Home { get; private set; } = "";

    /// <summary>Anything in <see cref="Parent"/> not named like a run folder predates per-run folders;
    /// it is removed once nothing has written to it for this long, which no run ever lasts.</summary>
    internal static readonly TimeSpan LegacyAge = TimeSpan.FromDays(1);

    private static readonly Regex RunName = new(@"^[a-z]+-(?<pid>\d+)-[0-9a-f]{8}$", RegexOptions.CultureInvariant);

    /// <summary>Point the runtime dir at this run's folder before any test touches Paths, so tests never
    /// write into the user's real %LOCALAPPDATA%\DeskWall or into another run's home.</summary>
    [ModuleInitializer]
    internal static void Init()
    {
        var project = typeof(TestRun).Assembly.GetName().Name!
            .Replace("DeskWall.", "").Replace(".Tests", "").ToLowerInvariant();
        Sweep(Parent, DateTime.UtcNow);
        Root = Path.Combine(Parent, $"{project}-{Environment.ProcessId}-{Guid.NewGuid().ToString("N")[..8]}");
        Home = Path.Combine(Root, "home");
        Directory.CreateDirectory(Home);
        Environment.SetEnvironmentVariable("DESKWALL_HOME", Home);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => TryDelete(Root);
    }

    /// <summary>Removes what earlier runs left behind: a run folder whose process is gone (it crashed,
    /// or a file was still locked when it exited), and anything from before per-run folders once it
    /// is <see cref="LegacyAge"/> old. A live run's folder is never touched.</summary>
    internal static void Sweep(string parent, DateTime nowUtc)
    {
        if (!Directory.Exists(parent)) return;
        foreach (var entry in new DirectoryInfo(parent).EnumerateFileSystemInfos())
        {
            var m = RunName.Match(entry.Name);
            var stale = m.Success && entry is DirectoryInfo
                ? !IsRunning(int.Parse(m.Groups["pid"].Value))
                : nowUtc - entry.LastWriteTimeUtc > LegacyAge;
            if (stale) TryDelete(entry.FullName);
        }
    }

    private static bool IsRunning(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { }                    // a file still open: the next run's sweep gets it
        catch (UnauthorizedAccessException) { }
    }
}
