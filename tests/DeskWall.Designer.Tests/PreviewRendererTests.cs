using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using DeskWall.Core;
using DeskWall.Core.Display;
using DeskWall.Core.Layout;
using DeskWall.Core.Render;
using DeskWall.Core.Resolve;
using DeskWall.Core.Values;
using DeskWall.Designer.Tests.Widgets;
using DeskWall.Core.Widgets;
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

    /// <summary>The widget editor's canvas: no photograph on purpose, so no error plate either.</summary>
    [Fact]
    public void No_Base_Image_Renders_On_Flat_Grey_Without_An_Error()
    {
        using var r = new PreviewRenderer(() => ValueTree.Empty);
        PreviewFrame? frame = null;
        var done = new ManualResetEventSlim();
        r.Rendered += f => { frame = f; done.Set(); };

        r.Request(Model(""));

        Assert.True(done.Wait(10_000), "no frame arrived");
        Assert.NotNull(frame);
        Assert.Equal(2, frame!.Resolved.Count);
        // The flat fill is (32, 32, 32) everywhere the two components are not; an error plate is
        // drawn at (32, 32) in a strong red, so its absence is the thing being asserted.
        var i = ((10 * 320) + 300) * 4;
        Assert.Equal(32, frame.Bgra[i]);
        Assert.Equal(32, frame.Bgra[i + 1]);
        Assert.Equal(32, frame.Bgra[i + 2]);
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

    private static PreviewFrame RenderOnce(DesignerModel model, Viewport? view = null)
    {
        using var r = new PreviewRenderer(() => ValueTree.Empty);
        PreviewFrame? frame = null;
        var done = new ManualResetEventSlim();
        r.Rendered += f => { frame = f; done.Set(); };
        r.Request(model, view);
        Assert.True(done.Wait(10_000), "no frame arrived");
        return frame!;
    }

    /// <summary>Above zoom 1 the frame is the pane, rendered at the zoom: a 13 px label is drawn
    /// 4x in viewport coordinates, while the hit map stays the 1:1 resolve.</summary>
    [Fact]
    public void At_4x_The_Frame_Is_The_Viewport_And_Parts_Are_Drawn_At_Scale()
    {
        var model = Model(BaseImage());
        model.Edit("label", l => l.Components.Add(new TextDef { Id = "label", Rect = new Rect(100, 150, 60, 16), Text = PropertyValue.Literal("cpu"), Size = PropertyValue.Literal(13) }));
        // The canvas's bottom edge (y 200) lands at screen y 200, so the pane's last 100 rows are off it.
        var view = new Viewport(4, -300, -600, 400, 300);

        var frame = RenderOnce(model, view);

        Assert.Equal(400, frame.Width);
        Assert.Equal(300, frame.Height);
        Assert.Equal(400 * 300 * 4, frame.Bgra.Length);
        Assert.Equal(view, frame.View);
        Assert.Equal(new Rect(100, 150, 60, 16), frame.Resolved.Single(c => c.Id == "label").Rect);
        var drawn = Assert.IsType<ResolvedText>(frame.Drawn!.Single(c => c.Id == "label"));
        Assert.Equal(new Rect(100, 0, 240, 64), drawn.Rect);
        Assert.Equal(52f, drawn.Style.Size);
        Assert.Equal(255, frame.Bgra[((100 * 400) + 350) * 4 + 3]);    // the photograph, stretched
        Assert.Equal(0, frame.Bgra[((250 * 400) + 10) * 4 + 3]);       // off the canvas: clear
    }

    /// <summary>The measurement plan Task 3.1 asks for: the starter column layout on its 3440x1440
    /// canvas, over a real-sized photograph, at 8x on a 1600x1000 pane centred on dial-1's label.
    /// The warm figure is what a pan or an edit costs; the stop line is 100 ms.</summary>
    [Fact]
    public void RenderTime_At_8x_On_3440x1440_Is_Recorded()
    {
        var photo = Path.Combine(Paths.RuntimeDir, "preview-test-base-3440.png");
        if (!File.Exists(photo))
        {
            using var s = Surface.Create(3440, 1440);
            s.Clear(new Color(255, 60, 90, 120));
            s.FillRect(new Rect(1000, 200, 1400, 900), new Color(255, 200, 160, 90), 40);
            s.SavePng(photo);
        }
        var layout = LayoutFile.Load(Path.Combine(TestRepo.Root, "layouts", "column-system.json"));
        layout.BaseImage = photo;
        var model = new DesignerModel(layout, new DisplaySignature("TEST", 3440, 1440, 100), null);
        const double zoom = 8;
        var view = new Viewport(zoom, 800 - 3260 * zoom, 500 - 312 * zoom, 1600, 1000);

        RenderOnce(model, view);                                     // cold: base cache, fonts
        var times = Enumerable.Range(0, 9).Select(_ => RenderOnce(model, view).RenderTime.TotalMilliseconds).ToList();
        // The whole-canvas render beside it, for comparison: what zoom 1 and below costs.
        var whole = Enumerable.Range(0, 9).Select(_ => RenderOnce(model, null).RenderTime.TotalMilliseconds).ToList();

        var frame = RenderOnce(model, view);
        Assert.Equal(1600, frame.Width);
        Assert.Contains(frame.Drawn!, c => c.Id == "dial-1.label" && c.Rect.W == 80 * 8);
        var median = times.Order().ElementAt(times.Count / 2);
        _output.WriteLine($"8x render, 3440x1440 layout, 1600x1000 pane: median {median:F1} ms, all [{string.Join(", ", times.Select(t => t.ToString("F1")))}]");
        _output.WriteLine($"1:1 whole-canvas render, same layout: median {whole.Order().ElementAt(whole.Count / 2):F1} ms, all [{string.Join(", ", whole.Select(t => t.ToString("F1")))}]");
        Assert.True(median < 1000, $"8x render took {median} ms");   // a sanity bound; the report records the figure
    }

    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public PreviewRendererTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    /// <summary>The render expands before it resolves, as the daemon does: the hit map holds the
    /// copy's parts under their expanded ids, offset to the copy's origin. The shipped dial is used
    /// (not the clock) so no other test's user-dir widget can stand in for it.</summary>
    [Fact]
    public void A_Copy_Is_Expanded_Before_Resolve()
    {
        var model = Model("");
        model.Layout.Version = 2;
        model.Layout.Copies = [new WidgetCopy { Id = "dial-1", Widget = "dial", X = 200, Y = 100 }];

        var frame = RenderOnce(model);

        Assert.Empty(frame.Problems);
        var dial = frame.Resolved.Single(c => c.Id == "dial-1.dial");
        Assert.Equal(200, dial.Rect.X);
        Assert.Equal(100, dial.Rect.Y);
        Assert.Contains(frame.Resolved, c => c.Id == "clock");   // the loose components still draw
    }

    /// <summary>A copy whose widget file is gone is a problem in the frame, never an exception, and
    /// the rest of the layout still draws.</summary>
    [Fact]
    public void A_Missing_Widget_Is_A_Problem_Not_An_Exception()
    {
        var model = Model("");
        model.Layout.Version = 2;
        model.Layout.Copies = [new WidgetCopy { Id = "gone-1", Widget = "no-such-widget", X = 10, Y = 10 }];

        var frame = RenderOnce(model);

        var problem = Assert.Single(frame.Problems);
        Assert.Equal(ExpandProblemKind.MissingWidget, problem.Kind);
        Assert.Equal("gone-1", problem.CopyId);
        Assert.Equal(2, frame.Resolved.Count);
        Assert.Contains(frame.Bgra, b => b != 0);
    }
}
