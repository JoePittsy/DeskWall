# DeskWall v1 Phase 4: Sources Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The five remaining built-in sources from spec 4.1 (`http`, `rss`, `file`, `command`,
`system`), the remote image cache, and secrets by reference, so the Steam recipe
(`layouts/steam-recent.json`) renders four recent-game covers from the public Steam API with no
Steam-specific code in the daemon.

**Architecture:** Every source implements the frozen `ISource` from Phase 1. A shared
`JsonValues` converter turns any JSON document into the `Value` tree. `Secrets` resolves
`{secret:name}` placeholders from `secrets.json`. `RemoteImageCache` maps http(s) URLs in resolved
image components to local files, downloading off the tick with revalidation, and posts a
`SourceCompleted` wake when a download lands. `SourceFactory` gains five cases.

**Tech Stack:** .NET 10 (`HttpClient`, `System.Text.Json` source-gen where a fixed shape exists,
`JsonDocument` for arbitrary input, `XmlReader`, `Process`, `Microsoft.Win32.Registry`), xUnit.
One new NuGet: `System.Diagnostics.EventLog` (Microsoft, AOT-annotated) for `system.daysSinceCrash`.

**Spec:** `docs/superpowers/specs/2026-09-20-deskwall-v1-design.md` (sections 4.1, 4.2, 4.3, 3.2)
**Master plan:** `docs/superpowers/plans/2026-09-20-deskwall-v1-master.md`

## Global Constraints

- Everything in the master plan's Global Constraints, plus:
- **Never block the tick on the network or a child process.** `RefreshAsync` is awaited by the tick
  with a timeout (`SourceDef.Settings["timeout"]` seconds, default 10). On timeout the tick marks
  the source failed and moves on; the in-flight task continues, and on completion the source
  raises `Completed` so the host can post `WakeKind.SourceCompleted`. Implement via the
  `AsyncSource` base class in Task 1, do not hand-roll it per source.
