using DeskWall.Core.Sources;
using DeskWall.Core.Values;
using Xunit;

file sealed class SlowSource(TimeSpan work, TimeSpan timeout) : AsyncSource("slow", TimeSpan.FromMinutes(1), timeout)
{
    public int Runs;
    protected override async Task<RecordValue> FetchAsync(CancellationToken ct)
    {
        Runs++;
        await Task.Delay(work, ct);
        return ValueTree.Of(("n", new NumberValue(Runs)));
    }
}

public class AsyncSourceTests
{
    [Fact]
    public async Task Fast_Work_Returns_Directly()
    {
        var s = new SlowSource(TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(5));
        var v = await s.RefreshAsync(default);
        Assert.Equal(1, ((NumberValue)v.Get("n")!).Number);
    }

    [Fact]
    public async Task Slow_Work_Times_Out_Then_Completes_And_Is_Reused()
    {
        var s = new SlowSource(TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(50));
        var completed = new TaskCompletionSource();
        s.Completed += _ => completed.TrySetResult();
        await Assert.ThrowsAsync<TimeoutException>(async () => await s.RefreshAsync(default));
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var v = await s.RefreshAsync(default);        // returns the finished result without a second run
        Assert.Equal(1, ((NumberValue)v.Get("n")!).Number);
        Assert.Equal(1, s.Runs);
    }

    /// <summary>Finding 11: Completed used to be decided in a continuation that raced with RefreshAsync
    /// clearing _inFlight, so an ordinary on-time refresh could report itself as a late one - which the
    /// daemon turns into a spurious extra tick.</summary>
    [Fact]
    public async Task An_On_Time_Refresh_Does_Not_Report_Itself_As_Late()
    {
        var raised = 0;
        for (var i = 0; i < 40; i++)
        {
            var s = new SlowSource(TimeSpan.Zero, TimeSpan.FromSeconds(5));
            s.Completed += _ => Interlocked.Increment(ref raised);
            await s.RefreshAsync(default);
            await Task.Yield();
        }
        await Task.Delay(50);
        Assert.Equal(0, raised);
    }
}

public class JsonValuesTests
{
    [Fact]
    public void Converts_Steam_Shape_With_Key_Field_And_Unix_Times()
    {
        var json = """
            { "response": { "total_count": 2, "games": [
                { "appid": 620, "name": "Portal 2", "rtime_last_played": 1758369600, "playtime_forever": 900 },
                { "appid": 730, "name": "CS2", "rtime_last_played": 1758283200 } ] } }
            """;
        var v = JsonValues.Parse(json, new HashSet<string> { "rtime_last_played" });
        var games = (ListValue)((RecordValue)v.Get("response")!).Get("games")!;
        Assert.Equal("appid", games.KeyField);
        Assert.Equal(2, games.Items.Count);
        Assert.Equal(620, ((NumberValue)games.Items[0].Get("appid")!).Number);
        Assert.IsType<TimeValue>(games.Items[0].Get("rtime_last_played"));
        Assert.Equal("Portal 2", ((TextValue)games.ByKey("620")!.Get("name")!).Text);
        Assert.Equal(2, ((NumberValue)((RecordValue)v.Get("response")!).Get("total_count")!).Number);
    }

    [Fact]
    public void Top_Level_Array_And_Scalar_Arrays_And_Nulls()
    {
        var v = JsonValues.Parse("""[ { "id": "a", "tags": ["x", "y"], "gone": null, "ok": true }, { "id": "b" } ]""");
        var items = (ListValue)v.Get("items")!;
        Assert.Equal("id", items.KeyField);
        var tags = (ListValue)items.Items[0].Get("tags")!;
        Assert.Null(tags.KeyField);
        Assert.Equal("y", ((TextValue)tags.Items[1].Get("value")!).Text);
        Assert.Null(items.Items[0].Get("gone"));
        Assert.True(((BoolValue)items.Items[0].Get("ok")!).Flag);
    }

    [Fact]
    public void Mixed_Object_Array_Without_Common_Key_Has_No_KeyField()
    {
        var v = JsonValues.Parse("""{ "rows": [ { "id": 1 }, { "name": "x" } ] }""");
        Assert.Null(((ListValue)v.Get("rows")!).KeyField);
    }

    /// <summary>Finding 14: a SteamID64 or a snowflake through GetDouble renders as 7.6561198E+16 and
    /// can never match a [key] lookup, which compares the value's text. Anything that still round-trips
    /// through a double stays a NumberValue so arithmetic and numeric formats are untouched.</summary>
    [Fact]
    public void An_Integer_Past_2_Pow_53_Stays_Whole()
    {
        var v = JsonValues.Parse("""{ "rows": [ { "id": 76561198012345678, "n": 9007199254740993, "small": 620, "f": 1.5 } ] }""");
        var row = ((ListValue)v.Get("rows")!).Items[0];
        Assert.Equal("76561198012345678", ((TextValue)row.Get("id")!).Text);
        Assert.Equal("9007199254740993", ((TextValue)row.Get("n")!).Text);
        Assert.Equal(620, ((NumberValue)row.Get("small")!).Number);
        Assert.Equal(1.5, ((NumberValue)row.Get("f")!).Number);
        Assert.NotNull(((ListValue)v.Get("rows")!).ByKey("76561198012345678"));
    }
}

public class SecretsTests
{
    [Fact]
    public void Substitutes_Known_And_Throws_On_Unknown_Without_Leaking()
    {
        var dir = Path.Combine(Path.GetTempPath(), "deskwall-tests");
        Directory.CreateDirectory(dir);
        var p = Path.Combine(dir, "secrets-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        File.WriteAllText(p, """{ "steamKey": "ABC123" }""");
        var s = new Secrets(p);
        Assert.Equal("ABC123", s.Get("steamKey"));
        Assert.Equal("https://x/?key=ABC123&id=7", s.Substitute("https://x/?key={secret:steamKey}&id=7"));
        Assert.True(Secrets.ContainsPlaceholder("{secret:a}"));
        Assert.False(Secrets.ContainsPlaceholder("plain"));
        var ex = Assert.Throws<KeyNotFoundException>(() => s.Substitute("{secret:nope}"));
        Assert.Contains("nope", ex.Message);
        Assert.DoesNotContain("ABC123", ex.Message);
    }

    [Fact]
    public void Missing_File_Means_No_Secrets()
    {
        var s = new Secrets(Path.Combine(Path.GetTempPath(), "deskwall-tests", "no-such-secrets.json"));
        Assert.Null(s.Get("x"));
        Assert.Equal("plain", s.Substitute("plain"));
    }
}
