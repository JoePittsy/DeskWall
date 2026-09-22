using DeskWall.Core.Events;
using DeskWall.Core.Sources;
using Xunit;

file sealed class FixedClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset Now { get; set; } = now;
}

/// <summary>Signal is what every in-process producer uses once the schedule-shaped sources keep
/// their own path: a watched file, a landed image, an async fetch, a volume callback.</summary>

public class EventBusSignalTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 22, 9, 0, 0, TimeSpan.Zero);

    private static EventBus Bus(IClock clock) => new(clock, TimeSpan.FromMilliseconds(400), autoWake: false);

    [Fact]
    public void A_Signal_Wakes_After_The_Coalescing_Window()
    {
        var clock = new FixedClock(T0);
        using var bus = Bus(clock);
        var wakes = 0;
        bus.WakeRequested += () => wakes++;

        bus.Signal("file1");
        Assert.False(bus.PumpWake(T0.AddMilliseconds(399)));
        Assert.True(bus.PumpWake(T0.AddMilliseconds(400)));
        Assert.Equal(1, wakes);
        Assert.False(bus.PumpWake(T0.AddMilliseconds(401)));
    }

    [Fact]
    public void Several_Signals_Inside_One_Window_Cost_One_Wake()
    {
        var clock = new FixedClock(T0);
        using var bus = Bus(clock);
        var wakes = 0;
        bus.WakeRequested += () => wakes++;

        for (var i = 0; i < 20; i++) bus.Signal("image" + i);
        Assert.True(bus.PumpWake(T0.AddMilliseconds(400)));
        Assert.Equal(1, wakes);
    }

    [Fact]
    public void A_Signal_And_An_Event_Share_The_Same_Wake()
    {
        var clock = new FixedClock(T0);
        using var bus = Bus(clock);
        var wakes = 0;
        bus.WakeRequested += () => wakes++;

        bus.Signal("file1");
        Assert.True(bus.Publish("""{"source":"build","data":{"status":"green"}}"""));
        Assert.True(bus.PumpWake(T0.AddMilliseconds(400)));
        Assert.Equal(1, wakes);
    }

    [Fact]
    public void A_Signal_Creates_No_Provider_Because_The_Source_Publishes_Its_Own_Values()
    {
        var clock = new FixedClock(T0);
        using var bus = Bus(clock);
        bus.Signal("file1");
        Assert.Empty(bus.Providers);
    }

    /// <summary>The ring exists to explain a producer's pipe events. Signals are frequent and
    /// internal, and fifty of them would push every real entry out of it.</summary>
    [Fact]
    public void A_Signal_Does_Not_Fill_The_Diagnostics_Ring()
    {
        var clock = new FixedClock(T0);
        using var bus = Bus(clock);
        bus.Publish("""{"source":"build","data":{"status":"green"}}""");
        for (var i = 0; i < 60; i++) bus.Signal("noisy");

        Assert.Single(bus.Recent);
        Assert.Equal("build", bus.Recent[0].Source);
    }

    [Fact]
    public void A_Blank_Source_Name_Is_A_Programming_Error_Not_A_Silent_Wake()
        => Assert.Throws<ArgumentException>(() => Bus(new FixedClock(T0)).Signal("  "));
}
