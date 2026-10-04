using System.Net;
using DeskWall.Core;
using DeskWall.Core.Display;
using DeskWall.Core.Layout;
using DeskWall.Core.Render;
using DeskWall.Core.Sources;
using DeskWall.Core.Tick;
using Xunit;

file sealed class ImagesFakeClock(DateTimeOffset now) : IClock { public DateTimeOffset Now { get; set; } = now; }

file sealed class BytesHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public int Calls;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { Interlocked.Increment(ref Calls); return Task.FromResult(respond(request)); }
}

public class TickRunnerImagesTests
{
    private const string ImageUrl = "https://example.invalid/cover.png";

    private static byte[] Png()
    {
        using var s = Surface.Create(4, 4);
        s.Clear(new Color(255, 1, 2, 3));
        var p = Path.GetTempFileName();
        s.SavePng(p);
        var b = File.ReadAllBytes(p);
        File.Delete(p);
        return b;
    }

    private static (string Dir, LayoutFile Layout) Scene()
    {
        var dir = Path.Combine(TestRun.Root, "tickimg-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var basePng = Path.Combine(dir, "base.png");
        using (var b = Surface.Create(320, 180)) { b.Clear(new Color(255, 30, 30, 30)); b.SavePng(basePng); }

        var layout = LayoutFile.Parse($$"""
        { "version": 1, "baseImage": {{System.Text.Json.JsonSerializer.Serialize(basePng)}},
          "components": [ { "type": "image", "id": "cover", "rect": [10, 10, 40, 40], "source": {{System.Text.Json.JsonSerializer.Serialize(ImageUrl)}} } ] }
        """);
        return (dir, layout);
    }

    private static TickRunner Runner(string dir, LayoutFile layout, RemoteImageCache cache)
    {
        var clock = new ImagesFakeClock(new DateTimeOffset(2026, 9, 20, 14, 32, 5, TimeSpan.Zero));
        var sources = layout.Sources.Select(s => SourceFactory.Create(s, clock)).ToList();
        var monitor = new MonitorInfo(new DisplaySignature("TEST", 320, 180, 100), new Rect(0, 0, 320, 180), true, "TEST");
        return new TickRunner(layout, sources, new SourceRegistry(), clock, monitor,
            statePath: Path.Combine(dir, "state.json"), outPath: Path.Combine(dir, "out.jpg"), framePath: Path.Combine(dir, "frame.raw"),
            images: cache);
    }

    /// <summary>A cover the CDN does not have is asked for once, not on every tick: the failure is
    /// backed off, and the placeholder plate stays.</summary>
    [Fact]
    public async Task A_Failed_Cover_Download_Is_Not_Retried_Every_Tick()
    {
        var (dir, layout) = Scene();
        var handler = new BytesHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var cache = new RemoteImageCache(Path.Combine(dir, "images"), handler);
        var runner = Runner(dir, layout, cache);

        await runner.RunAsync(force: true, apply: false, default);
        await cache.DownloadAsync(ImageUrl, default);   // wait out the download the first tick scheduled
        (byte A, byte R, byte G, byte B) pixel1;
        using (var frame1 = Surface.LoadRaw(Path.Combine(dir, "frame.raw"))) pixel1 = frame1.GetPixel(20, 20);

        for (var i = 0; i < 5; i++)
        {
            var t = await runner.RunAsync(force: false, apply: false, default);
            Assert.True(t.Skipped);                    // still the placeholder: nothing changed to repaint
            await cache.DownloadAsync(ImageUrl, default);
        }
        Assert.Equal(1, handler.Calls);
        using var frame = Surface.LoadRaw(Path.Combine(dir, "frame.raw"));
        Assert.Equal(pixel1, frame.GetPixel(20, 20));
    }

    [Fact]
    public async Task Remote_Image_Resolves_To_Missing_Plate_Then_Real_Pixels_After_Download()
    {
        var (dir, layout) = Scene();

        var png = Png();
        var handler = new BytesHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new ByteArrayContent(png) { Headers = { ContentType = new("image/png") } } });
        var cache = new RemoteImageCache(Path.Combine(dir, "images"), handler);

        var runner = Runner(dir, layout, cache);

        var t1 = await runner.RunAsync(force: true, apply: false, default);
        Assert.False(t1.Skipped);
        (byte A, byte R, byte G, byte B) pixel1;
        using (var frame1 = Surface.LoadRaw(Path.Combine(dir, "frame.raw"))) pixel1 = frame1.GetPixel(20, 20);

        await cache.DownloadAsync(ImageUrl, default);   // land the download the first tick scheduled

        var t2 = await runner.RunAsync(force: false, apply: false, default);
        Assert.False(t2.Skipped);   // the mapped local path changed the content key: not skipped despite force: false

        using var frame2 = Surface.LoadRaw(Path.Combine(dir, "frame.raw"));
        var pixel2 = frame2.GetPixel(20, 20);
        Assert.NotEqual(pixel1, pixel2);
        Assert.Equal(((byte)255, (byte)1, (byte)2, (byte)3), pixel2);
    }
}
