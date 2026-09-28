using System.Net;
using DeskWall.Core.Layout;
using DeskWall.Core.Render;
using DeskWall.Core.Sources;
using Xunit;

namespace DeskWall.Core.Tests.Sources;

/// <summary>A body with no Content-Length that never ends - what a chunked sender looks like when the
/// URL is a mistake. Non-seekable, so StreamContent reports no length at all.</summary>
file sealed class EndlessStream : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) { Array.Fill(buffer, (byte)'x', offset, count); return count; }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

file sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public int Calls;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    { Calls++; return respond(request, ct); }
}

file sealed class Clock(DateTimeOffset now) : IClock { public DateTimeOffset Now => now; }

/// <summary>Findings 5 and 6: a fetch is bounded in bytes and in wall-clock time. Content-Length is not
/// a bound (absent on anything chunked) and the client Timeout is not one either (AsyncSource hands
/// FetchAsync a token nothing ever cancels).</summary>
public class BoundedFetchTests
{
    private static Secrets NoSecrets()
    {
        var p = Path.Combine(Path.GetTempPath(), "deskwall-tests", "s-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        File.WriteAllText(p, "{}");
        return new Secrets(p);
    }

    private static HttpResponseMessage Endless(string mediaType)
    {
        var content = new StreamContent(new EndlessStream());
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType);
        Assert.Null(content.Headers.ContentLength);   // the guard the old code relied on is not there
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    [Fact]
    public async Task Http_Stops_Reading_A_Chunked_Body_At_The_Cap()
    {
        var h = new Handler((_, _) => Task.FromResult(Endless("application/json")));
        var def = new SourceDef { Name = "steam", Type = "http" };
        def.Settings["url"] = "https://api/big";
        var src = HttpSource.FromDef(def, new Clock(DateTimeOffset.UnixEpoch), NoSecrets(), h);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(async () => await src.RefreshAsync(default));
        Assert.Contains("body over", ex.Message, StringComparison.Ordinal);
        Assert.Contains("https://api/big", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rss_Stops_Reading_A_Chunked_Body_At_The_Cap()
    {
        var h = new Handler((_, _) => Task.FromResult(Endless("application/rss+xml")));
        var def = new SourceDef { Name = "feed", Type = "rss" };
        def.Settings["url"] = "https://feed/big";
        var src = RssSource.FromDef(def, NoSecrets(), h);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(async () => await src.RefreshAsync(default));
        Assert.Contains("body over", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>The template, never the substituted URL, reaches the message: the cap must not become
    /// the one place a key leaks.</summary>
    [Fact]
    public async Task The_Cap_Message_Carries_The_Template_Not_The_Secret()
    {
        var p = Path.Combine(Path.GetTempPath(), "deskwall-tests", "s-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        File.WriteAllText(p, """{ "k": "SHOULD-NOT-APPEAR" }""");
        var h = new Handler((_, _) => Task.FromResult(Endless("text/plain")));
        var def = new SourceDef { Name = "steam", Type = "http" };
        def.Settings["url"] = "https://api/x?key={secret:k}";
        var src = HttpSource.FromDef(def, new Clock(DateTimeOffset.UnixEpoch), new Secrets(p), h);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(async () => await src.RefreshAsync(default));
        Assert.DoesNotContain("SHOULD-NOT-APPEAR", ex.Message, StringComparison.Ordinal);
        Assert.Contains("{secret:k}", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A server that stalls the body used to leave the task in flight forever: every later
    /// RefreshAsync threw TimeoutException at once and the source was dead for the life of the daemon.
    /// The ceiling is six timeouts, so with a 100 ms timeout the fetch gives up inside a second.</summary>
    [Fact]
    public async Task A_Stalled_Fetch_Is_Abandoned_At_The_Hard_Ceiling()
    {
        var h = new Handler(async (_, ct) => { await Task.Delay(TimeSpan.FromMinutes(5), ct); return new HttpResponseMessage(HttpStatusCode.OK); });
        var def = new SourceDef { Name = "steam", Type = "http" };
        def.Settings["url"] = "https://api/slow";
        def.Settings["timeout"] = "0.1";
        var src = HttpSource.FromDef(def, new Clock(DateTimeOffset.UnixEpoch), NoSecrets(), h);
        var landed = new TaskCompletionSource();
        src.Changed += _ => landed.TrySetResult();

        await Assert.ThrowsAsync<TimeoutException>(async () => await src.RefreshAsync(default));

        // The abandoned fetch really does end, and reports - it does not sit in flight for ever.
        var finished = await Task.WhenAny(landed.Task, Task.Delay(TimeSpan.FromSeconds(20)));
        Assert.Same(landed.Task, finished);
        // And the next refresh gets the cancellation, not another "still running".
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await src.RefreshAsync(default));
    }

    [Fact]
    public async Task An_Oversized_Image_Leaves_No_File_And_No_Temp_File()
    {
        var dir = Path.Combine(Path.GetTempPath(), "deskwall-tests", "img-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var h = new Handler((_, _) => Task.FromResult(Endless("image/jpeg")));
            var cache = new RemoteImageCache(dir, h);

            await cache.DownloadAsync("https://cdn/huge.jpg", default);

            Assert.Empty(Directory.GetFiles(dir, "*.img"));
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
            // And it is backed off, so the next lookup does not ask again.
            await cache.DownloadAsync("https://cdn/huge.jpg", default);
            Assert.Equal(1, h.Calls);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    /// <summary>Finding 13: Landed used to be raised inside the try whose catch writes the negative
    /// cache entry, so a subscriber that threw marked a successful download as failed and blocked the
    /// URL for ten minutes.</summary>
    [Fact]
    public async Task A_Throwing_Landed_Subscriber_Does_Not_Poison_The_Cache()
    {
        var dir = Path.Combine(Path.GetTempPath(), "deskwall-tests", "img-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var h = new Handler((_, _) =>
            {
                var c = new ByteArrayContent([1, 2, 3, 4]);
                c.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = c });
            });
            var cache = new RemoteImageCache(dir, h);
            cache.Landed += _ => throw new InvalidOperationException("a subscriber went wrong");

            await cache.DownloadAsync("https://cdn/ok.png", default);

            Assert.Single(Directory.GetFiles(dir, "*.img"));
            await cache.DownloadAsync("https://cdn/ok.png", default);
            Assert.Equal(2, h.Calls);   // not negative-cached: the second call really went out
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }
}
