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
}
