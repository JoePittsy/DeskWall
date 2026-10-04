using System.Diagnostics;
using System.IO.Pipes;
using Xunit;

namespace DeskWall.Core.Tests;

/// <summary>Issue #28: each runtime dir's daemon listens on its own event pipe, and a producer for a
/// non-default home can ask `deskwall pipe` which one that is. Same harness as StopCommandTests: a
/// scratch home with no layout registered never paints the wallpaper, and --no-tray --no-shortcuts
/// keeps the daemon off the tray and the desktop. Nothing here ever opens DeskWall.Events, which on
/// the owner's machine is the live daemon's.</summary>
[Trait("Category", "Pipe")]
public class EventPipeCommandTests
{
    private static readonly string Exe = Path.Combine(AppContext.BaseDirectory, "deskwall.exe");

    private static string ScratchHome(string tag)
    {
        var home = Path.Combine(TestRun.Root, tag + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        return home;
    }

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
    public void Pipe_prints_the_event_pipe_name_for_its_home()
    {
        var home = ScratchHome("pipe");
        try
        {
            var (exit, output) = Deskwall(home, "pipe");
            Assert.Equal(0, exit);
            Assert.Equal(RuntimeInstance.EventPipeName(home), output.Trim());
            Assert.StartsWith("DeskWall.Events.", output.Trim());
        }
        finally
        {
            try { Directory.Delete(home, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void A_scratch_daemon_receives_events_on_its_own_homes_pipe()
    {
        var home = ScratchHome("pipe-run");
        var psi = new ProcessStartInfo(Exe) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "--home", home, "run", "--no-tray", "--no-shortcuts" }) psi.ArgumentList.Add(a);
        using var daemon = Process.Start(psi)!;
        try
        {
            // Connect retries until the daemon has created the instance; before #28 it never did,
            // because it listened on DeskWall.Events instead.
            using (var client = new NamedPipeClientStream(".", RuntimeInstance.EventPipeName(home), PipeDirection.Out))
            {
                client.Connect(20_000);
                using var w = new StreamWriter(client) { AutoFlush = true };
                w.WriteLine("""{"source":"pipetest","data":{"status":"green"}}""");
            }

            // Wait for the line to be read before stopping: the bus has it once the provider is
            // known, and the daemon saves events.json unconditionally on shutdown.
            var log = Path.Combine(home, "deskwall.log");
            var sw = Stopwatch.StartNew();
            while (!(File.Exists(log) && ReadShared(log).Contains("daemon start")))
            {
                Assert.False(daemon.HasExited, "daemon exited early");
                Assert.True(sw.ElapsedMilliseconds < 30_000, "daemon never logged its start");
                Thread.Sleep(50);
            }
            Thread.Sleep(500);

            var (exit, _) = Deskwall(home, "stop");
            Assert.Equal(0, exit);
            var events = ReadShared(Path.Combine(home, "events.json"));
            Assert.Contains("pipetest", events);
            Assert.Contains("green", events);
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
