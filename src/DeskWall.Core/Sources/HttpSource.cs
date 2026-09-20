using System.Net;
using DeskWall.Core.Layout;
using DeskWall.Core.Values;

namespace DeskWall.Core.Sources;

/// <summary>Settings: url (required, may contain {secret:x}); every (seconds, default 600); timeout (seconds, default 10);
/// header.&lt;Name&gt; = value (may contain secrets); parse = "json" | "text" (default: json if Content-Type says so, else text);
/// unixTimeFields = comma-separated field names. Publishes: json (RecordValue) or text (TextValue), status (NumberValue),
/// fetchedAt (TimeValue), fromCache (BoolValue: 304 served the previous body).</summary>
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
