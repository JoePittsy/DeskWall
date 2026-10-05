using System.Diagnostics;
using DeskWall.Core;
using DeskWall.Core.Display;
using DeskWall.Core.Layout;
using DeskWall.Core.Render;
using DeskWall.Core.Scheduling;
using DeskWall.Core.Sources;
using DeskWall.Core.Tick;
using DeskWall.Core.Values;
using Xunit;

/// <summary>An async source whose fetches finish only when the test says so: Open() lets the
/// current one land with the next number, Fail() lets it land with an error.</summary>
internal sealed class GatedAsyncSource(string name, TimeSpan timeout) : AsyncSource(name, TimeSpan.FromMinutes(1), timeout)
{
    private readonly SemaphoreSlim _gate = new(0);
    private readonly System.Collections.Concurrent.ConcurrentQueue<bool> _outcomes = new();
    public int Runs;

    public void Open() { _outcomes.Enqueue(true); _gate.Release(); }
    public void Fail() { _outcomes.Enqueue(false); _gate.Release(); }

    protected override async Task<RecordValue> FetchAsync(CancellationToken ct)
    {
        var n = Interlocked.Increment(ref Runs);
        await _gate.WaitAsync(ct);
        _outcomes.TryDequeue(out var ok);
        if (!ok) throw new HttpRequestException("connection refused");
        return ValueTree.Of(("n", new NumberValue(n)));
    }
}

file sealed class DeferClock(DateTimeOffset now) : IClock { public DateTimeOffset Now { get; set; } = now; }

