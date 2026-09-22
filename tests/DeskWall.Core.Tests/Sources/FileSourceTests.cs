using DeskWall.Core.Layout;
using DeskWall.Core.Sources;
using DeskWall.Core.Values;
using Xunit;

file sealed class FixedClock(DateTimeOffset now) : IClock { public DateTimeOffset Now { get; set; } = now; }

public class FileSourceTests
{
    private static string Temp(string name) { var d = Path.Combine(Path.GetTempPath(), "deskwall-tests", "file-" + Guid.NewGuid().ToString("N")[..8]); Directory.CreateDirectory(d); return Path.Combine(d, name); }

    private static SourceDef Def(string path, string? parse = null)
    {
        var d = new SourceDef { Name = "hearth", Type = "file" };
        d.Settings["path"] = path;
        if (parse is not null) d.Settings["parse"] = parse;
        return d;
    }

    [Fact]
    public async Task Json_By_Extension_With_Metadata()
    {
        var p = Temp("games.json");
        File.WriteAllText(p, """{ "games": [ { "id": "abc", "name": "Portal 2", "cover": "C:\\x.jpg" } ] }""");
        var v = await FileSource.FromDef(Def(p), new FixedClock(DateTimeOffset.UnixEpoch)).RefreshAsync(default);
        Assert.True(((BoolValue)v.Get("exists")!).Flag);
        Assert.Equal("Portal 2", ((TextValue)((ListValue)((RecordValue)v.Get("json")!).Get("games")!).ByKey("abc")!.Get("name")!).Text);
        Assert.IsType<TimeValue>(v.Get("modifiedAt"));
        Assert.True(((NumberValue)v.Get("size")!).Number > 10);
    }

