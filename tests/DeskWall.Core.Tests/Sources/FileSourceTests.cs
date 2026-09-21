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

    [Fact]
    public async Task Due_When_Mtime_Changes_Else_Periodic()
    {
        var p = Temp("w.json");
        File.WriteAllText(p, "{}");
        var t0 = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
        var src = FileSource.FromDef(Def(p), new FixedClock(t0));
        await src.RefreshAsync(default);
        Assert.Equal(t0.AddSeconds(30), src.NextDue(t0, t0));
        File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddMinutes(5));
        Assert.Equal(t0, src.NextDue(t0, t0));   // changed on disk: due now
    }

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
