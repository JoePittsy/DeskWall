using DeskWall.Core;
using DeskWall.Core.Display;
using DeskWall.Core.Layout;
using DeskWall.Core.Render;
using DeskWall.Core.Resolve;
using DeskWall.Core.Sources;
using DeskWall.Core.Tick;
using DeskWall.Core.Values;
using Xunit;

file sealed class BoundBaseClock(DateTimeOffset now) : IClock { public DateTimeOffset Now { get; set; } = now; }

/// <summary>A bound <c>baseImage</c>: one photo per <c>time.phase</c>, resolved each tick with
/// <c>runtime:</c> expanded, falling back to the last good base (and saying so once) when the
/// binding names a file that is not there.</summary>
public class TickRunnerBoundBaseTests
{
    private static readonly Color Night = new(255, 10, 20, 60), Day = new(255, 150, 190, 230);

    /// <summary>A scratch folder under the test runtime dir, so layouts can name it with runtime:.</summary>
    private static (string Dir, string Rel) Assets()
    {
        var rel = "assets/bound-base-" + Guid.NewGuid().ToString("N")[..8];
        var dir = Paths.ExpandRuntime("runtime:" + rel);
        Directory.CreateDirectory(dir);
        return (dir, rel);
    }

    private static void Png(string path, Color c)
    {
        using var b = Surface.Create(320, 180);
        b.Clear(c);
        b.SavePng(path);
    }

    private static LayoutFile PhaseLayout(string rel) => LayoutFile.Parse($$"""
        { "version": 1,
          "baseImage": { "bind": "time.phase | \"?night=runtime:{{rel}}/night.png,day=runtime:{{rel}}/day.png,dawn=runtime:{{rel}}/dawn.png\"" },
          "sources": [ { "name": "time", "type": "time" } ],
          "components": [ { "type": "text", "id": "label", "rect": [10, 10, 100, 30], "text": "hi" } ] }
        """);

    private static TickRunner Runner(LayoutFile layout, IClock clock, string dir) =>
        new(layout, layout.Sources.Select(s => SourceFactory.Create(s, clock)).ToList(), new SourceRegistry(), clock,
            new MonitorInfo(new DisplaySignature("TEST-BB", 320, 180, 100), new Rect(0, 0, 320, 180), true, "TEST-BB"),
            statePath: Path.Combine(dir, "state.json"), outPath: Path.Combine(dir, "out.jpg"), framePath: Path.Combine(dir, "frame.raw"));

    private static (byte, byte, byte, byte) Corner(string dir)
    {
        using var frame = Surface.LoadRaw(Path.Combine(dir, "frame.raw"));
        return frame.GetPixel(300, 170);
    }

    private static (byte, byte, byte, byte) Px(Color c) => (c.A, c.R, c.G, c.B);

    [Fact]
    public void Bound_BaseImage_Parses_Resolves_And_Round_Trips()
    {
        var layout = PhaseLayout("assets/x");
        Assert.True(layout.BaseImage.IsBound);
        var again = LayoutFile.Parse(layout.ToJson());
        Assert.True(again.BaseImage.IsBound);
        Assert.Equal(layout.BaseImage.Binding!.ToString(), again.BaseImage.Binding!.ToString());

        var night = new RecordValue(new Dictionary<string, Value> { ["time"] = new RecordValue(new Dictionary<string, Value> { ["phase"] = new TextValue("night") }) });
        Assert.Equal(Paths.InRuntime("assets", "x", "night.png"), LayoutResolver.BaseImagePath(layout, night));
        Assert.Equal("", LayoutResolver.BaseImagePath(layout, ValueTree.Empty));   // nothing bound yet: no base, not a crash

        // A literal still reads and writes as a plain string, and runtime: expands for it too.
        var literal = LayoutFile.Parse("""{ "baseImage": "runtime:assets/photo.jpg", "components": [] }""");
        Assert.False(literal.BaseImage.IsBound);
        Assert.Contains("\"baseImage\": \"runtime:assets/photo.jpg\"", literal.ToJson());
        Assert.Equal(Paths.InRuntime("assets", "photo.jpg"), LayoutResolver.BaseImagePath(literal, ValueTree.Empty));
    }

