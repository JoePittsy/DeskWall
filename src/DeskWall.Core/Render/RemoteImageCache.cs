using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace DeskWall.Core.Render;

/// <summary>Maps http(s) image URLs to files under runtime/images/&lt;sha256-16&gt;.&lt;ext&gt;. Lookups are
/// synchronous and never touch the network: a miss returns null and schedules a background download;
/// when a download lands, Landed fires (the host posts WakeKind.SourceCompleted). Entries older than
/// MaxAge are revalidated (ETag) on the next lookup, still in the background, still serving the old file.
/// Files not looked up for 30 days are deleted on Sweep().</summary>
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

    private static string Key(string url) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..16];
    private string FileFor(string url) => Path.Combine(dir, Key(url) + ".img");
    private string MetaFor(string url) => Path.Combine(dir, Key(url) + ".meta");   // line 1: url, line 2: etag or empty, line 3: fetched-at O

    public string? Lookup(string url)
    {
        var file = FileFor(url);
        if (File.Exists(file))
        {
            File.SetLastAccessTimeUtc(file, DateTime.UtcNow);
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > _maxAge) _ = DownloadAsync(url, CancellationToken.None);   // stale: revalidate in the background, serve the old file now
            return file;
        }
        _ = DownloadAsync(url, CancellationToken.None);   // background download; Lookup never blocks on the network
        return null;
    }

    /// <summary>Exposed for tests; Lookup calls it fire-and-forget. A URL already downloading (whether
    /// started by Lookup's background schedule or by a previous direct call) is shared rather than
    /// starting a second concurrent request for the same image.</summary>
    public Task DownloadAsync(string url, CancellationToken ct)
    {
        if (_negative.TryGetValue(url, out var until) && until > DateTimeOffset.UtcNow) return Task.CompletedTask;
        return _inFlight.GetOrAdd(url, u =>
        {
            var t = DoDownloadAsync(u, ct);
            t.ContinueWith(_ => _inFlight.TryRemove(new KeyValuePair<string, Task>(u, t)), TaskScheduler.Default);
            return t;
        });
    }

    private async Task DoDownloadAsync(string url, CancellationToken ct)
    {
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
