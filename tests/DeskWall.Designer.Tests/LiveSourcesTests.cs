using System.IO;
using System.Linq;
using DeskWall.Core.Events;
using DeskWall.Core.Layout;
using DeskWall.Core.Sources;
using DeskWall.Designer.Model;
using Xunit;

file sealed class FixedClock(DateTimeOffset now) : IClock { public DateTimeOffset Now => now; }

/// <summary>A source that holds a resource, the shape `hardware` now has (a timer and NVML).</summary>
file sealed class DisposableSource() : PeriodicSource("d", TimeSpan.FromMinutes(1)), IDisposable
{
    public int Disposals;
    public override ValueTask<DeskWall.Core.Values.RecordValue> RefreshAsync(CancellationToken ct)
        => new(new DeskWall.Core.Values.RecordValue(new Dictionary<string, DeskWall.Core.Values.Value>()));
    public void Dispose() => Disposals++;
}

/// <summary>A source that knows when it has something new: a watched file, a streaming command, a
/// volume callback. Due only once it has said so, which is how a pushed source earns its refresh.</summary>
file sealed class SignallingSource : ISource, ISignalSource
{
    private volatile bool _pending;

    public bool HasPending => _pending;
    public readonly ManualResetEventSlim Refreshed = new(false);
    public int Refreshes;

    public string Name => "pushy";
    public event Action<ISource>? Changed;

    public void Fire()
    {
        _pending = true;
        Changed?.Invoke(this);
    }

    public DateTimeOffset NextDue(DateTimeOffset? lastRefresh, DateTimeOffset now)
        => _pending ? now : now.AddMinutes(10);

    public ValueTask<DeskWall.Core.Values.RecordValue> RefreshAsync(CancellationToken ct)
    {
        _pending = false;
        Interlocked.Increment(ref Refreshes);
        Refreshed.Set();
        return new(new DeskWall.Core.Values.RecordValue(new Dictionary<string, DeskWall.Core.Values.Value>()));
    }
}

public class LiveSourcesTests
{
    private static Secrets NoSecrets() => new(Path.Combine(Path.GetTempPath(), "deskwall-tests", "home-designer", "no-such-secrets.json"));

    [Fact]
    public async Task Tree_Merges_Snapshots_From_Multiple_Sources()
    {
        var defs = new[]
        {
            new SourceDef { Name = "time", Type = "time" },
            new SourceDef { Name = "disks", Type = "disks" },
        };
        using var live = new LiveSources(defs, NoSecrets(), new FixedClock(DateTimeOffset.UtcNow));
        await live.RefreshNowAsync("time");
        await live.RefreshNowAsync("disks");

        var tree = live.Tree();
        Assert.NotNull(tree.Get("time"));
        Assert.NotNull(tree.Get("disks"));
    }

    [Fact]
    public async Task Updated_Fires_After_RefreshNowAsync()
    {
        var defs = new[] { new SourceDef { Name = "time", Type = "time" } };
        using var live = new LiveSources(defs, NoSecrets(), new FixedClock(DateTimeOffset.UtcNow));
        var n = 0;
        live.Updated += () => Interlocked.Increment(ref n);

        await live.RefreshNowAsync("time");

        Assert.True(n >= 1);
    }

    [Fact]
    public async Task Failing_Source_Keeps_Last_Values_And_Exposes_LastError()
    {
        var dir = Path.Combine(Path.GetTempPath(), "deskwall-tests", "livesources-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, "t.exe");
        File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), exe);

        var def = new SourceDef
        {
            Name = "cmd",
            Type = "command",
            Settings = new Dictionary<string, string> { ["command"] = exe, ["args"] = "/c echo hello" },
        };
        using var live = new LiveSources([def], NoSecrets(), new FixedClock(DateTimeOffset.UtcNow));

        await live.RefreshNowAsync("cmd");
        var first = live.Snapshots.Single(s => s.Name == "cmd");
        Assert.NotNull(first.Values);
        Assert.Null(first.LastError);

        File.Delete(exe);
        await live.RefreshNowAsync("cmd");
        var second = live.Snapshots.Single(s => s.Name == "cmd");
        Assert.NotNull(second.Values);   // last good values are kept
        Assert.NotNull(second.LastError);
        Assert.Equal(1, second.ConsecutiveFailures);
    }

    [Fact]
    public void Snapshots_Reflects_A_Source_That_Fails_To_Construct()
    {
        // http requires settings.url; omitting it throws inside SourceFactory.Create, which
        // LiveSources must record as a failed snapshot rather than let the exception escape.
        var def = new SourceDef { Name = "bad-http", Type = "http" };
        using var live = new LiveSources([def], NoSecrets(), new FixedClock(DateTimeOffset.UtcNow));

        var snap = live.Snapshots.Single();
        Assert.Equal("bad-http", snap.Name);
        Assert.NotNull(snap.LastError);
    }

    /// <summary>The designer builds a new LiveSources on every edit to the Sources list, so a source
    /// that holds a timer or a native library (a `hardware` source holds both) leaks one per edit
    /// unless Dispose lets go of it.</summary>
    [Fact]
    public void Dispose_Disposes_Sources_That_Hold_Resources()
    {
        var fake = new DisposableSource();
        var live = new LiveSources([new SourceDef { Name = "d", Type = "time" }], NoSecrets(),
            new FixedClock(DateTimeOffset.UtcNow), _ => fake);

        live.Dispose();
        live.Dispose();   // idempotent

        Assert.Equal(1, fake.Disposals);
    }

    /// <summary>The designer routes a signalling source through its own bus exactly as the daemon
    /// does: Changed signals, the bus coalesces, one wake refreshes whatever is due. Before this the
    /// designer had a second, bespoke path for AsyncSource alone.</summary>
    [Fact]
    public void A_Signalling_Source_Is_Refreshed_When_The_Bus_Wakes()
    {
        var clock = new FixedClock(DateTimeOffset.UtcNow);
        using var bus = new EventBus(clock, TimeSpan.FromMilliseconds(400), autoWake: false);
        var fake = new SignallingSource();
        using var live = new LiveSources([new SourceDef { Name = "pushy", Type = "time" }], NoSecrets(), clock, _ => fake, bus);
        Assert.Equal(0, fake.Refreshes);   // not due until it says so

        fake.Fire();
        Assert.True(bus.PumpWake(clock.Now.AddMilliseconds(400)));

        Assert.True(fake.Refreshed.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, fake.Refreshes);
    }

    /// <summary>The designer rebuilds LiveSources on every edit to the Sources list. A handler left
    /// on the bus would keep the whole discarded set alive and refresh it behind the new one.</summary>
    [Fact]
    public void Dispose_Detaches_From_The_Bus()
    {
        var clock = new FixedClock(DateTimeOffset.UtcNow);
        using var bus = new EventBus(clock, TimeSpan.FromMilliseconds(400), autoWake: false);
        var fake = new SignallingSource();
        var live = new LiveSources([new SourceDef { Name = "pushy", Type = "time" }], NoSecrets(), clock, _ => fake, bus);
        live.Dispose();

        fake.Fire();
        bus.PumpWake(clock.Now.AddMilliseconds(400));

        Assert.False(fake.Refreshed.Wait(TimeSpan.FromMilliseconds(300)));
        Assert.Equal(0, fake.Refreshes);
    }
}
