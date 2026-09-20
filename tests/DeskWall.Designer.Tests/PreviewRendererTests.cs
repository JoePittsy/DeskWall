using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using DeskWall.Core;
using DeskWall.Core.Display;
using DeskWall.Core.Layout;
using DeskWall.Core.Render;
using DeskWall.Core.Values;
using DeskWall.Designer.Model;
using Xunit;

public class PreviewRendererTests
{
    // No dispatcher on the xUnit thread, so Rendered is raised synchronously on the render thread
    // and the counts below need no pumping.
    private static DesignerModel Model(string baseImage) => new(LayoutFile.Parse($$"""
        { "version": 1, "baseImage": {{System.Text.Json.JsonSerializer.Serialize(baseImage)}}, "sources": [],
          "components": [
            { "type": "text", "id": "clock", "rect": [40, 30, 200, 60], "z": 1, "text": "12:34", "size": 40 },
            { "type": "bar", "id": "bar", "rect": [40, 120, 200, 8], "z": 2, "fraction": 0.5 } ] }
        """), new DisplaySignature("TEST", 320, 200, 100), null);

    /// <summary>A real 4x4 PNG so the base-image path is exercised rather than the error path.</summary>
    private static string BaseImage()
    {
        var path = Path.Combine(Paths.RuntimeDir, "preview-test-base.png");
        if (!File.Exists(path))
        {
            using var s = Surface.Create(4, 4);
            s.Clear(new Color(255, 10, 40, 80));
            s.SavePng(path);
        }
        return path;
    }

    [Fact]
    public void A_Burst_Of_Requests_Renders_Once()
    {
        using var r = new PreviewRenderer(() => ValueTree.Empty);
        var frames = new List<PreviewFrame>();
        var first = new ManualResetEventSlim();
        r.Rendered += f => { lock (frames) frames.Add(f); first.Set(); };

        var m = Model(BaseImage());
        for (var i = 0; i < 20; i++) r.Request(m);

        Assert.True(first.Wait(10_000), "no frame arrived");
        Thread.Sleep(500);                       // long enough for any straggler to land
        lock (frames) Assert.Single(frames);
    }

    [Fact]
    public void A_Request_During_A_Render_Renders_Once_More()
    {
        using var r = new PreviewRenderer(() => ValueTree.Empty);
        var count = 0;
        var second = new ManualResetEventSlim();
        var m = Model(BaseImage());
        r.Rendered += _ =>
        {
            var n = Interlocked.Increment(ref count);
            if (n == 1)
            {
                // Still inside the render loop: the debounce fires while this handler sleeps, so
                // the request lands on the in-flight render and the loop picks it up on the way out.
                r.Request(m);
                Thread.Sleep(3 * PreviewRenderer.DebounceMs);
            }
            else second.Set();
        };

        r.Request(m);

        Assert.True(second.Wait(10_000), "the second frame never arrived");
        Thread.Sleep(500);
        Assert.Equal(2, Volatile.Read(ref count));
    }

    [Fact]
    public void Frame_Carries_The_Hit_Map_And_Pixels()
    {
        using var r = new PreviewRenderer(() => ValueTree.Empty);
        PreviewFrame? frame = null;
        var done = new ManualResetEventSlim();
        r.Rendered += f => { frame = f; done.Set(); };

        r.Request(Model(BaseImage()));

        Assert.True(done.Wait(10_000), "no frame arrived");
        Assert.NotNull(frame);
        Assert.Equal(320, frame!.Width);
        Assert.Equal(200, frame.Height);
        Assert.Equal(320 * 200 * 4, frame.Bgra.Length);
        Assert.Contains(frame.Bgra, b => b != 0);                     // something was painted
        Assert.Equal(new Rect(40, 30, 200, 60), frame.Resolved.Single(c => c.Id == "clock").Rect);
        Assert.Equal(new Rect(40, 120, 200, 8), frame.Resolved.Single(c => c.Id == "bar").Rect);
    }

    [Fact]
    public void A_Bad_Base_Image_Still_Produces_A_Frame_And_A_Hit_Map()
    {
        using var r = new PreviewRenderer(() => ValueTree.Empty);
        PreviewFrame? frame = null;
        var done = new ManualResetEventSlim();
        r.Rendered += f => { frame = f; done.Set(); };

        r.Request(Model(Path.Combine(Paths.RuntimeDir, "no-such-image.jpg")));

        Assert.True(done.Wait(10_000), "no frame arrived");
        Assert.NotNull(frame);
        Assert.Equal(320 * 200 * 4, frame!.Bgra.Length);
        Assert.Equal(2, frame.Resolved.Count);
    }
}
