using DeskWall.Core;
using DeskWall.Core.Display;
using DeskWall.Core.Layout;
using DeskWall.Core.Render;
using DeskWall.Core.Sources;
using DeskWall.Core.Tick;
using Xunit;

file sealed class TickFakeClock(DateTimeOffset now) : IClock { public DateTimeOffset Now { get; set; } = now; }

public class TickRunnerTests
{
    [Fact]
    public async Task Second_Tick_Without_Changes_Is_Skipped_And_Clock_Change_Redraws()
    {
        var dir = Path.Combine(Path.GetTempPath(), "deskwall-tests", "tick-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var basePng = Path.Combine(dir, "tickbase.png");
        using (var b = Surface.Create(320, 180)) { b.Clear(new Color(255, 30, 30, 30)); b.SavePng(basePng); }
        var layout = LayoutFile.Parse($$"""
        { "version": 1, "baseImage": {{System.Text.Json.JsonSerializer.Serialize(basePng)}}, "sources": [ { "name": "time", "type": "time" } ],
          "components": [ { "type": "text", "id": "clock", "rect": [10, 10, 200, 60], "text": { "bind": "time.now | HH:mm" }, "size": 40 } ] }
        """);
        var clock = new TickFakeClock(new DateTimeOffset(2026, 9, 20, 14, 32, 5, TimeSpan.Zero));
        var registry = new SourceRegistry();
        var sources = layout.Sources.Select(s => SourceFactory.Create(s, clock)).ToList();
        var monitor = new MonitorInfo(new DisplaySignature("TEST", 320, 180, 100), new Rect(0, 0, 320, 180), true, "TEST");
        var runner = new TickRunner(layout, sources, registry, clock, monitor,
            statePath: Path.Combine(dir, "state.json"), outPath: Path.Combine(dir, "out.jpg"), framePath: Path.Combine(dir, "frame.raw"));

        var t1 = await runner.RunAsync(force: true, apply: false, default);
        Assert.False(t1.Skipped);
        Assert.Equal(1, t1.Redrawn);
        Assert.True(File.Exists(Path.Combine(dir, "out.jpg")));
        // Finding 3: CPU time now comes from Environment.CpuUsage, which opens no process handle.
        Assert.True(t1.CpuMs >= 0);

        clock.Now = clock.Now.AddSeconds(10);            // same minute: time source not due
        var t2 = await runner.RunAsync(force: false, apply: false, default);
        Assert.True(t2.Skipped);
        Assert.Equal(0, t2.Redrawn);
        Assert.Equal(0, t2.EncodeMs);

        clock.Now = clock.Now.AddMinutes(1);              // next minute: due, key changes
        var t3 = await runner.RunAsync(force: false, apply: false, default);
        Assert.False(t3.Skipped);
        Assert.Equal(1, t3.Redrawn);   // incremental path: only the clock was redrawn
        Assert.Contains("resolve", t3.ToTable());
        using (var frame = Surface.LoadRaw(Path.Combine(dir, "frame.raw")))
        {
            Assert.Equal(((byte)255, (byte)30, (byte)30, (byte)30), frame.GetPixel(300, 170));   // base untouched away from the clock
        }
    }

    /// <summary>Finding 12: the skip gate used to check only the component keys and the display
    /// signature, so replacing the base image file in place (same path, same components) was
    /// skipped forever.</summary>
    [Fact]
    public async Task Replacing_The_Base_Image_In_Place_Is_Not_Skipped()
    {
        var dir = Path.Combine(Path.GetTempPath(), "deskwall-tests", "tick-basekey-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var basePng = Path.Combine(dir, "base.png");
        using (var b = Surface.Create(320, 180)) { b.Clear(new Color(255, 10, 10, 10)); b.SavePng(basePng); }
        var layout = LayoutFile.Parse($$"""
        { "version": 1, "baseImage": {{System.Text.Json.JsonSerializer.Serialize(basePng)}}, "sources": [],
          "components": [ { "type": "text", "id": "static", "rect": [10, 10, 100, 30], "text": "hi" } ] }
        """);
        var clock = new TickFakeClock(new DateTimeOffset(2026, 9, 20, 14, 32, 5, TimeSpan.Zero));
        var registry = new SourceRegistry();
        var monitor = new MonitorInfo(new DisplaySignature("TEST-BK", 320, 180, 100), new Rect(0, 0, 320, 180), true, "TEST-BK");
        var runner = new TickRunner(layout, [], registry, clock, monitor,
            statePath: Path.Combine(dir, "state.json"), outPath: Path.Combine(dir, "out.jpg"), framePath: Path.Combine(dir, "frame.raw"));

        var t1 = await runner.RunAsync(force: true, apply: false, default);
        Assert.False(t1.Skipped);

        var t2 = await runner.RunAsync(force: false, apply: false, default);
        Assert.True(t2.Skipped);   // nothing changed: base untouched, no component change

        // Replace the base image's contents in place (same path). The mtime must move for the
        // key to change; force it forward explicitly rather than relying on clock resolution.
        using (var b = Surface.Create(320, 180)) { b.Clear(new Color(255, 200, 0, 0)); b.SavePng(basePng); }
        File.SetLastWriteTimeUtc(basePng, File.GetLastWriteTimeUtc(basePng).AddSeconds(5));

        var t3 = await runner.RunAsync(force: false, apply: false, default);
        Assert.False(t3.Skipped);
        using (var frame = Surface.LoadRaw(Path.Combine(dir, "frame.raw")))
        {
            Assert.Equal(((byte)255, (byte)200, (byte)0, (byte)0), frame.GetPixel(300, 170));   // new base visible away from the text
        }
    }

    /// <summary>
    /// Finding 7: RenderIncremental can throw ("previous frame size mismatch") after TickRunner has
    /// already loaded the previous frame surface with Surface.LoadRaw; that surface used to leak.
    /// </summary>
    [Fact]
    public async Task Incremental_Path_Disposes_The_Previous_Frame_When_RenderIncremental_Throws()
    {
        var dir = Path.Combine(Path.GetTempPath(), "deskwall-tests", "tick-leak-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var basePng = Path.Combine(dir, "leakbase.png");
        using (var b = Surface.Create(320, 180)) { b.Clear(new Color(255, 5, 5, 5)); b.SavePng(basePng); }
        var layout = LayoutFile.Parse($$"""
        { "version": 1, "baseImage": {{System.Text.Json.JsonSerializer.Serialize(basePng)}}, "sources": [ { "name": "time", "type": "time" } ],
          "components": [ { "type": "text", "id": "clock", "rect": [10, 10, 200, 60], "text": { "bind": "time.now | HH:mm" }, "size": 40 } ] }
        """);
        var clock = new TickFakeClock(new DateTimeOffset(2026, 9, 20, 14, 32, 5, TimeSpan.Zero));
        var registry = new SourceRegistry();
        var sources = layout.Sources.Select(s => SourceFactory.Create(s, clock)).ToList();
        var monitor = new MonitorInfo(new DisplaySignature("TEST-LEAK", 320, 180, 100), new Rect(0, 0, 320, 180), true, "TEST-LEAK");
        var framePath = Path.Combine(dir, "frame.raw");
        var runner = new TickRunner(layout, sources, registry, clock, monitor,
            statePath: Path.Combine(dir, "state.json"), outPath: Path.Combine(dir, "out.jpg"), framePath: framePath);

        await runner.RunAsync(force: true, apply: false, default);   // establishes state.json + frame.raw at 320x180

        // Corrupt frame.raw to a different size while the recorded signature/base key stay
        // matching, so the next non-forced tick takes the incremental path and RenderIncremental
        // throws "previous frame size mismatch" only after Surface.LoadRaw has already succeeded.
        using (var wrongSize = Surface.Create(10, 10)) { wrongSize.Clear(new Color(255, 9, 9, 9)); wrongSize.SaveRaw(framePath); }

        clock.Now = clock.Now.AddMinutes(1);   // the clock text changes: not a skip
        var before = Surface.LiveCount;
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(force: false, apply: false, default));
        Assert.Equal(before, Surface.LiveCount);   // the previous-frame surface LoadRaw produced must not leak
    }
}
