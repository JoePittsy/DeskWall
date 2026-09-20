using System.Net;
using DeskWall.Core.Layout;
using DeskWall.Core.Sources;
using DeskWall.Core.Values;
using Xunit;

file sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests = new();
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    { Requests.Add(request); return Task.FromResult(respond(request)); }
}

file sealed class FixedClock(DateTimeOffset now) : IClock { public DateTimeOffset Now => now; }

public class HttpSourceTests
{
    private static Secrets SecretsWith(string json)
    { var p = Path.Combine(Path.GetTempPath(), "deskwall-tests", "s-" + Guid.NewGuid().ToString("N")[..8] + ".json"); File.WriteAllText(p, json); return new Secrets(p); }

    private static SourceDef Def(string url, params (string k, string v)[] extra)
    {
        var d = new SourceDef { Name = "steam", Type = "http", EverySeconds = 600 };
        d.Settings["url"] = url;
        foreach (var (k, v) in extra) d.Settings[k] = v;
        return d;
    }

    [Fact]
    public async Task Parses_Json_Sends_Headers_And_Substitutes_Secret_In_Url()
    {
        var h = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{ "response": { "games": [ { "appid": 620, "name": "Portal 2" } ] } }""", System.Text.Encoding.UTF8, "application/json") });
        var src = HttpSource.FromDef(Def("https://api/x?key={secret:k}", ("header.Accept", "application/json")), new FixedClock(DateTimeOffset.UnixEpoch), SecretsWith("""{ "k": "SECRET" }"""), h);
        var v = await src.RefreshAsync(default);
        Assert.Equal("https://api/x?key=SECRET", h.Requests[0].RequestUri!.ToString());
        Assert.Contains("application/json", h.Requests[0].Headers.Accept.ToString());
        Assert.Equal("DeskWall/1.0", h.Requests[0].Headers.UserAgent.ToString());
        Assert.Equal(200, ((NumberValue)v.Get("status")!).Number);
        Assert.False(((BoolValue)v.Get("fromCache")!).Flag);
        var games = (ListValue)((RecordValue)((RecordValue)v.Get("json")!).Get("response")!).Get("games")!;
        Assert.Equal("Portal 2", ((TextValue)games.ByKey("620")!.Get("name")!).Text);
    }

    [Fact]
    public async Task Etag_Round_Trip_Serves_Previous_Body_On_304()
    {
        var calls = 0;
        var h = new ScriptedHandler(req =>
        {
            calls++;
            if (req.Headers.IfNoneMatch.Any(t => t.Tag == "\"v1\"")) return new HttpResponseMessage(HttpStatusCode.NotModified);
            var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{ "n": 1 }""", System.Text.Encoding.UTF8, "application/json") };
            r.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"v1\"");
            return r;
        });
        var src = HttpSource.FromDef(Def("https://api/y"), new FixedClock(DateTimeOffset.UnixEpoch), SecretsWith("{}"), h);
        var a = await src.RefreshAsync(default);
        var b = await src.RefreshAsync(default);
        Assert.Equal(2, calls);
        Assert.True(((BoolValue)b.Get("fromCache")!).Flag);
        Assert.Equal(1, ((NumberValue)((RecordValue)b.Get("json")!).Get("n")!).Number);
        Assert.Equal(304, ((NumberValue)b.Get("status")!).Number);
    }

    [Fact]
    public async Task Server_Error_Throws_With_Template_Not_Secret()
    {
        var h = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var src = HttpSource.FromDef(Def("https://api/z?key={secret:k}"), new FixedClock(DateTimeOffset.UnixEpoch), SecretsWith("""{ "k": "SECRET" }"""), h);
        var ex = await Assert.ThrowsAsync<HttpRequestException>(async () => await src.RefreshAsync(default));
        Assert.Contains("500", ex.Message);
        Assert.Contains("{secret:k}", ex.Message);
        Assert.DoesNotContain("SECRET", ex.Message);
    }

    [Fact]
    public async Task Text_Mode_Publishes_Body_As_Text()
    {
        var h = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("42.5 C", System.Text.Encoding.UTF8, "text/plain") });
        var src = HttpSource.FromDef(Def("https://api/t"), new FixedClock(DateTimeOffset.UnixEpoch), SecretsWith("{}"), h);
        var v = await src.RefreshAsync(default);
        Assert.Equal("42.5 C", ((TextValue)v.Get("text")!).Text);
        Assert.Null(v.Get("json"));
    }
}
