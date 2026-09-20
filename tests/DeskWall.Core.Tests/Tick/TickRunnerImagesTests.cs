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
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond(request));
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

    [Fact]
    public async Task Remote_Image_Resolves_To_Missing_Plate_Then_Real_Pixels_After_Download()
    {
        var dir = Path.Combine(Path.GetTempPath(), "deskwall-tests", "tickimg-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var basePng = Path.Combine(dir, "base.png");
        using (var b = Surface.Create(320, 180)) { b.Clear(new Color(255, 30, 30, 30)); b.SavePng(basePng); }

        var layout = LayoutFile.Parse($$"""
        { "version": 1, "baseImage": {{System.Text.Json.JsonSerializer.Serialize(basePng)}},
          "components": [ { "type": "image", "id": "cover", "rect": [10, 10, 40, 40], "source": {{System.Text.Json.JsonSerializer.Serialize(ImageUrl)}} } ] }
        """);

        var clock = new ImagesFakeClock(new DateTimeOffset(2026, 9, 20, 14, 32, 5, TimeSpan.Zero));
        var registry = new SourceRegistry();
        var sources = layout.Sources.Select(s => SourceFactory.Create(s, clock)).ToList();
        var monitor = new MonitorInfo(new DisplaySignature("TEST", 320, 180, 100), new Rect(0, 0, 320, 180), true, "TEST");

        var png = Png();
        var handler = new BytesHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new ByteArrayContent(png) { Headers = { ContentType = new("image/png") } } });
        var cache = new RemoteImageCache(Path.Combine(dir, "images"), handler);

        var runner = new TickRunner(layout, sources, registry, clock, monitor,
            statePath: Path.Combine(dir, "state.json"), outPath: Path.Combine(dir, "out.jpg"), framePath: Path.Combine(dir, "frame.raw"),
            images: cache);

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
