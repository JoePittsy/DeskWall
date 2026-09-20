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

    [Fact]
    public async Task Missing_File_Publishes_Exists_False_Without_Throwing()
    {
        var v = await FileSource.FromDef(Def(Temp("nope.json")), new FixedClock(DateTimeOffset.UnixEpoch)).RefreshAsync(default);
        Assert.False(((BoolValue)v.Get("exists")!).Flag);
        Assert.Null(v.Get("json"));
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
}