- **Last good value survives failure.** A failed refresh must not clear `SourceSnapshot.Values`
  (Phase 1's `Failed()` already keeps them); a source must throw, not return an empty record, when
  it has nothing valid.
- **Secrets never reach a layout file, a log line, or a content key.** Substitution happens inside
  the source at request time; the resolved URL with the key in it is never logged (log the
  template).
- **Idle cost zero.** No timers inside sources, no background loops, no `HttpClient` per call (one
  static `HttpClient` with a 10 s connect timeout). Image downloads run on the thread pool and
  finish; nothing lingers.
- ASCII-only sources. Tests under `DESKWALL_HOME`. No real network in unit tests: `http` and
  `rss` take an `HttpMessageHandler` for tests and use a local `HttpListener` only in one
  integration test marked `[Trait("Category","Network")]`.
- Lane assignment (cap stated in the ledger before dispatch): Task 1 seam by the controller; then
  five Sonnet lanes `lane/p4-http` (Tasks 2, 3), `lane/p4-rss` (Task 4), `lane/p4-file` (Task 5),
  `lane/p4-command` (Task 6), `lane/p4-system` (Task 7); Tasks 8, 9 by the controller.

## Interfaces shipped by Phase 1 that this phase consumes

```csharp
interface ISource { string Name; DateTimeOffset NextDue(DateTimeOffset? lastRefresh, DateTimeOffset now); ValueTask<RecordValue> RefreshAsync(CancellationToken ct) }
abstract class PeriodicSource(string name, TimeSpan every) : ISource   // NextDue = last + every
Value: TextValue(string) | NumberValue(double) | TimeValue(DateTimeOffset) | BoolValue(bool) | ImageValue(string) | RecordValue(IReadOnlyDictionary<string,Value>) | ListValue(IReadOnlyList<RecordValue>, string? KeyField)
SourceDef { Name, Type, EverySeconds, Settings: Dictionary<string,string> }
SourceFactory.Create(SourceDef, IClock) : ISource          // switch on Type; add cases here
IClock { Now }
ResolvedImage(Id, Rect, Z, Path, Fit, Radius, Opacity)     // Path may be an http(s) URL after resolution
LayoutResolver.Resolve(layout, tree, canvas) : IReadOnlyList<Resolved>
Paths.InRuntime(...)
```

## File structure

```
src/DeskWall.Core/Sources/AsyncSource.cs          timeout + Completed event base (Task 1)
src/DeskWall.Core/Sources/JsonValues.cs           JsonElement -> Value (Task 1)
src/DeskWall.Core/Sources/Secrets.cs              secrets.json + {secret:x} substitution (Task 1)
src/DeskWall.Core/Sources/HttpSource.cs           (Task 2)
src/DeskWall.Core/Render/RemoteImageCache.cs      (Task 3)
src/DeskWall.Core/Sources/RssSource.cs            (Task 4)
src/DeskWall.Core/Sources/FileSource.cs           (Task 5)
src/DeskWall.Core/Sources/CommandSource.cs        (Task 6)
src/DeskWall.Core/Sources/SystemSource.cs         (Task 7)
src/DeskWall.Core/Sources/SourceFactory.cs        add cases (Task 8)
src/DeskWall.Core/Tick/TickRunner.cs              URL -> cache path mapping before render (Task 8)
layouts/steam-recent.json                         the recipe (Task 9)
tests/DeskWall.Core.Tests/Sources/*Tests.cs
```

---

### Task 1: Seam: `AsyncSource`, `JsonValues`, `Secrets` (controller)

**Files:**
- Create: `src/DeskWall.Core/Sources/AsyncSource.cs`, `src/DeskWall.Core/Sources/JsonValues.cs`, `src/DeskWall.Core/Sources/Secrets.cs`
- Test: `tests/DeskWall.Core.Tests/Sources/AsyncSourceTests.cs`, `tests/DeskWall.Core.Tests/Sources/JsonValuesTests.cs`, `tests/DeskWall.Core.Tests/Sources/SecretsTests.cs`

**Interfaces:**
- Produces:

```csharp
/// <summary>Periodic source whose work may be slow. RefreshAsync races the work against Timeout:
/// on timeout it throws TimeoutException (the tick marks the source failed) and lets the work
/// finish in the background; when it finishes, Completed fires with the result and the next
/// RefreshAsync returns it immediately without re-running.</summary>
public abstract class AsyncSource(string name, TimeSpan every, TimeSpan timeout) : PeriodicSource(name, every)
{
    public TimeSpan Timeout => timeout;
    /// <summary>Raised (on a pool thread) when a refresh that overran its timeout finally completes, successfully or not.</summary>
    public event Action<ISource>? Completed;
    protected abstract Task<RecordValue> FetchAsync(CancellationToken ct);
    public sealed override ValueTask<RecordValue> RefreshAsync(CancellationToken ct);
}

public static class JsonValues
{
    /// <summary>Whole document to a RecordValue. A top-level array becomes { "items": [...] }.
    /// Objects -> RecordValue; arrays of objects -> ListValue with KeyField = first of ("id","appid","key","letter","name") present in every item, else null;
    /// arrays of scalars -> ListValue of records { "value": scalar }; numbers -> NumberValue; strings -> TextValue; bool -> BoolValue; null -> omitted.
    /// unixTimeFields: field names whose numbers are Unix seconds and become TimeValue (local).</summary>
    public static RecordValue From(JsonElement root, IReadOnlySet<string>? unixTimeFields = null);
    public static RecordValue Parse(string json, IReadOnlySet<string>? unixTimeFields = null);
}

/// <summary>secrets.json in the runtime dir: { "steamKey": "...", ... }. Never logged.</summary>
public sealed class Secrets(string path)
{
    public static Secrets Default();
    public string? Get(string name);
    /// <summary>Replace every {secret:name} in text. Unknown names throw KeyNotFoundException naming the secret, not its value.</summary>
    public string Substitute(string text);
    public static bool ContainsPlaceholder(string text);
}
```

- [ ] **Step 1: Failing tests**

```csharp
using System.Text.Json;
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
}

public class JsonValuesTests
{
    [Fact]
    public void Converts_Steam_Shape_With_Key_Field_And_Unix_Times()
    {
        var json = """{ "response": { "total_count": 2, "games": [
            { "appid": 620, "name": "Portal 2", "rtime_last_played": 1758369600, "playtime_forever": 900 },
            { "appid": 730, "name": "CS2", "rtime_last_played": 1758283200 } ] } }""";
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
}

public class SecretsTests
{
    [Fact]
    public void Substitutes_Known_And_Throws_On_Unknown_Without_Leaking()
    {
        var p = Path.Combine(Path.GetTempPath(), "deskwall-tests", "secrets-" + Guid.NewGuid().ToString("N")[..8] + ".json");
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
```

- [ ] **Step 2: Run, expect compile failure.**

- [ ] **Step 3: Implement**

`AsyncSource.cs`:

```csharp
using DeskWall.Core.Values;

namespace DeskWall.Core.Sources;

public abstract class AsyncSource(string name, TimeSpan every, TimeSpan timeout) : PeriodicSource(name, every)
{
    private readonly object _lock = new();
    private Task<RecordValue>? _inFlight;

    public TimeSpan Timeout => timeout;
    public event Action<ISource>? Completed;

    protected abstract Task<RecordValue> FetchAsync(CancellationToken ct);

    public sealed override async ValueTask<RecordValue> RefreshAsync(CancellationToken ct)
    {
        Task<RecordValue> work;
        lock (_lock)
        {
            if (_inFlight is { IsCompleted: true } done) { _inFlight = null; return await done; }   // overran last time; hand back the result (or rethrow its failure)
            work = _inFlight ??= Start(ct);
        }
        var finished = await Task.WhenAny(work, Task.Delay(timeout, ct));
        if (finished != work) throw new TimeoutException($"source '{Name}' exceeded {timeout.TotalSeconds:0} s; still running");
        lock (_lock) _inFlight = null;
        return await work;
    }

    private Task<RecordValue> Start(CancellationToken ct)
    {
        var t = Task.Run(() => FetchAsync(CancellationToken.None), CancellationToken.None);   // never cancelled by the tick's token: it must finish and report
        t.ContinueWith(_ =>
        {
            bool overran; lock (_lock) overran = ReferenceEquals(_inFlight, t);
            if (overran) Completed?.Invoke(this);
        }, TaskScheduler.Default);
        return t;
    }
}
```

`JsonValues.cs`:

```csharp
using System.Text.Json;
using DeskWall.Core.Values;

namespace DeskWall.Core.Sources;

public static class JsonValues
{
    private static readonly string[] KeyCandidates = ["id", "name", "key", "letter", "appid"];

    public static RecordValue Parse(string json, IReadOnlySet<string>? unixTimeFields = null)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        return From(doc.RootElement, unixTimeFields);
    }

    public static RecordValue From(JsonElement root, IReadOnlySet<string>? unixTimeFields = null)
    {
        if (root.ValueKind == JsonValueKind.Array) return new RecordValue(new Dictionary<string, Value> { ["items"] = List(root, unixTimeFields) });
        if (root.ValueKind != JsonValueKind.Object) throw new FormatException("JSON root must be an object or an array");
        return Record(root, unixTimeFields);
    }

    private static RecordValue Record(JsonElement obj, IReadOnlySet<string>? unix)
    {
        var d = new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in obj.EnumerateObject())
        {
            var v = Convert(p.Value, p.Name, unix);
            if (v is not null) d[p.Name] = v;
        }
        return new RecordValue(d);
    }

    private static ListValue List(JsonElement arr, IReadOnlySet<string>? unix)
    {
        var items = new List<RecordValue>();
        var allObjects = true;
        foreach (var e in arr.EnumerateArray())
        {
            if (e.ValueKind == JsonValueKind.Object) items.Add(Record(e, unix));
            else
            {
                allObjects = false;
                var v = Convert(e, "value", unix);
                items.Add(new RecordValue(v is null ? new Dictionary<string, Value>() : new Dictionary<string, Value> { ["value"] = v }));
            }
        }
        string? key = null;
        if (allObjects && items.Count > 0)
            key = KeyCandidates.FirstOrDefault(k => items.All(i => i.Get(k) is TextValue or NumberValue));
        // ByKey compares TextValue only; expose numeric keys as text too so [620] lookups work.
        if (key is not null && items.Any(i => i.Get(key) is NumberValue))
            items = items.Select(i => new RecordValue(i.Fields.ToDictionary(kv => kv.Key, kv => kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase) && kv.Value is NumberValue n ? new TextValue(n.ToText(null)) : kv.Value, StringComparer.OrdinalIgnoreCase))).ToList();
        return new ListValue(items, key);
    }

    private static Value? Convert(JsonElement e, string name, IReadOnlySet<string>? unix) => e.ValueKind switch
    {
        JsonValueKind.Object => Record(e, unix),
        JsonValueKind.Array => List(e, unix),
        JsonValueKind.String => new TextValue(e.GetString()!),
        JsonValueKind.Number when unix is not null && unix.Contains(name) && e.TryGetInt64(out var secs) => new TimeValue(DateTimeOffset.FromUnixTimeSeconds(secs).ToLocalTime()),
        JsonValueKind.Number => new NumberValue(e.GetDouble()),
        JsonValueKind.True => new BoolValue(true),
        JsonValueKind.False => new BoolValue(false),
        _ => null,
    };
}
```

Note the `appid` key becomes text so `games[620]` works; the test asserts
`((NumberValue)games.Items[0].Get("appid")!).Number == 620`, which conflicts. Ruling for the
implementer: keep the numeric field as `NumberValue` and instead make `ListValue.ByKey` compare
numbers by their invariant text. That is a one-line change in the frozen seam
(`Values/Value.cs`): `if (r.Get(KeyField) is { } kv && string.Equals(kv.ToText(null), key, OrdinalIgnoreCase))`.
The controller applies that seam change in this task; drop the "expose numeric keys as text"
block above.

`Secrets.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DeskWall.Core.Sources;

public sealed partial class Secrets(string path)
{
    private Dictionary<string, string>? _map;

    public static Secrets Default() => new(Paths.InRuntime("secrets.json"));

    [GeneratedRegex(@"\{secret:([A-Za-z0-9_\-]+)\}")]
    private static partial Regex Placeholder();

    public static bool ContainsPlaceholder(string text) => Placeholder().IsMatch(text);

    public string? Get(string name)
    {
        _map ??= Load();
        return _map.TryGetValue(name, out var v) ? v : null;
    }

    public string Substitute(string text)
        => Placeholder().Replace(text, m => Get(m.Groups[1].Value) ?? throw new KeyNotFoundException($"secret '{m.Groups[1].Value}' is not defined in secrets.json"));

    private Dictionary<string, string> Load()
    {
        if (!File.Exists(path)) return new(StringComparer.OrdinalIgnoreCase);
        var d = JsonSerializer.Deserialize(File.ReadAllText(path), SecretsJsonContext.Default.DictionaryStringString) ?? new();
        return new(d, StringComparer.OrdinalIgnoreCase);
    }
}

[JsonSerializable(typeof(Dictionary<string, string>))]
internal partial class SecretsJsonContext : JsonSerializerContext;
```

`GeneratedRegex` is AOT-safe (source-generated).

- [ ] **Step 4: Run tests, expect pass** (7). `dotnet build` zero warnings.
- [ ] **Step 5: Commit** `git commit -m "Phase 4 seam: AsyncSource (timeout + Completed), JsonValues, Secrets; ByKey compares by text"`

Fan-out starts here: five lanes.

---

### Task 2: `HttpSource` (lane `lane/p4-http`, Sonnet)

**Files:**
- Create: `src/DeskWall.Core/Sources/HttpSource.cs`
- Test: `tests/DeskWall.Core.Tests/Sources/HttpSourceTests.cs`

**Interfaces:**
- Consumes: `AsyncSource`, `JsonValues`, `Secrets`.
- Produces:

```csharp
/// <summary>Settings: url (required, may contain {secret:x}); every (seconds, default 600); timeout (seconds, default 10);
/// header.<Name> = value (may contain secrets); parse = "json" | "text" (default: json if Content-Type says so, else text);
/// unixTimeFields = comma-separated field names. Publishes: json (RecordValue) or text (TextValue), status (NumberValue),
/// fetchedAt (TimeValue), fromCache (BoolValue: 304 served the previous body).</summary>
public sealed class HttpSource(string name, TimeSpan every, TimeSpan timeout, string urlTemplate, IReadOnlyDictionary<string,string> headers, string? parse, IReadOnlySet<string> unixTimeFields, Secrets secrets, IClock clock, HttpMessageHandler? handler = null) : AsyncSource(name, every, timeout)
{
    public static HttpSource FromDef(SourceDef def, IClock clock, Secrets secrets, HttpMessageHandler? handler = null);
}
```

Behaviour: one static `HttpClient` per handler (tests pass a fake handler). Sends
`If-None-Match` with the last ETag and `If-Modified-Since` with the last `Last-Modified`; on 304
returns the previous parsed body with `fromCache = true`. Non-2xx/304 throws
`HttpRequestException($"{status} from {templateUrl}")` (template, not substituted). Body over
4 MB throws. User-Agent `DeskWall/1.0`.

- [ ] **Step 1: Failing tests** (fake handler returning scripted responses; assert JSON parse, headers sent including the substituted secret, ETag round trip with 304 -> fromCache and same body, 500 -> throws with template URL in message and no secret in message, text mode).

```csharp
using System.Net;
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
```

- [ ] **Step 2: Run, expect compile failure.**
- [ ] **Step 3: Implement**

```csharp
using System.Net;
using System.Net.Http.Headers;
using DeskWall.Core.Layout;
using DeskWall.Core.Values;

namespace DeskWall.Core.Sources;

public sealed class HttpSource(string name, TimeSpan every, TimeSpan timeout, string urlTemplate, IReadOnlyDictionary<string, string> headers,
    string? parse, IReadOnlySet<string> unixTimeFields, Secrets secrets, IClock clock, HttpMessageHandler? handler = null) : AsyncSource(name, every, timeout)
{
    private const long MaxBody = 4 * 1024 * 1024;
    private static readonly HttpClient s_shared = new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10), PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
    private readonly HttpClient _client = handler is null ? s_shared : new HttpClient(handler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

    private string? _etag, _lastModified;
    private RecordValue? _lastBody;

    public static HttpSource FromDef(SourceDef def, IClock clock, Secrets secrets, HttpMessageHandler? handler = null)
    {
        var s = def.Settings;
        if (!s.TryGetValue("url", out var url) || string.IsNullOrWhiteSpace(url)) throw new ArgumentException($"http source '{def.Name}' needs settings.url");
        var headers = s.Where(kv => kv.Key.StartsWith("header.", StringComparison.OrdinalIgnoreCase)).ToDictionary(kv => kv.Key["header.".Length..], kv => kv.Value);
        var unix = new HashSet<string>((s.TryGetValue("unixTimeFields", out var u) ? u : "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase);
        var timeout = TimeSpan.FromSeconds(s.TryGetValue("timeout", out var t) && double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ts) ? ts : 10);
        return new HttpSource(def.Name, TimeSpan.FromSeconds(def.EverySeconds ?? 600), timeout, url, headers, s.TryGetValue("parse", out var p) ? p : null, unix, secrets, clock, handler);
    }

    protected override async Task<RecordValue> FetchAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, secrets.Substitute(urlTemplate));
        req.Headers.UserAgent.ParseAdd("DeskWall/1.0");
        foreach (var (k, v) in headers) req.Headers.TryAddWithoutValidation(k, secrets.Substitute(v));
        if (_etag is not null) req.Headers.IfNoneMatch.ParseAdd(_etag);
        if (_lastModified is not null) req.Headers.TryAddWithoutValidation("If-Modified-Since", _lastModified);

        using var res = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (res.StatusCode == HttpStatusCode.NotModified && _lastBody is not null)
            return Publish(_lastBody, 304, fromCache: true);
        if (!res.IsSuccessStatusCode) throw new HttpRequestException($"{(int)res.StatusCode} from {urlTemplate}");
        if (res.Content.Headers.ContentLength is > MaxBody) throw new HttpRequestException($"body over {MaxBody} bytes from {urlTemplate}");

        var body = await res.Content.ReadAsStringAsync(ct);
        if (body.Length > MaxBody) throw new HttpRequestException($"body over {MaxBody} bytes from {urlTemplate}");
        _etag = res.Headers.ETag?.ToString();
        _lastModified = res.Content.Headers.LastModified?.ToString("R");

        var mode = parse ?? (res.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true ? "json" : "text");
        var payload = new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase);
        if (mode == "json") payload["json"] = JsonValues.Parse(body, unixTimeFields);
        else payload["text"] = new TextValue(body);
        _lastBody = new RecordValue(payload);
        return Publish(_lastBody, (int)res.StatusCode, fromCache: false);
    }

    private RecordValue Publish(RecordValue body, int status, bool fromCache)
    {
        var d = new Dictionary<string, Value>(body.Fields, StringComparer.OrdinalIgnoreCase)
        {
            ["status"] = new NumberValue(status),
            ["fetchedAt"] = new TimeValue(clock.Now),
            ["fromCache"] = new BoolValue(fromCache),
        };
        return new RecordValue(d);
    }
}
```

- [ ] **Step 4: Run tests, expect pass** (4).
- [ ] **Step 5: Commit** `git commit -m "HttpSource: GET with secrets, headers, ETag/304, json or text"`

---

### Task 3: `RemoteImageCache` (lane `lane/p4-http`, Sonnet)

**Files:**
- Create: `src/DeskWall.Core/Render/RemoteImageCache.cs`
- Test: `tests/DeskWall.Core.Tests/Render/RemoteImageCacheTests.cs`

**Interfaces:**
- Produces:

```csharp
/// <summary>Maps http(s) image URLs to files under runtime/images/<sha256-16>.<ext>. Lookups are
/// synchronous and never touch the network: a miss returns null and schedules a background download;
/// when a download lands, Landed fires (the host posts WakeKind.SourceCompleted). Entries older than
/// MaxAge are revalidated (ETag) on the next lookup, still in the background, still serving the old file.
/// Files not looked up for 30 days are deleted on Sweep().</summary>
public sealed class RemoteImageCache(string dir, HttpMessageHandler? handler = null, TimeSpan? maxAge = null)
{
    public static RemoteImageCache Default();
    public static bool IsRemote(string pathOrUrl);               // http:// or https://
    public string? Lookup(string url);                           // local path or null (download scheduled)
    public event Action<string>? Landed;                         // url
    public Task DownloadAsync(string url, CancellationToken ct); // exposed for tests; Lookup calls it fire-and-forget
    public int Sweep();                                          // returns deleted count
}
```

- [ ] **Step 1: Failing tests** (fake handler serving PNG bytes; `Lookup` returns null first and the file path after `DownloadAsync` completes; a second `Lookup` is a hit with no request; a non-image content type or 404 records a negative entry for 10 minutes so the tick does not hammer the endpoint; `Sweep` deletes a file with an old last-access marker; `IsRemote`).

```csharp
using System.Net;
using DeskWall.Core.Render;
using Xunit;

file sealed class BytesHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public int Calls;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { Calls++; return Task.FromResult(respond(request)); }
}

public class RemoteImageCacheTests
{
    private static string Dir() { var d = Path.Combine(Path.GetTempPath(), "deskwall-tests", "img-" + Guid.NewGuid().ToString("N")[..8]); Directory.CreateDirectory(d); return d; }
    private static byte[] Png() { using var s = Surface.Create(4, 4); s.Clear(new Color(255, 1, 2, 3)); var p = Path.GetTempFileName(); s.SavePng(p); var b = File.ReadAllBytes(p); File.Delete(p); return b; }

    [Fact]
    public async Task Miss_Then_Hit_After_Download()
    {
        var png = Png();
        var h = new BytesHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(png) { Headers = { ContentType = new("image/png") } } });
        var cache = new RemoteImageCache(Dir(), h);
        string? landed = null; cache.Landed += u => landed = u;
        const string url = "https://cdn/x/620/library_600x900.jpg";
        Assert.Null(cache.Lookup(url));
        await cache.DownloadAsync(url, default);
        var path = cache.Lookup(url);
        Assert.NotNull(path);
        Assert.True(File.Exists(path));
        Assert.Equal(url, landed);
        Assert.Equal(1, h.Calls);
        Assert.NotNull(cache.Lookup(url));
        Assert.Equal(1, h.Calls);
        using var s = Surface.Load(path!);
        Assert.Equal((4, 4), (s.Width, s.Height));
    }

    [Fact]
    public async Task Not_Found_Is_Remembered_Briefly()
    {
        var h = new BytesHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var cache = new RemoteImageCache(Dir(), h);
        const string url = "https://cdn/missing.jpg";
        Assert.Null(cache.Lookup(url));
        await cache.DownloadAsync(url, default);
        Assert.Null(cache.Lookup(url));
        await cache.DownloadAsync(url, default);      // negative entry: no second request
        Assert.Equal(1, h.Calls);
    }

    [Fact]
    public void IsRemote_Distinguishes_Urls_From_Paths()
    {
        Assert.True(RemoteImageCache.IsRemote("https://x/y.jpg"));
        Assert.True(RemoteImageCache.IsRemote("HTTP://x/y.jpg"));
        Assert.False(RemoteImageCache.IsRemote(@"C:\img\y.jpg"));
        Assert.False(RemoteImageCache.IsRemote(""));
    }
}
```

- [ ] **Step 2: Run, expect compile failure.**
- [ ] **Step 3: Implement**

```csharp
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace DeskWall.Core.Render;

public sealed class RemoteImageCache(string dir, HttpMessageHandler? handler = null, TimeSpan? maxAge = null)
{
    private static readonly HttpClient s_shared = new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) }) { Timeout = TimeSpan.FromSeconds(30) };
    private readonly HttpClient _client = handler is null ? s_shared : new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    private readonly TimeSpan _maxAge = maxAge ?? TimeSpan.FromHours(24);
    private readonly ConcurrentDictionary<string, Task> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _negative = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan NegativeTtl = TimeSpan.FromMinutes(10);

    public event Action<string>? Landed;

    public static RemoteImageCache Default() => new(Paths.InRuntime("images"));

    public static bool IsRemote(string pathOrUrl)
        => pathOrUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || pathOrUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private string Key(string url) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..16];
    private string FileFor(string url) => Path.Combine(dir, Key(url) + ".img");
    private string MetaFor(string url) => Path.Combine(dir, Key(url) + ".meta");   // line 1: url, line 2: etag or empty, line 3: fetched-at O

    public string? Lookup(string url)
    {
        var file = FileFor(url);
        if (File.Exists(file))
        {
            File.SetLastAccessTimeUtc(file, DateTime.UtcNow);
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > _maxAge) Schedule(url);   // stale: revalidate in the background, serve the old file now
            return file;
        }
        Schedule(url);
        return null;
    }

    private void Schedule(string url)
    {
        if (_negative.TryGetValue(url, out var until) && until > DateTimeOffset.UtcNow) return;
        _inFlight.GetOrAdd(url, u => DownloadAsync(u, CancellationToken.None).ContinueWith(_ => _inFlight.TryRemove(u, out _), TaskScheduler.Default));
    }

    public async Task DownloadAsync(string url, CancellationToken ct)
    {
        if (_negative.TryGetValue(url, out var until) && until > DateTimeOffset.UtcNow) return;
        Directory.CreateDirectory(dir);
        var file = FileFor(url); var meta = MetaFor(url);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd("DeskWall/1.0");
            if (File.Exists(meta) && File.Exists(file))
            {
                var lines = File.ReadAllLines(meta);
                if (lines.Length > 1 && lines[1].Length > 0) req.Headers.IfNoneMatch.ParseAdd(lines[1]);
            }
            using var res = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (res.StatusCode == HttpStatusCode.NotModified) { File.SetLastWriteTimeUtc(file, DateTime.UtcNow); return; }
            if (!res.IsSuccessStatusCode || res.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true)
            {
                _negative[url] = DateTimeOffset.UtcNow + NegativeTtl;
                return;
            }
            var tmp = file + ".tmp";
            await using (var fs = File.Create(tmp)) await res.Content.CopyToAsync(fs, ct);
            File.Move(tmp, file, overwrite: true);
            File.WriteAllLines(meta, [url, res.Headers.ETag?.ToString() ?? "", DateTimeOffset.UtcNow.ToString("O")]);
            Landed?.Invoke(url);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            _negative[url] = DateTimeOffset.UtcNow + NegativeTtl;   // network down: back off, keep any old file
        }
    }

    public int Sweep()
    {
        if (!Directory.Exists(dir)) return 0;
        var n = 0;
        foreach (var f in Directory.EnumerateFiles(dir, "*.img"))
            if (DateTime.UtcNow - File.GetLastAccessTimeUtc(f) > TimeSpan.FromDays(30))
            { File.Delete(f); var m = Path.ChangeExtension(f, ".meta"); if (File.Exists(m)) File.Delete(m); n++; }
        return n;
    }
}
```

`File.SetLastAccessTimeUtc` works even when NTFS last-access updates are disabled system-wide,
because we set it explicitly.

- [ ] **Step 4: Run tests, expect pass** (3).
- [ ] **Step 5: Commit** `git commit -m "RemoteImageCache: URL -> local file with background download, ETag revalidation, negative cache"`

Lane `lane/p4-http` complete.

---

### Task 4: `RssSource` (lane `lane/p4-rss`, Sonnet)

**Files:**
- Create: `src/DeskWall.Core/Sources/RssSource.cs`
- Test: `tests/DeskWall.Core.Tests/Sources/RssSourceTests.cs`

**Interfaces:**
- Consumes: `AsyncSource`, `Secrets` (url may carry a secret for private feeds).
- Produces:

```csharp
/// <summary>Settings: url (required); every (default 900); timeout (default 10); max (items, default 20).
/// Publishes: title (TextValue), link (TextValue), items (ListValue keyed by "link") with fields
/// title, link, published (TimeValue, or omitted), summary (TextValue, HTML tags stripped, max 500 chars), author (TextValue, or omitted).
/// Accepts RSS 2.0 (channel/item, pubDate) and Atom 1.0 (feed/entry, published|updated, link[@rel=alternate|none]/@href).</summary>
public sealed class RssSource(string name, TimeSpan every, TimeSpan timeout, string urlTemplate, int max, Secrets secrets, HttpMessageHandler? handler = null) : AsyncSource(name, every, timeout)
{
    public static RssSource FromDef(SourceDef def, Secrets secrets, HttpMessageHandler? handler = null);
    /// <summary>Pure parser, exposed for tests and for FileSource users who point it at a saved feed.</summary>
    public static RecordValue ParseFeed(string xml, int max);
}
```

Use `XmlReader` with `DtdProcessing.Prohibit` and `XmlResolver = null` (no external entities).
Dates: `DateTimeOffset.TryParse` with `RFC1123` first (`pubDate`), then round-trip `O`
(Atom); unparseable dates are omitted, not thrown. `RecordValue` fields are omitted rather than
empty when a feed lacks them.

- [ ] **Step 1: Failing tests**

```csharp
using DeskWall.Core.Sources;
using DeskWall.Core.Values;
using Xunit;

public class RssSourceTests
{
    private const string Rss = """
    <?xml version="1.0"?><rss version="2.0"><channel><title>Blog</title><link>https://blog/</link>
      <item><title>First &amp; foremost</title><link>https://blog/1</link><pubDate>Sat, 20 Sep 2026 10:00:00 GMT</pubDate><description>&lt;p&gt;Hello &lt;b&gt;world&lt;/b&gt;&lt;/p&gt;</description><author>joe@x</author></item>
      <item><title>Second</title><link>https://blog/2</link><pubDate>not a date</pubDate></item>
      <item><title>Third</title><link>https://blog/3</link></item>
    </channel></rss>
    """;

    private const string Atom = """
    <?xml version="1.0"?><feed xmlns="http://www.w3.org/2005/Atom"><title>Releases</title><link rel="self" href="https://gh/feed"/><link rel="alternate" href="https://gh/"/>
      <entry><title>v1.2</title><link rel="alternate" href="https://gh/v1.2"/><updated>2026-09-19T08:30:00Z</updated><summary>Bug fixes</summary><author><name>Joe</name></author></entry>
      <entry><title>v1.1</title><link href="https://gh/v1.1"/><published>2026-09-01T00:00:00Z</published><content type="html">&lt;ul&gt;&lt;li&gt;Thing&lt;/li&gt;&lt;/ul&gt;</content></entry>
    </feed>
    """;

    [Fact]
    public void Parses_Rss2_With_Entities_Html_Stripping_And_Bad_Dates()
    {
        var v = RssSource.ParseFeed(Rss, 20);
        Assert.Equal("Blog", ((TextValue)v.Get("title")!).Text);
        Assert.Equal("https://blog/", ((TextValue)v.Get("link")!).Text);
        var items = (ListValue)v.Get("items")!;
        Assert.Equal("link", items.KeyField);
        Assert.Equal(3, items.Items.Count);
        var first = items.Items[0];
        Assert.Equal("First & foremost", ((TextValue)first.Get("title")!).Text);
        Assert.Equal("Hello world", ((TextValue)first.Get("summary")!).Text);
        Assert.Equal(new DateTimeOffset(2026, 9, 20, 10, 0, 0, TimeSpan.Zero), ((TimeValue)first.Get("published")!).Time);
        Assert.Equal("joe@x", ((TextValue)first.Get("author")!).Text);
        Assert.Null(items.Items[1].Get("published"));
        Assert.Null(items.Items[2].Get("summary"));
        Assert.Same(items.Items[1], items.ByKey("https://blog/2"));
    }

    [Fact]
    public void Parses_Atom_With_Alternate_Links_And_Content_Fallback()
    {
        var v = RssSource.ParseFeed(Atom, 20);
        Assert.Equal("Releases", ((TextValue)v.Get("title")!).Text);
        Assert.Equal("https://gh/", ((TextValue)v.Get("link")!).Text);
        var items = (ListValue)v.Get("items")!;
        Assert.Equal("https://gh/v1.2", ((TextValue)items.Items[0].Get("link")!).Text);
        Assert.Equal("Joe", ((TextValue)items.Items[0].Get("author")!).Text);
        Assert.Equal(new DateTimeOffset(2026, 9, 19, 8, 30, 0, TimeSpan.Zero), ((TimeValue)items.Items[0].Get("published")!).Time);
        Assert.Equal("Thing", ((TextValue)items.Items[1].Get("summary")!).Text);
        Assert.Equal("https://gh/v1.1", ((TextValue)items.Items[1].Get("link")!).Text);
    }

    [Fact]
    public void Max_Caps_Items_And_Garbage_Throws()
    {
        Assert.Single(((ListValue)RssSource.ParseFeed(Rss, 1).Get("items")!).Items);
        Assert.ThrowsAny<Exception>(() => RssSource.ParseFeed("<html>not a feed</html>", 5));
        Assert.ThrowsAny<Exception>(() => RssSource.ParseFeed("<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///c:/x'>]><rss><channel><title>&e;</title></channel></rss>", 5));
    }
}
```

- [ ] **Step 2: Run, expect compile failure.**
- [ ] **Step 3: Implement**

```csharp
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Xml;
using DeskWall.Core.Layout;
using DeskWall.Core.Values;

namespace DeskWall.Core.Sources;

public sealed partial class RssSource(string name, TimeSpan every, TimeSpan timeout, string urlTemplate, int max, Secrets secrets, HttpMessageHandler? handler = null)
    : AsyncSource(name, every, timeout)
{
    private static readonly HttpClient s_shared = new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) }) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
    private readonly HttpClient _client = handler is null ? s_shared : new HttpClient(handler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

    public static RssSource FromDef(SourceDef def, Secrets secrets, HttpMessageHandler? handler = null)
    {
        var s = def.Settings;
        if (!s.TryGetValue("url", out var url) || string.IsNullOrWhiteSpace(url)) throw new ArgumentException($"rss source '{def.Name}' needs settings.url");
        var timeout = TimeSpan.FromSeconds(s.TryGetValue("timeout", out var t) && double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var ts) ? ts : 10);
        var max = s.TryGetValue("max", out var m) && int.TryParse(m, out var mi) && mi > 0 ? mi : 20;
        return new RssSource(def.Name, TimeSpan.FromSeconds(def.EverySeconds ?? 900), timeout, url, max, secrets, handler);
    }

    protected override async Task<RecordValue> FetchAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, secrets.Substitute(urlTemplate));
        req.Headers.UserAgent.ParseAdd("DeskWall/1.0");
        using var res = await _client.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) throw new HttpRequestException($"{(int)res.StatusCode} from {urlTemplate}");
        return ParseFeed(await res.Content.ReadAsStringAsync(ct), max);
    }

    public static RecordValue ParseFeed(string xml, int max)
    {
        var doc = new XmlDocument { XmlResolver = null };
        using (var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
            doc.Load(reader);
        var root = doc.DocumentElement ?? throw new FormatException("empty feed");
        return root.LocalName switch
        {
            "rss" => Rss2(root, max),
            "feed" => Atom(root, max),
            _ => throw new FormatException($"not an RSS or Atom feed (root <{root.LocalName}>)"),
        };
    }

    private static RecordValue Rss2(XmlElement rss, int max)
    {
        var channel = rss["channel"] ?? throw new FormatException("rss without channel");
        var items = new List<RecordValue>();
        foreach (XmlElement item in channel.GetElementsByTagName("item"))
        {
            if (items.Count >= max) break;
            var d = new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase);
            Put(d, "title", Text(item["title"]));
            Put(d, "link", Text(item["link"]));
            Put(d, "summary", Strip(Text(item["description"])));
            Put(d, "author", Text(item["author"]) ?? Text(item["dc:creator"]));
            if (Date(Text(item["pubDate"])) is { } p) d["published"] = new TimeValue(p);
            items.Add(new RecordValue(d));
        }
        var feed = new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase) { ["items"] = new ListValue(items, "link") };
        Put(feed, "title", Text(channel["title"]));
        Put(feed, "link", Text(channel["link"]));
        return new RecordValue(feed);
    }

    private static RecordValue Atom(XmlElement feedEl, int max)
    {
        var items = new List<RecordValue>();
        foreach (XmlElement entry in Children(feedEl, "entry"))
        {
            if (items.Count >= max) break;
            var d = new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase);
            Put(d, "title", Text(Child(entry, "title")));
            Put(d, "link", AtomLink(entry));
            Put(d, "summary", Strip(Text(Child(entry, "summary")) ?? Text(Child(entry, "content"))));
            Put(d, "author", Text(Child(Child(entry, "author"), "name")));
            if (Date(Text(Child(entry, "published")) ?? Text(Child(entry, "updated"))) is { } p) d["published"] = new TimeValue(p);
            items.Add(new RecordValue(d));
        }
        var feed = new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase) { ["items"] = new ListValue(items, "link") };
        Put(feed, "title", Text(Child(feedEl, "title")));
        Put(feed, "link", AtomLink(feedEl));
        return new RecordValue(feed);
    }

    private static IEnumerable<XmlElement> Children(XmlElement? parent, string localName)
        => parent is null ? [] : parent.ChildNodes.OfType<XmlElement>().Where(e => e.LocalName == localName);
    private static XmlElement? Child(XmlElement? parent, string localName) => Children(parent, localName).FirstOrDefault();

    private static string? AtomLink(XmlElement el)
    {
        var links = Children(el, "link").ToList();
        var pick = links.FirstOrDefault(l => l.GetAttribute("rel") is "alternate" or "") ?? links.FirstOrDefault(l => l.GetAttribute("rel") != "self");
        var href = pick?.GetAttribute("href");
        return string.IsNullOrEmpty(href) ? null : href;
    }

    private static string? Text(XmlElement? e)
    {
        var t = e?.InnerText?.Trim();
        return string.IsNullOrEmpty(t) ? null : t;
    }

    private static void Put(Dictionary<string, Value> d, string k, string? v) { if (v is not null) d[k] = new TextValue(v); }

    private static DateTimeOffset? Date(string? s)
    {
        if (s is null) return null;
        if (DateTimeOffset.TryParseExact(s, "r", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var r)) return r;
        if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var any)) return any;
        return null;
    }

    [GeneratedRegex("<[^>]+>")] private static partial Regex Tags();
    [GeneratedRegex(@"\s+")] private static partial Regex Spaces();

    private static string? Strip(string? html)
    {
        if (html is null) return null;
        var t = Spaces().Replace(WebUtility.HtmlDecode(Tags().Replace(html, " ")), " ").Trim();
        if (t.Length > 500) t = t[..500];
        return t.Length == 0 ? null : t;
    }
}
```

`XmlDocument` is in the shared framework and AOT-safe. `item["dc:creator"]` matches by
qualified name in `XmlElement`'s indexer, which is what RSS feeds use.

- [ ] **Step 4: Run tests, expect pass** (3).
- [ ] **Step 5: Commit** `git commit -m "RssSource: RSS 2.0 and Atom to items list, HTML stripped, secure XML"`

Lane `lane/p4-rss` complete.

---

### Task 5: `FileSource` (lane `lane/p4-file`, Sonnet)

**Files:**
- Create: `src/DeskWall.Core/Sources/FileSource.cs`
- Test: `tests/DeskWall.Core.Tests/Sources/FileSourceTests.cs`

**Interfaces:**
- Produces:

```csharp
/// <summary>Settings: path (required; %ENV% expanded); parse = json | text | rss (default by extension: .json, .xml/.rss/.atom, else text);
/// every (default 30 s: the cheap re-check of mtime; the daemon also wakes on the file watcher in Phase 2 style, Task 8);
/// unixTimeFields as for http. Publishes: json | text | (rss shape), plus modifiedAt (TimeValue), size (NumberValue), exists (BoolValue).
/// NextDue returns now when the file's mtime changed since the last refresh, so the tick picks a change up on any wake.</summary>
public sealed class FileSource(string name, TimeSpan every, string path, string? parse, IReadOnlySet<string> unixTimeFields, IClock clock) : ISource
{
    public static FileSource FromDef(SourceDef def, IClock clock);
    public string Path { get; }
}
```

Missing file: publishes `{ exists: false }` only (does not throw; a Hearth export may not have
happened yet). Unparseable content throws (keeps the last good value).

- [ ] **Step 1: Failing tests**

```csharp
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
```

- [ ] **Step 2: Run, expect compile failure.**
- [ ] **Step 3: Implement**

```csharp
using DeskWall.Core.Layout;
using DeskWall.Core.Values;

namespace DeskWall.Core.Sources;

public sealed class FileSource(string name, TimeSpan every, string path, string? parse, IReadOnlySet<string> unixTimeFields, IClock clock) : ISource
{
    private DateTime _seenMtime;

    public string Name => name;
    public string Path { get; } = Environment.ExpandEnvironmentVariables(path);

    public static FileSource FromDef(SourceDef def, IClock clock)
    {
        var s = def.Settings;
        if (!s.TryGetValue("path", out var p) || string.IsNullOrWhiteSpace(p)) throw new ArgumentException($"file source '{def.Name}' needs settings.path");
        var unix = new HashSet<string>((s.TryGetValue("unixTimeFields", out var u) ? u : "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase);
        return new FileSource(def.Name, TimeSpan.FromSeconds(def.EverySeconds ?? 30), p, s.TryGetValue("parse", out var mode) ? mode : null, unix, clock);
    }

    public DateTimeOffset NextDue(DateTimeOffset? lastRefresh, DateTimeOffset now)
    {
        if (lastRefresh is null) return now;
        var mtime = File.Exists(Path) ? File.GetLastWriteTimeUtc(Path) : DateTime.MinValue;
        return mtime != _seenMtime ? now : lastRefresh.Value + every;
    }

    public ValueTask<RecordValue> RefreshAsync(CancellationToken ct)
    {
        var d = new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(Path))
        {
            _seenMtime = DateTime.MinValue;
            d["exists"] = new BoolValue(false);
            return new(new RecordValue(d));
        }
        var info = new FileInfo(Path);
        _seenMtime = info.LastWriteTimeUtc;
        var text = File.ReadAllText(Path);
        var mode = parse ?? (info.Extension.ToLowerInvariant() switch { ".json" => "json", ".xml" or ".rss" or ".atom" => "rss", _ => "text" });
        switch (mode)
        {
            case "json": d["json"] = JsonValues.Parse(text, unixTimeFields); break;
            case "rss": foreach (var kv in RssSource.ParseFeed(text, 50).Fields) d[kv.Key] = kv.Value; break;
            default: d["text"] = new TextValue(text); break;
        }
        d["exists"] = new BoolValue(true);
        d["modifiedAt"] = new TimeValue(new DateTimeOffset(info.LastWriteTimeUtc).ToLocalTime());
        d["size"] = new NumberValue(info.Length);
        return new(new RecordValue(d));
    }
}
```

`RssSource.ParseFeed` is in the parallel `lane/p4-rss`; until it merges, this lane compiles
against a stub: create `src/DeskWall.Core/Sources/RssSource.cs` containing only
`public static partial class RssSourceStub` **no**. Ruling: the file lane does NOT reference
`RssSource`. Drop the `"rss"` case; the controller adds it in Task 8 after both lanes merge.
Remove `.xml/.rss/.atom` from the extension switch (they fall to text) and from the XML doc comment.

- [ ] **Step 4: Run tests, expect pass** (5).
- [ ] **Step 5: Commit** `git commit -m "FileSource: local json/text with mtime-driven due time and env expansion"`

Lane `lane/p4-file` complete.

---

### Task 6: `CommandSource` (lane `lane/p4-command`, Sonnet)

**Files:**
- Create: `src/DeskWall.Core/Sources/CommandSource.cs`
- Test: `tests/DeskWall.Core.Tests/Sources/CommandSourceTests.cs`

**Interfaces:**
- Produces:

```csharp
/// <summary>Settings: command (required: program), args (optional, may contain {secret:x}), workingDir, every (default 600), timeout (default 10),
/// parse = json | text (default: json if stdout starts with { or [, else text), unixTimeFields.
/// Runs hidden (no window), captures stdout (UTF-8) and stderr. Publishes: text | json, exitCode (NumberValue), ranAt (TimeValue), stderr (TextValue when non-empty).
/// Non-zero exit does not throw (users may script that); a timeout kills the process tree and throws.</summary>
public sealed class CommandSource(string name, TimeSpan every, TimeSpan timeout, string command, string? args, string? workingDir, string? parse, IReadOnlySet<string> unixTimeFields, Secrets secrets, IClock clock) : AsyncSource(name, every, timeout)
{
    public static CommandSource FromDef(SourceDef def, IClock clock, Secrets secrets);
}
```

- [ ] **Step 1: Failing tests** (use `cmd.exe /c` so no extra tooling is needed)

```csharp
using DeskWall.Core.Sources;
using DeskWall.Core.Values;
using Xunit;

file sealed class FixedClock(DateTimeOffset now) : IClock { public DateTimeOffset Now => now; }

public class CommandSourceTests
{
    private static Secrets NoSecrets() => new(Path.Combine(Path.GetTempPath(), "deskwall-tests", "none.json"));

    private static SourceDef Def(string command, string args, params (string k, string v)[] extra)
    {
        var d = new SourceDef { Name = "cmd", Type = "command", EverySeconds = 60 };
        d.Settings["command"] = command; d.Settings["args"] = args;
        foreach (var (k, v) in extra) d.Settings[k] = v;
        return d;
    }

    [Fact]
    public async Task Captures_Stdout_As_Text_And_ExitCode()
    {
        var v = await CommandSource.FromDef(Def("cmd.exe", "/c echo hello & exit 3"), new FixedClock(DateTimeOffset.UnixEpoch), NoSecrets()).RefreshAsync(default);
        Assert.Equal("hello", ((TextValue)v.Get("text")!).Text.Trim());
        Assert.Equal(3, ((NumberValue)v.Get("exitCode")!).Number);
        Assert.IsType<TimeValue>(v.Get("ranAt"));
        Assert.Null(v.Get("stderr"));
    }

    [Fact]
    public async Task Json_Stdout_Is_Parsed()
    {
        var v = await CommandSource.FromDef(Def("cmd.exe", "/c echo {\"up\": 5, \"name\": \"x\"}"), new FixedClock(DateTimeOffset.UnixEpoch), NoSecrets()).RefreshAsync(default);
        Assert.Equal(5, ((NumberValue)((RecordValue)v.Get("json")!).Get("up")!).Number);
    }

    [Fact]
    public async Task Stderr_Is_Captured()
    {
        var v = await CommandSource.FromDef(Def("cmd.exe", "/c echo oops 1>&2"), new FixedClock(DateTimeOffset.UnixEpoch), NoSecrets()).RefreshAsync(default);
        Assert.Contains("oops", ((TextValue)v.Get("stderr")!).Text);
    }

    [Fact]
    public async Task Timeout_Kills_And_Throws()
    {
        var src = CommandSource.FromDef(Def("cmd.exe", "/c ping -n 30 127.0.0.1 > nul", ("timeout", "1")), new FixedClock(DateTimeOffset.UnixEpoch), NoSecrets());
        await Assert.ThrowsAsync<TimeoutException>(async () => await src.RefreshAsync(default));
    }

    [Fact]
    public void Missing_Command_Setting_Throws_On_Create()
        => Assert.Throws<ArgumentException>(() => CommandSource.FromDef(new SourceDef { Name = "c", Type = "command" }, new FixedClock(DateTimeOffset.UnixEpoch), NoSecrets()));
}
```

Note `Timeout_Kills_And_Throws`: `AsyncSource` throws `TimeoutException` at 1 s while the
`ping` keeps running; `CommandSource.FetchAsync` must itself enforce the timeout on the
process (kill after `Timeout + 1 s`) so nothing is left behind. Assert that too: after the
throw, `Process.GetProcessesByName("ping")` started by us is gone within 3 s (use a unique
marker: pass `-n 30` and check no `ping` older than the test start remains).

- [ ] **Step 2: Run, expect compile failure.**
- [ ] **Step 3: Implement**

```csharp
using System.Diagnostics;
using System.Globalization;
using System.Text;
using DeskWall.Core.Layout;
using DeskWall.Core.Values;

namespace DeskWall.Core.Sources;

public sealed class CommandSource(string name, TimeSpan every, TimeSpan timeout, string command, string? args, string? workingDir, string? parse,
    IReadOnlySet<string> unixTimeFields, Secrets secrets, IClock clock) : AsyncSource(name, every, timeout)
{
    public static CommandSource FromDef(SourceDef def, IClock clock, Secrets secrets)
    {
        var s = def.Settings;
        if (!s.TryGetValue("command", out var cmd) || string.IsNullOrWhiteSpace(cmd)) throw new ArgumentException($"command source '{def.Name}' needs settings.command");
        var timeout = TimeSpan.FromSeconds(s.TryGetValue("timeout", out var t) && double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var ts) ? ts : 10);
        var unix = new HashSet<string>((s.TryGetValue("unixTimeFields", out var u) ? u : "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase);
        return new CommandSource(def.Name, TimeSpan.FromSeconds(def.EverySeconds ?? 600), timeout, cmd, s.GetValueOrDefault("args"), s.GetValueOrDefault("workingDir"), s.GetValueOrDefault("parse"), unix, secrets, clock);
    }

    protected override async Task<RecordValue> FetchAsync(CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = Environment.ExpandEnvironmentVariables(command),
            Arguments = args is null ? "" : secrets.Substitute(args),
            WorkingDirectory = workingDir is null ? "" : Environment.ExpandEnvironmentVariables(workingDir),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"could not start {command}");
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        using var kill = new CancellationTokenSource(Timeout + TimeSpan.FromSeconds(1));   // hard stop slightly after the tick gives up
        try { await p.WaitForExitAsync(kill.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new TimeoutException($"command '{command}' exceeded {Timeout.TotalSeconds:0} s and was killed");
        }
        var outText = await stdout; var errText = await stderr;
        var d = new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase)
        {
            ["exitCode"] = new NumberValue(p.ExitCode),
            ["ranAt"] = new TimeValue(clock.Now),
        };
        var trimmed = outText.TrimStart();
        var mode = parse ?? (trimmed.StartsWith('{') || trimmed.StartsWith('[') ? "json" : "text");
        if (mode == "json") d["json"] = JsonValues.Parse(outText, unixTimeFields);
        else d["text"] = new TextValue(outText);
        if (!string.IsNullOrWhiteSpace(errText)) d["stderr"] = new TextValue(errText);
        return new RecordValue(d);
    }
}
```

`echo {"up": 5}` through `cmd.exe` keeps the quotes as typed; if the test's JSON arrives
mangled, change the test to `/c echo {"up":5}` without spaces rather than changing the source.

- [ ] **Step 4: Run tests, expect pass** (5).
- [ ] **Step 5: Commit** `git commit -m "CommandSource: hidden process with timeout kill, stdout as json/text, stderr, exit code"`

Lane `lane/p4-command` complete.

---

### Task 7: `SystemSource` (lane `lane/p4-system`, Sonnet)

**Files:**
- Create: `src/DeskWall.Core/Sources/SystemSource.cs`
- Modify: `src/DeskWall.Core/DeskWall.Core.csproj` (add `<PackageReference Include="System.Diagnostics.EventLog" Version="10.*" />`), master plan Global Constraints already lists it as allowed via this plan
- Test: `tests/DeskWall.Core.Tests/Sources/SystemSourceTests.cs`

**Interfaces:**
- Produces:

```csharp
/// <summary>every default 900. Publishes: uptime (NumberValue seconds), uptimeText (TextValue "3d 4h"), bootedAt (TimeValue),
/// daysSinceCrash (NumberValue; days since the newest System-log event id 41 (Kernel-Power) or 1001 (BugCheck); -1 if none in the log),
/// lastCrashAt (TimeValue, omitted if none), pendingReboot (BoolValue: any of the registry markers below exists),
/// machine (TextValue), user (TextValue).</summary>
public sealed class SystemSource(string name, TimeSpan every, IClock clock, Func<DateTimeOffset?>? crashProbe = null, Func<bool>? rebootProbe = null) : PeriodicSource(name, every)
{
    public static SystemSource FromDef(SourceDef def, IClock clock);
    public static DateTimeOffset? NewestCrashEvent();   // EventLog("System"): entries with InstanceId 41 or 1001, newest first; read at most the last 2000 entries
    public static bool RebootPending();                 // HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending exists, or
                                                        // HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired exists, or
                                                        // HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\PendingFileRenameOperations has a value
}
```

`crashProbe`/`rebootProbe` exist so tests inject values; the real probes are static so a test
can also call them once against the real machine and only assert they do not throw.

- [ ] **Step 1: Failing tests**

```csharp
using DeskWall.Core.Sources;
using DeskWall.Core.Values;
using Xunit;

file sealed class FixedClock(DateTimeOffset now) : IClock { public DateTimeOffset Now => now; }

public class SystemSourceTests
{
    [Fact]
    public async Task Publishes_Uptime_Crash_Days_And_Reboot_From_Probes()
    {
        var now = new DateTimeOffset(2026, 9, 20, 14, 0, 0, TimeSpan.Zero);
        var src = new SystemSource("sys", TimeSpan.FromMinutes(15), new FixedClock(now), crashProbe: () => now.AddDays(-12.4), rebootProbe: () => true);
        var v = await src.RefreshAsync(default);
        Assert.Equal(12, ((NumberValue)v.Get("daysSinceCrash")!).Number);
        Assert.IsType<TimeValue>(v.Get("lastCrashAt"));
        Assert.True(((BoolValue)v.Get("pendingReboot")!).Flag);
        Assert.True(((NumberValue)v.Get("uptime")!).Number > 0);
        Assert.Matches(@"^\d+d \d+h$|^\d+h \d+m$", ((TextValue)v.Get("uptimeText")!).Text);
        Assert.Equal(Environment.MachineName, ((TextValue)v.Get("machine")!).Text);
    }

    [Fact]
    public async Task No_Crash_Means_Minus_One_And_No_LastCrashAt()
    {
        var src = new SystemSource("sys", TimeSpan.FromMinutes(15), new FixedClock(DateTimeOffset.UnixEpoch), crashProbe: () => null, rebootProbe: () => false);
        var v = await src.RefreshAsync(default);
        Assert.Equal(-1, ((NumberValue)v.Get("daysSinceCrash")!).Number);
        Assert.Null(v.Get("lastCrashAt"));
    }

    [Fact]
    public void Real_Probes_Do_Not_Throw()
    {
        _ = SystemSource.NewestCrashEvent();
        _ = SystemSource.RebootPending();
    }
}
```

- [ ] **Step 2: Run, expect compile failure.**
- [ ] **Step 3: Implement**

```csharp
using System.Diagnostics;
using DeskWall.Core.Layout;
using DeskWall.Core.Values;
using Microsoft.Win32;

namespace DeskWall.Core.Sources;

public sealed class SystemSource(string name, TimeSpan every, IClock clock, Func<DateTimeOffset?>? crashProbe = null, Func<bool>? rebootProbe = null)
    : PeriodicSource(name, every)
{
    public static SystemSource FromDef(SourceDef def, IClock clock) => new(def.Name, TimeSpan.FromSeconds(def.EverySeconds ?? 900), clock);

    public override ValueTask<RecordValue> RefreshAsync(CancellationToken ct)
    {
        var now = clock.Now;
        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        var d = new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase)
        {
            ["uptime"] = new NumberValue(Math.Round(uptime.TotalSeconds)),
            ["uptimeText"] = new TextValue(uptime.TotalDays >= 1 ? $"{(int)uptime.TotalDays}d {uptime.Hours}h" : $"{(int)uptime.TotalHours}h {uptime.Minutes}m"),
            ["bootedAt"] = new TimeValue(now - uptime),
            ["machine"] = new TextValue(Environment.MachineName),
            ["user"] = new TextValue(Environment.UserName),
            ["pendingReboot"] = new BoolValue((rebootProbe ?? RebootPending)()),
        };
        var crash = (crashProbe ?? NewestCrashEvent)();
        d["daysSinceCrash"] = new NumberValue(crash is null ? -1 : Math.Floor((now - crash.Value).TotalDays));
        if (crash is not null) d["lastCrashAt"] = new TimeValue(crash.Value);
        return new(new RecordValue(d));
    }

    public static DateTimeOffset? NewestCrashEvent()
    {
        try
        {
            using var log = new EventLog("System");
            var entries = log.Entries;
            var count = entries.Count;
            for (var i = count - 1; i >= Math.Max(0, count - 2000); i--)
            {
                var e = entries[i];
                if (e.InstanceId is 41 or 1001 && e.EntryType is EventLogEntryType.Error or EventLogEntryType.Warning)
                    return new DateTimeOffset(e.TimeGenerated);
            }
            return null;
        }
        catch (Exception) { return null; }   // no access, or the log is unavailable: report "no crash known"
    }

    public static bool RebootPending()
    {
        try
        {
            using var cbs = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
            if (cbs is not null) return true;
            using var wu = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
            if (wu is not null) return true;
            using var sm = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager");
            return sm?.GetValue("PendingFileRenameOperations") is string[] { Length: > 0 };
        }
        catch (Exception) { return false; }
    }
}
```

`EventLog.Entries[i]` reads one record at a time; 2000 is a bound on work, not a scan of the
whole log. If `System.Diagnostics.EventLog` produces an IL2xxx/IL3xxx warning under
`IsAotCompatible`, stop and report NEEDS_CONTEXT with the exact warning: the fallback is
`EvtQuery`/`EvtNext` via CsWin32 with an XPath filter `*[System[(EventID=41 or EventID=1001)]]`,
which the controller will decide on.

- [ ] **Step 4: Run tests, expect pass** (3). `dotnet build` zero warnings.
- [ ] **Step 5: Commit** `git commit -m "SystemSource: uptime, days since crash (event 41/1001), pending reboot"`

Lane `lane/p4-system` complete.

---

### Task 8: Integration: factory cases, URL mapping in the tick, completion wakes (controller)

**Files:**
- Modify: `src/DeskWall.Core/Sources/SourceFactory.cs`, `src/DeskWall.Core/Sources/FileSource.cs` (add the `rss` parse case now that `RssSource` exists), `src/DeskWall.Core/Tick/TickRunner.cs`, `src/DeskWall.Daemon/DaemonLoop.cs` (Phase 2)
- Test: `tests/DeskWall.Core.Tests/Sources/SourceFactoryTests.cs` (extend), `tests/DeskWall.Core.Tests/Tick/TickRunnerImagesTests.cs`

**Interfaces:**
- Produces: `SourceFactory.Create(SourceDef, IClock, Secrets? secrets = null)` (overload; the old signature stays and uses `Secrets.Default()`); `TickRunner` gains an optional `RemoteImageCache? images` ctor parameter; `TickRunner.MapRemoteImages(IReadOnlyList<Resolved>) : IReadOnlyList<Resolved>` (internal, tested via the tick).

- [ ] **Step 1: Failing tests**

Extend `SourceFactoryTests.Creates_Known_Types_And_Rejects_Unknown` with one line per new type
asserting the concrete type (`http` needs `url`, `rss` needs `url`, `file` needs `path`,
`command` needs `command`, `system` needs nothing).

`TickRunnerImagesTests`: a layout with one `image` component whose `source` is a literal
`https://example.invalid/cover.png`; a `RemoteImageCache` over a fake handler that serves a 4x4
PNG. First tick: the image resolves to the "missing" plate (path "" so `FrameRenderer` draws the
dark plate) and the cache has scheduled a download; await `cache.DownloadAsync(url)`; second tick
with `force: false` must NOT be skipped (the mapped path changed the content key) and the frame
pixel inside the image rect is the PNG's colour.

- [ ] **Step 2: Implement**

`SourceFactory`:

```csharp
public static ISource Create(SourceDef def, IClock clock) => Create(def, clock, Secrets.Default());

public static ISource Create(SourceDef def, IClock clock, Secrets secrets)
{
    var every = TimeSpan.FromSeconds(def.EverySeconds ?? DefaultEvery(def.Type));
    return def.Type.ToLowerInvariant() switch
    {
        "time" => new TimeSource(def.Name, clock),
        "disks" => new DisksSource(def.Name, every),
        "system" => SystemSource.FromDef(def, clock),
        "file" => FileSource.FromDef(def, clock),
        "http" => HttpSource.FromDef(def, clock, secrets),
        "rss" => RssSource.FromDef(def, secrets),
        "command" => CommandSource.FromDef(def, clock, secrets),
        _ => throw new NotSupportedException($"source type '{def.Type}' (source '{def.Name}')"),
    };
}
```

`TickRunner`: after `LayoutResolver.Resolve`, replace every `ResolvedImage` whose `Path` is remote:

```csharp
var resolved = MapRemoteImages(LayoutResolver.Resolve(layout, registry.Tree(), canvas));
...
private IReadOnlyList<Resolved> MapRemoteImages(IReadOnlyList<Resolved> all)
{
    if (images is null) return all;
    var outList = new List<Resolved>(all.Count);
    foreach (var c in all)
    {
        if (c is ResolvedImage img && RemoteImageCache.IsRemote(img.Path))
        {
            var local = images.Lookup(img.Path) ?? "";
            var mapped = img with { Path = local };
            outList.Add(mapped with { ContentKey = ContentKey.Of(mapped) });
        }
        else outList.Add(c);
    }
    return outList;
}
```

Also the repeater `auto` cell height in `LayoutResolver.CellExtent` loads the image path to
measure it; for a remote URL it must ask the cache too. Give `LayoutResolver.Resolve` an optional
`Func<string, string?>? remote` parameter that `TickRunner` passes as `images.Lookup`; `CellExtent`
uses it when `RemoteImageCache.IsRemote(path)`. When the cache misses, the 2:3 placeholder height
applies, exactly as for a missing local file, and the landing wake re-resolves with the real
aspect.

`DaemonLoop` (Phase 2): `images.Landed += _ => win.Post(WakeKind.SourceCompleted)`; for every
`AsyncSource` in the active set, `Completed += _ => win.Post(WakeKind.SourceCompleted)`. A
`SourceCompleted` wake runs a normal (non-forced) tick: the changed keys do the rest. Call
`images.Sweep()` once at daemon start.

- [ ] **Step 3: `dotnet test` green; commit** `git commit -m "Sources wired: factory cases, remote image mapping in the tick, completion wakes"`

---

### Task 9: The Steam recipe and Phase 4 exit (controller)

**Files:**
- Create: `layouts/steam-recent.json`, `layouts/README.md`
- Modify: `docs/superpowers/plans/2026-09-20-phase1-spike-results.md` (append `## Phase 4 measurements`)

- [ ] **Step 1: The recipe**

`layouts/steam-recent.json`: the owner's right-hand column with the covers supplied by Steam's
public API. Uses `{secret:steamKey}` and `{secret:steamId}`; the user puts both in
`%LOCALAPPDATA%\DeskWall\secrets.json` (`layouts/README.md` says how to get a key at
steamcommunity.com/dev/apikey and the 64-bit SteamID).

```json
{
  "version": 1,
  "baseImage": "C:\\Windows\\SystemApps\\MicrosoftWindows.Client.CBS_cw5n1h2txyewy\\DesktopSpotlight\\Assets\\Images\\image_3.jpg",
  "baseFit": "cover",
  "encode": "jpeg",
  "jpegQuality": 92,
  "sources": [
    { "name": "time", "type": "time" },
    { "name": "disks", "type": "disks", "every": 300 },
    { "name": "steam", "type": "http", "every": 600, "settings": {
        "url": "https://api.steampowered.com/IPlayerService/GetRecentlyPlayedGames/v1/?key={secret:steamKey}&steamid={secret:steamId}&count=4&format=json",
        "unixTimeFields": "rtime_last_played" } }
  ],
  "components": [
    { "type": "text", "id": "clock", "rect": [3220, 40, 172, 78], "z": 1,
      "text": { "bind": "time.now | HH:mm" }, "font": "Segoe UI Light", "size": 64, "weight": 300, "align": "right" },
    { "type": "repeater", "id": "games", "rect": [3220, 142, 172, 1094], "z": 1,
      "items": { "bind": "steam.json.response.games" }, "axis": "vertical", "gap": 14, "cellHeight": "auto",
      "template": [
        { "type": "image", "id": "cover", "rect": [0, 0, 172, 258], "radius": 4,
          "source": { "bind": "appid | \"https://cdn.cloudflare.steamstatic.com/steam/apps/{0}/library_600x900.jpg\"" } },
        { "type": "shortcut", "id": "play", "rect": [0, 0, 172, 258], "slot": 0,
          "target": { "bind": "appid | \"steam://rungameid/{0}\"" }, "tooltip": { "bind": "name | \"Play {0}\"" } }
      ] },
    { "type": "repeater", "id": "drives", "rect": [3220, 1260, 172, 92], "z": 1,
      "items": { "bind": "disks.drives" }, "axis": "vertical", "gap": 0, "cellHeight": 46,
      "template": [
        { "type": "text", "id": "letter", "rect": [0, 0, 60, 24], "text": { "bind": "letter | \"{0}:\"" }, "size": 15 },
        { "type": "text", "id": "free", "rect": [60, 0, 112, 24], "text": { "bind": "freeGB | \"{0:N0} GB free\"" }, "size": 15, "align": "right" },
        { "type": "bar", "id": "bar", "rect": [0, 26, 172, 6], "fraction": { "bind": "usedFraction" }, "threshold": 0.85, "thresholdFill": "#FFD13438" }
      ] }
  ]
}
```

Note `GetRecentlyPlayedGames` orders by recent play and includes only the last two weeks; the
POC's "most recent installed" semantics differ slightly (this shows recently *played*, which is
the job statement). `count=4` matches the four slots. The shortcut draws nothing until Phase 3
places icons; the layout is still valid now.