/// <summary>#20: a dead `http` endpoint held every tick that refreshed it for ~2 s (Windows retries a
/// refused local connect), because RefreshAsync awaited the fetch on the tick. After a source's first
/// refresh, a fetch that has not finished is left to run: the caller is told "pending" at once, the
/// previous values keep publishing, and the landing's Changed wake is the tick that harvests it.</summary>
public class AsyncSourceDeferralTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 4, 9, 0, 0, TimeSpan.Zero);

    private static async Task<GatedAsyncSource> Warmed(TaskCompletionSource landed)
    {
        var s = new GatedAsyncSource("slow", TimeSpan.FromSeconds(10));
        s.Changed += _ => landed.TrySetResult();
        var first = s.RefreshAsync(default);
        s.Open();
        Assert.Equal(1, ((NumberValue)(await first).Get("n")!).Number);
        return s;
    }

    [Fact]
    public async Task After_The_First_Refresh_A_Slow_Fetch_Returns_Pending_At_Once_And_Is_Harvested_When_It_Lands()
    {
        var landed = new TaskCompletionSource();
        var s = await Warmed(landed);

        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAsync<SourcePendingException>(async () => await s.RefreshAsync(default));
        Assert.True(sw.ElapsedMilliseconds < 1000, $"refresh held the caller {sw.ElapsedMilliseconds} ms");
        Assert.True(s.Fetching);

        // Asked again while it is still running: still pending, and no second fetch.
        await Assert.ThrowsAsync<SourcePendingException>(async () => await s.RefreshAsync(default));

        s.Open();
        await landed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(s.Fetching);
        Assert.True(s.HasPending);
        Assert.Equal(2, ((NumberValue)(await s.RefreshAsync(default)).Get("n")!).Number);
        Assert.False(s.HasPending);
        Assert.Equal(2, s.Runs);
    }

    [Fact]
    public async Task A_Deferred_Fetch_That_Fails_Is_Reported_By_The_Refresh_That_Harvests_It()
    {
        var landed = new TaskCompletionSource();
        var s = await Warmed(landed);
        await Assert.ThrowsAsync<SourcePendingException>(async () => await s.RefreshAsync(default));
        s.Fail();
        await landed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<HttpRequestException>(async () => await s.RefreshAsync(default));
    }

    /// <summary>In flight is not due: Changed is what says it landed. Otherwise the wake maths
    /// would answer "now" (a healthy source's LastRefresh is a whole interval ago) and pin the daemon
    /// at MinDelay for as long as the fetch runs. Once landed it is due at once, failing or not.</summary>
    [Fact]
    public async Task A_Source_With_A_Fetch_In_Flight_Is_Not_Due_Until_It_Lands()
    {
        var landed = new TaskCompletionSource();
        var s = await Warmed(landed);
        var reg = new SourceRegistry();
        reg.Set(SourceSnapshot.Initial("slow").Succeeded(ValueTree.Empty, T0).Failed("down", T0.AddMinutes(1)));
        var now = T0.AddMinutes(5);
        Assert.True(Scheduler.IsDue(s, reg.Get("slow"), now));

        await Assert.ThrowsAsync<SourcePendingException>(async () => await s.RefreshAsync(default));
        Assert.False(Scheduler.IsDue(s, reg.Get("slow"), now));
        Assert.Equal(now + Scheduler.MaxDelay, new Scheduler([s], reg).NextWake(now));

        s.Open();
        await landed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(Scheduler.IsDue(s, reg.Get("slow"), now));
    }

    /// <summary>The tick end to end: a due source whose fetch has not landed costs the tick nothing,
    /// and its snapshot is left exactly as it was - previous values still published, LastRefresh
    /// (what staleness is judged from) and the failure count carried, not reset by a non-answer.</summary>
    [Fact]
    public async Task A_Tick_Does_Not_Wait_For_A_Pending_Source_And_Leaves_Its_Snapshot_Alone()
    {
        var dir = Path.Combine(Path.GetTempPath(), "deskwall-tests", "tick-defer-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var basePng = Path.Combine(dir, "base.png");
            using (var b = Surface.Create(320, 180)) { b.Clear(new Color(255, 30, 30, 30)); b.SavePng(basePng); }
            var layout = LayoutFile.Parse($$"""
            { "version": 1, "baseImage": {{System.Text.Json.JsonSerializer.Serialize(basePng)}}, "sources": [],
              "components": [ { "type": "text", "id": "n", "rect": [10, 10, 200, 60], "text": { "bind": "slow.n" }, "size": 40 } ] }
            """);
            var clock = new DeferClock(T0);
            var landed = new TaskCompletionSource();
            var s = await Warmed(landed);
            var registry = new SourceRegistry();
            var monitor = new MonitorInfo(new DisplaySignature("TEST-DEFER", 320, 180, 100), new Rect(0, 0, 320, 180), true, "TEST-DEFER");
            var runner = new TickRunner(layout, [s], registry, clock, monitor,
                statePath: Path.Combine(dir, "state.json"), outPath: Path.Combine(dir, "out.jpg"), framePath: Path.Combine(dir, "frame.raw"));

            var before = SourceSnapshot.Initial("slow").Succeeded(ValueTree.Of(("n", new NumberValue(1))), T0).Failed("down", T0.AddMinutes(1));
            registry.Set(before);
            clock.Now = T0.AddMinutes(3);   // due again on its back-off
            Assert.True(Scheduler.IsDue(s, before, clock.Now));

            var sw = Stopwatch.StartNew();
            var t = await runner.RunAsync(force: false, apply: false, default);
            Assert.True(sw.ElapsedMilliseconds < 1000, $"tick held {sw.ElapsedMilliseconds} ms");
            Assert.Same(before, registry.Get("slow"));
            Assert.Equal(1, t.Redrawn);   // the previous value, painted

            s.Open();
            await landed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            clock.Now = clock.Now.AddSeconds(1);
            var harvest = await runner.RunAsync(force: false, apply: false, default);
            Assert.Equal(1, harvest.Redrawn);
            Assert.Equal(2, ((NumberValue)registry.Get("slow").Values!.Get("n")!).Number);
            Assert.Equal(clock.Now, registry.Get("slow").LastRefresh);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch (IOException) { } }
    }
}
