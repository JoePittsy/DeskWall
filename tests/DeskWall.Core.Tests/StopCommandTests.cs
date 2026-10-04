using System.Diagnostics;
using Xunit;

namespace DeskWall.Core.Tests;

/// <summary>`deskwall --home &lt;scratch&gt; stop` against a real daemon. The scratch home has no layout
/// registered, so the daemon never paints the wallpaper, and --no-tray --no-shortcuts keeps it off
/// the tray and the desktop. It runs the JIT deskwall.exe the project reference copies next to the
/// test binaries, so it needs no publish.
/// <para>
/// Category=Desktop all the same: it is a real resident `deskwall run` in the interactive session, and
/// a second daemon opens another instance of the shared DeskWall.Events pipe, so for its lifetime a
/// producer's event can land on it instead of the owner's live daemon (CLAUDE.md, "Two daemons can
/// share one pipe name").
/// </para></summary>
[Trait("Category", "Desktop")]
public class StopCommandTests
{
    private static readonly string Exe = Path.Combine(AppContext.BaseDirectory, "deskwall.exe");

    private static (int Exit, string Out) Deskwall(string home, params string[] args)
    {
        var psi = new ProcessStartInfo(Exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("--home");
        psi.ArgumentList.Add(home);
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        Assert.True(p.WaitForExit(30_000), "deskwall did not exit");
        return (p.ExitCode, output);
    }

    [Fact]
    public void Stop_closes_the_daemon_for_its_home_and_then_reports_nothing_running()
    {
        var home = Path.Combine(Path.GetTempPath(), "deskwall-tests", "stop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        var psi = new ProcessStartInfo(Exe) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "--home", home, "run", "--no-tray", "--no-shortcuts" }) psi.ArgumentList.Add(a);
        using var daemon = Process.Start(psi)!;
        try
        {
            // "daemon start" is logged after the host window exists, which is what stop looks for.
            var log = Path.Combine(home, "deskwall.log");
            var sw = Stopwatch.StartNew();
            while (!(File.Exists(log) && ReadShared(log).Contains("daemon start")))
            {
                Assert.False(daemon.HasExited, $"daemon exited early with {(daemon.HasExited ? daemon.ExitCode : 0)}");
                Assert.True(sw.ElapsedMilliseconds < 30_000, "daemon never logged its start");
                Thread.Sleep(50);
            }

            var (exit, output) = Deskwall(home, "stop");
            Assert.Equal(0, exit);
            Assert.Contains($"stopped pid {daemon.Id}", output);
            Assert.True(daemon.HasExited, "stop returned before the process exited");

            (exit, output) = Deskwall(home, "stop");
            Assert.Equal(0, exit);
            Assert.Contains("not running", output);
        }
        finally
        {
            if (!daemon.HasExited) daemon.Kill();
            try { Directory.Delete(home, recursive: true); } catch (IOException) { }
        }
    }

    private static string ReadShared(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return new StreamReader(fs).ReadToEnd();
        }
        catch (IOException) { return ""; }
    }
}
