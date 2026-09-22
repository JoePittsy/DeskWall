using DeskWall.Core.Events;
using DeskWall.Core.Sources;
using DeskWall.Core.Values;
using Xunit;

file sealed class FixedClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset Now { get; set; } = now;
}

/// <summary>A source that knows when it changed and nothing else. Stands in for a watched file, a
/// streaming command or a volume callback, none of which the test needs to own.</summary>
file sealed class FakeSignalSource(string name) : ISource, ISignalSource
{
    public string Name => name;
    public event Action<ISource>? Changed;
    public void Fire() => Changed?.Invoke(this);
    public DateTimeOffset NextDue(DateTimeOffset? lastRefresh, DateTimeOffset now) => now;
    public ValueTask<RecordValue> RefreshAsync(CancellationToken ct) => new(new RecordValue(new Dictionary<string, Value>()));
}

/// <summary>A source that is only ever asked.</summary>
file sealed class FakePullSource(string name) : ISource
{
    public string Name => name;
    public DateTimeOffset NextDue(DateTimeOffset? lastRefresh, DateTimeOffset now) => now;
    public ValueTask<RecordValue> RefreshAsync(CancellationToken ct) => new(new RecordValue(new Dictionary<string, Value>()));
}

/// <summary>One rule for every in-process producer: it raises Changed, the bus is signalled by
/// name, and the host wakes once for the lot. Both hosts (the daemon and the designer's
/// LiveSources) attach the same handler, so the rule has one implementation and one test.</summary>
public class SourceSignalsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 22, 9, 0, 0, TimeSpan.Zero);

    private static EventBus Bus(IClock clock) => new(clock, TimeSpan.FromMilliseconds(400), autoWake: false);

    [Fact]
    public void A_Connected_Source_That_Changes_Wakes_The_Host()
    {
        using var bus = Bus(new FixedClock(T0));
        var wakes = 0;
        bus.WakeRequested += () => wakes++;
        var src = new FakeSignalSource("watched");
        SourceSignals.ConnectAll(bus, [src]);

        src.Fire();
        Assert.True(bus.PumpWake(T0.AddMilliseconds(400)));
        Assert.Equal(1, wakes);
    }

    [Fact]
    public void Two_Sources_Changing_Inside_One_Window_Cost_One_Wake()
    {
        using var bus = Bus(new FixedClock(T0));
        var wakes = 0;
        bus.WakeRequested += () => wakes++;
        var a = new FakeSignalSource("a");
        var b = new FakeSignalSource("b");
        SourceSignals.ConnectAll(bus, [a, b]);

        a.Fire();
        b.Fire();
        Assert.True(bus.PumpWake(T0.AddMilliseconds(400)));
        Assert.Equal(1, wakes);
        Assert.False(bus.PumpWake(T0.AddMilliseconds(900)));
    }

    /// <summary>Signal carries the name and nothing else, and the bus keeps no record of it, so the
    /// only observable proof that the handler passes the source's own name rather than a constant
    /// is the bus's own contract: a blank name is a programming error, not a silent wake.</summary>
    [Fact]
    public void The_Handler_Passes_The_Source_Its_Own_Name()
    {
        using var bus = Bus(new FixedClock(T0));
        var blank = new FakeSignalSource("   ");
        SourceSignals.ConnectAll(bus, [blank]);

        Assert.Throws<ArgumentException>(blank.Fire);
    }

    [Fact]
    public void A_Pull_Source_In_The_Set_Is_Left_Alone()
    {
        using var bus = Bus(new FixedClock(T0));
        var wakes = 0;
        bus.WakeRequested += () => wakes++;
        SourceSignals.ConnectAll(bus, [new FakePullSource("disks"), new FakeSignalSource("watched")]);

        Assert.False(bus.PumpWake(T0.AddMilliseconds(400)));
        Assert.Equal(0, wakes);
    }

    /// <summary>The designer rebuilds its whole source set on every edit, so the handler it attached
    /// has to come back off or each edit leaks a set of sources through the bus.</summary>
    [Fact]
    public void The_Handler_Can_Be_Detached()
    {
        using var bus = Bus(new FixedClock(T0));
        var src = new FakeSignalSource("watched");
        var handler = SourceSignals.ConnectAll(bus, [src]);
        src.Changed -= handler;

        src.Fire();
        Assert.False(bus.PumpWake(T0.AddMilliseconds(400)));
    }

    /// <summary>AsyncSource's old Completed event is this event. One name for one thing.</summary>
    [Fact]
    public void AsyncSource_Is_A_Signalling_Source()
        => Assert.True(typeof(ISignalSource).IsAssignableFrom(typeof(AsyncSource)));
}