    [Fact]
    public async Task The_Base_Swaps_When_The_Phase_Moves()
    {
        var (assets, rel) = Assets();
        Png(Path.Combine(assets, "night.png"), Night);
        Png(Path.Combine(assets, "day.png"), Day);
        var dir = Path.Combine(TestRun.Root, "tick-bound-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var clock = new BoundBaseClock(DateTimeOffset.Now.Date.AddHours(2));
        var runner = Runner(PhaseLayout(rel), clock, dir);

        var t1 = await runner.RunAsync(force: true, apply: false, default);
        Assert.Null(t1.Warning);
        Assert.Equal(Px(Night), Corner(dir));

        clock.Now = clock.Now.AddSeconds(10);   // same minute, same phase: the base key holds, so it skips
        Assert.True((await runner.RunAsync(force: false, apply: false, default)).Skipped);

        clock.Now = clock.Now.Date.AddHours(12);   // noon: day. Not forced - the base key alone must move the gate
        var t3 = await runner.RunAsync(force: false, apply: false, default);
        Assert.False(t3.Skipped);
        Assert.Null(t3.Warning);
        Assert.Equal(Px(Day), Corner(dir));
    }

    [Fact]
    public async Task A_Missing_Photo_Keeps_The_Last_Good_Base_And_Warns_Once()
    {
        var (assets, rel) = Assets();
        Png(Path.Combine(assets, "night.png"), Night);   // no dawn.png: the binding will name a file that is not there
        var dir = Path.Combine(TestRun.Root, "tick-bound-miss-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var clock = new BoundBaseClock(DateTimeOffset.Now.Date.AddHours(2));
        var runner = Runner(PhaseLayout(rel), clock, dir);
        await runner.RunAsync(force: true, apply: false, default);

        clock.Now = clock.Now.Date.AddHours(6);   // 06:00 is dawn
        Assert.Equal("dawn", TimeSource.Phase(0.25));
        var t2 = await runner.RunAsync(force: false, apply: false, default);
        Assert.NotNull(t2.Warning);
        Assert.Contains("dawn.png", t2.Warning);
        Assert.Equal(Px(Night), Corner(dir));   // the last good base, still

        clock.Now = clock.Now.AddMinutes(1);
        var t3 = await runner.RunAsync(force: false, apply: false, default);
        Assert.Null(t3.Warning);                  // once, not every tick
        clock.Now = clock.Now.AddMinutes(1);
        Assert.Null((await runner.RunAsync(force: true, apply: false, default)).Warning);

        // The file turns up: it is used, and the debt is cleared.
        Png(Path.Combine(assets, "dawn.png"), Day);
        clock.Now = clock.Now.AddMinutes(1);
        var t5 = await runner.RunAsync(force: false, apply: false, default);
        Assert.Null(t5.Warning);
        Assert.Equal(Px(Day), Corner(dir));
        Assert.Equal("", FrameState.Load(Path.Combine(dir, "state.json")).BaseMissing);
    }

    [Fact]
    public async Task With_No_Previous_Base_And_No_Photos_A_Fresh_Home_Draws_A_Solid_Base()
    {
        var (assets, rel) = Assets();   // no files at all: a fresh home before the photos are copied in
        var dir = Path.Combine(TestRun.Root, "tick-bound-none-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var clock = new BoundBaseClock(DateTimeOffset.Now.Date.AddHours(2));
        var runner = Runner(PhaseLayout(rel), clock, dir);

        var t1 = await runner.RunAsync(force: true, apply: false, default);
        Assert.NotNull(t1.Warning);
        Assert.Contains("night.png", t1.Warning);
        Assert.Contains("solid", t1.Warning);
        Assert.Equal(Px(BaseCache.SolidColor), Corner(dir));
        Assert.True(File.Exists(Path.Combine(dir, "out.jpg")));

        clock.Now = clock.Now.AddMinutes(1);
        Assert.Null((await runner.RunAsync(force: false, apply: false, default)).Warning);   // once, not every tick

        // The photo turns up: the base key moves, so an unforced tick renders onto it.
        Png(Path.Combine(assets, "night.png"), Night);
        clock.Now = clock.Now.AddMinutes(1);
        var t3 = await runner.RunAsync(force: false, apply: false, default);
        Assert.False(t3.Skipped);
        Assert.Null(t3.Warning);
        Assert.Equal(Px(Night), Corner(dir));
        Assert.Equal("", FrameState.Load(Path.Combine(dir, "state.json")).BaseMissing);
    }

    [Fact]
    public async Task With_No_Previous_Base_A_Missing_Photo_Falls_Back_To_Another_Photo_In_The_Map()
    {
        var (assets, rel) = Assets();
        Png(Path.Combine(assets, "day.png"), Day);   // only the day photo: night (02:00) is missing
        var dir = Path.Combine(TestRun.Root, "tick-bound-other-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var runner = Runner(PhaseLayout(rel), new BoundBaseClock(DateTimeOffset.Now.Date.AddHours(2)), dir);

        var t1 = await runner.RunAsync(force: true, apply: false, default);
        Assert.NotNull(t1.Warning);
        Assert.Contains("night.png", t1.Warning);
        Assert.Contains("day.png", t1.Warning);
        Assert.Equal(Px(Day), Corner(dir));
    }

    [Fact]
    public void Base_Image_Alternatives_Are_The_Map_Values_Expanded()
    {
        Assert.Equal(
            [Paths.InRuntime("assets", "x", "night.png"), Paths.InRuntime("assets", "x", "day.png"), Paths.InRuntime("assets", "x", "dawn.png")],
            LayoutResolver.BaseImageAlternatives(PhaseLayout("assets/x")));
        var literal = LayoutFile.Parse("""{ "baseImage": "runtime:assets/photo.jpg", "components": [] }""");
        Assert.Empty(LayoutResolver.BaseImageAlternatives(literal));
    }
}