- [ ] **Step 2: Live check** (needs the owner's key and id in `secrets.json`)

```powershell
& $exe layouts set layouts\steam-recent.json
& $exe tick --force --measure          # first: covers missing (downloads scheduled), plates drawn
Start-Sleep 5
& $exe tick --measure                  # second: covers landed -> redrawn 4+
```

Expected: four cover images in the column, correct aspect (cells sized by the real 600x900
images), disks below, clock above. Record the two tables under `## Phase 4 measurements` and
note the download sizes from `%LOCALAPPDATA%\DeskWall\images`.

Also confirm the failure modes by hand, each one line in the results file:
- wrong key -> log shows `403 from https://api.steampowered.com/...{secret:steamKey}...` (template, not the key), tick keeps the previous frame;
- network unplugged -> `TimeoutException` after 10 s, previous values shown, tooltip shows `ERROR see log`, next tick after `every` recovers.

- [ ] **Step 3: Phase 4 exit criteria**

- [ ] `dotnet test` green on `v1` (all new source tests included).
- [ ] No test hits the real network; the one `[Trait("Category","Network")]` test (if written) is excluded from the default run in the test csproj.
- [ ] `dotnet build` zero warnings including the `System.Diagnostics.EventLog` reference.
- [ ] Steam recipe renders live with real covers; failure modes verified as above.
- [ ] Idle footprint unchanged from Phase 2 after the daemon has fetched (no `HttpClient` per call, no lingering tasks: thread count back to the Phase 2 idle number within a minute of a fetch).

## Self-review notes

- Spec 4.1 table: `time`, `disks` (Phase 1); `system`, `command`, `http`, `rss`, `file` (Tasks 7, 6, 2, 4, 5). Every "publishes" column item is present, plus `fromCache`/`stderr`/`exists` extras that cost nothing.
- Spec 4.1 rules: off-tick network and command (Task 1 `AsyncSource`), remote image cache keyed by URL hash with revalidation (Task 3), secrets by reference never in layouts (Task 1; `Secrets.Substitute` at request time only).
- Spec 3.2: failed refresh keeps last good values (Phase 1 `SourceSnapshot.Failed`), sources throw rather than return junk (each task).
- Spec 4.2: the binding grammar is unchanged; `unixTimeFields` is a source setting, not a binding feature, which keeps the grammar closed.
- Type names: `AsyncSource`, `JsonValues.Parse/From`, `Secrets.Substitute`, `RemoteImageCache.Lookup/IsRemote/Landed`, `HttpSource.FromDef(def, clock, secrets, handler)`, `RssSource.FromDef(def, secrets, handler)` and `ParseFeed`, `FileSource.FromDef(def, clock)`, `CommandSource.FromDef(def, clock, secrets)`, `SystemSource.FromDef(def, clock)` used consistently in Task 8.
- Ruling embedded in Task 5: the file lane does not reference `RssSource`; the controller adds the `rss` parse case in Task 8. Task 1 embeds the `ListValue.ByKey` change (compare by `ToText`) that the seam needs for numeric keys.
