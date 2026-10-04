using System.Net;
using DeskWall.Core.Render;
using Xunit;

file sealed class BytesHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public int Calls;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { Calls++; return Task.FromResult(respond(request)); }
}

file sealed class GatedHandler(Func<Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => respond();
}

public class RemoteImageCacheTests
{
    private static string Dir() { var d = Path.Combine(TestRun.Root, "img-" + Guid.NewGuid().ToString("N")[..8]); Directory.CreateDirectory(d); return d; }
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

    /// <summary>#87: the in-flight entry used to be removed by a detached ContinueWith on the pool, so a
    /// download started right after the previous one finished could be handed the finished task and
    /// send nothing. With a handler that completes synchronously the removal could even run before
    /// GetOrAdd stored the task, leaving a completed task in flight for good.</summary>
    [Fact]
    public async Task A_Finished_Download_Is_Never_Handed_Back_As_In_Flight()
    {
        var png = Png();
        var h = new BytesHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(png) { Headers = { ContentType = new("image/png") } } });
        var cache = new RemoteImageCache(Dir(), h);
        const int n = 200;
        for (var i = 0; i < n; i++)
        {
            var url = $"https://cdn/{i}.png";
            await cache.DownloadAsync(url, default);
            await cache.DownloadAsync(url, default);   // revalidation: must really go out
        }
        Assert.Equal(2 * n, h.Calls);
    }

    [Fact]
    public async Task Concurrent_Downloads_Of_One_Url_Share_One_Request()
    {
        var png = Png();
        var gate = new TaskCompletionSource();
        var calls = 0;
        var h = new GatedHandler(async () =>
        {
            Interlocked.Increment(ref calls);
            await gate.Task;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(png) { Headers = { ContentType = new("image/png") } } };
        });
        var cache = new RemoteImageCache(Dir(), h);
        const string url = "https://cdn/shared.png";
        var a = cache.DownloadAsync(url, default);
        var b = cache.DownloadAsync(url, default);
        Assert.Null(cache.Lookup(url));                // a lookup while in flight joins it too
        Assert.False(a.IsCompleted);
        gate.SetResult();
        await Task.WhenAll(a, b);
        Assert.Equal(1, calls);
        Assert.NotNull(cache.Lookup(url));
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
