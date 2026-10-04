using System.Diagnostics;
using DeskWall.Daemon.Host;
using Xunit;

namespace DeskWall.Core.Tests.Events;

/// <summary>Issue #17's wake. A real FileSystemWatcher on a private temp folder, never the runtime
/// directory a live daemon may be watching.</summary>
public class EventsFileWatcherTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "deskwall-tests", "events-watch-" + Guid.NewGuid().ToString("N"));

    public EventsFileWatcherTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private static bool WaitFor(Func<bool> done, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (done()) return true;
            Thread.Sleep(20);
        }
        return done();
    }

    [Fact]
    public void A_Write_Temp_Then_Rename_Save_Fires_Once_After_The_Burst()
    {
        var path = Path.Combine(_dir, "events.json");
        File.WriteAllText(path, "{}");
        var fired = 0;
        using var watcher = new EventsFileWatcher(path, () => Interlocked.Increment(ref fired));

        // What EventStore.Save does, from either process.
        var tmp = path + ".1234.tmp";
        File.WriteAllText(tmp, """{"version":1,"providers":[]}""");
        File.Move(tmp, path, overwrite: true);

        Assert.True(WaitFor(() => Volatile.Read(ref fired) > 0));
        Thread.Sleep(500);                                   // past the 300 ms debounce
        Assert.Equal(1, Volatile.Read(ref fired));
    }

    [Fact]
    public void Its_Neighbours_In_The_Runtime_Directory_Do_Not_Fire_It()
    {
        var path = Path.Combine(_dir, "events.json");
        var fired = 0;
        using var watcher = new EventsFileWatcher(path, () => Interlocked.Increment(ref fired));

        // frame-state.json is rewritten every tick; reacting to it would be a wake a minute for nothing.
        File.WriteAllText(Path.Combine(_dir, "frame-state.json"), "{}");
        File.WriteAllText(path + ".1234.tmp", "{}");

        Thread.Sleep(700);
        Assert.Equal(0, Volatile.Read(ref fired));
    }
}