    /// <summary>Spec 3.2: a missing file is a failure, not a successful { exists: false }. The partial
    /// record installed itself over the last good values, so a producer that writes its JSON by
    /// delete-then-create blanked the column for a tick instead of holding the last list.</summary>
    [Fact]
    public async Task Missing_File_Throws_So_The_Last_Good_Values_Stay()
    {
        var src = FileSource.FromDef(Def(Temp("nope.json")), new FixedClock(DateTimeOffset.UnixEpoch));
        var ex = await Assert.ThrowsAsync<FileNotFoundException>(async () => await src.RefreshAsync(default));
        Assert.Contains("nope.json", ex.Message, StringComparison.Ordinal);

        // What the tick then does with it: the previous values stay published.
        var good = new RecordValue(new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase) { ["text"] = new TextValue("last good") });
        var failed = SourceSnapshot.Initial("hearth").Succeeded(good, DateTimeOffset.UnixEpoch).Failed(ex.Message, DateTimeOffset.UnixEpoch.AddMinutes(1));
        Assert.Equal("last good", ((TextValue)failed.Values!.Get("text")!).Text);
    }

    [Fact]
    public async Task Text_Mode_And_Env_Expansion()
    {
        var p = Temp("note.txt");
        File.WriteAllText(p, "hello");
        Environment.SetEnvironmentVariable("DESKWALL_TEST_DIR", Path.GetDirectoryName(p));
        var v = await FileSource.FromDef(Def(@"%DESKWALL_TEST_DIR%\note.txt"), new FixedClock(DateTimeOffset.UnixEpoch)).RefreshAsync(default);
        Assert.Equal("hello", ((TextValue)v.Get("text")!).Text);
    }

    /// <summary>The mtime check is the belt to the watcher's braces: a network path or a container
    /// mount may raise no events at all, and the re-check still finds the change on any wake. The
    /// 300 s is the re-check interval, not the latency; the watcher carries that.</summary>
    [Fact]
    public async Task Due_When_Mtime_Changes_Else_Periodic()
    {
        var p = Temp("w.json");
        File.WriteAllText(p, "{}");
        var t0 = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
        using var src = FileSource.FromDef(Def(p), new FixedClock(t0));
        await src.RefreshAsync(default);
        Assert.Equal(t0.AddSeconds(300), src.NextDue(t0, t0));
        File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddMinutes(5));
        Assert.Equal(t0, src.NextDue(t0, t0));   // changed on disk: due now
    }

    /// <summary>The point of the watcher: a save reaches the wallpaper in well under the 300 s
    /// re-check. Before this the source polled mtime and nothing else.</summary>
    [Fact]
    public void A_Write_Raises_Changed()
    {
        var p = Temp("w.json");
        File.WriteAllText(p, "{}");
        using var src = FileSource.FromDef(Def(p), Clock());
        using var fired = new ManualResetEventSlim(false);
        src.Changed += _ => fired.Set();

        File.WriteAllText(p, """{ "n": 1 }""");

        Assert.True(fired.Wait(TimeSpan.FromSeconds(5)));
    }

    /// <summary>One Ctrl+S raises three or four FileSystemWatcher events. LayoutWatcher already
    /// solved this; the same 300 ms debounce, for the same reason - without it the daemon takes
    /// three repaints of two megabytes each for one save.</summary>
    [Fact]
    public void One_Save_Raises_One_Changed()
    {
        var p = Temp("w.json");
        File.WriteAllText(p, "{}");
        using var src = FileSource.FromDef(Def(p), Clock());
        using var fired = new ManualResetEventSlim(false);
        var n = 0;
        src.Changed += _ => { Interlocked.Increment(ref n); fired.Set(); };

        File.WriteAllText(p, """{ "n": 1, "padding": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" }""");

        Assert.True(fired.Wait(TimeSpan.FromSeconds(5)));
        Thread.Sleep(700);   // anything the save raised after the first has had two debounces to arrive
        Assert.Equal(1, n);
    }

    /// <summary>A producer that writes its JSON by delete-then-create is the case FileSource's own
    /// missing-file rule exists for, so it has to be the case the watcher survives too.</summary>
    [Fact]
    public void Delete_And_Recreate_Still_Signals()
    {
        var p = Temp("w.json");
        File.WriteAllText(p, "{}");
        using var src = FileSource.FromDef(Def(p), Clock());
        using var fired = new ManualResetEventSlim(false);
        src.Changed += _ => fired.Set();

        File.Delete(p);
        File.WriteAllText(p, """{ "n": 2 }""");

        Assert.True(fired.Wait(TimeSpan.FromSeconds(5)));
    }

    /// <summary>FileSystemWatcher throws from its constructor when the directory is not there. A
    /// layout naming a file a producer has not created yet must still load, and must still find the
    /// file through the mtime path once it appears.</summary>
    [Fact]
    public async Task A_Watcher_That_Cannot_Be_Created_Leaves_The_Source_Working()
    {
        var dir = Path.Combine(Path.GetTempPath(), "deskwall-tests", "later-" + Guid.NewGuid().ToString("N")[..8]);
        var p = Path.Combine(dir, "x.json");
        using var src = FileSource.FromDef(Def(p), Clock());   // no directory: must not throw
        await Assert.ThrowsAsync<FileNotFoundException>(async () => await src.RefreshAsync(default));

        Directory.CreateDirectory(dir);
        File.WriteAllText(p, """{ "n": 3 }""");
        var v = await src.RefreshAsync(default);

        Assert.True(((BoolValue)v.Get("exists")!).Flag);
    }

    [Fact]
    public void Dispose_Is_Idempotent()
    {
        var p = Temp("w.json");
        File.WriteAllText(p, "{}");
        var src = FileSource.FromDef(Def(p), Clock());
        src.Dispose();
        src.Dispose();
    }

    private static IClock Clock() => new FixedClock(DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task Bad_Json_Throws()
    {
        var p = Temp("bad.json");
        File.WriteAllText(p, "{ not json");
        await Assert.ThrowsAnyAsync<Exception>(async () => await FileSource.FromDef(Def(p), new FixedClock(DateTimeOffset.UnixEpoch)).RefreshAsync(default));
    }

    /// <summary>A widget that names C:\Users\<me>\AppData\Local\DeskWall\... is not portable.
    /// `path` understands the same `runtime:` prefix an image's `source` does, on top of the %ENV%
    /// expansion it already had.</summary>
    [Fact]
    public void Path_Understands_The_Runtime_Prefix_And_Still_Expands_Env()
    {
        var clock = new FixedClock(DateTimeOffset.UnixEpoch);
        Assert.Equal(DeskWall.Core.Paths.InRuntime("scripts", "downloads.json"),
            FileSource.FromDef(Def(@"runtime:scripts\downloads.json"), clock).Path);
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "win.ini"),
            FileSource.FromDef(Def(@"%SystemRoot%\win.ini"), clock).Path);
        Assert.Equal(@"C:\data\x.json", FileSource.FromDef(Def(@"C:\data\x.json"), clock).Path);
    }
}
