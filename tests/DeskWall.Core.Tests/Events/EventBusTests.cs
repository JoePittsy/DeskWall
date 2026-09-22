using DeskWall.Core.Events;
using DeskWall.Core.Sources;
using DeskWall.Core.Values;
using Xunit;

namespace DeskWall.Core.Tests.Events;

// Not file-local: it appears in the signature of this class's own helper, which the compiler
// forbids for a file-local type.
internal sealed class BusClock(DateTimeOffset now) : IClock { public DateTimeOffset Now { get; set; } = now; }

/// <summary>autoWake is false throughout and the tests drive <see cref="EventBus.PumpWake"/>: a
/// coalescing window verified by sleeping would be both slow and flaky.</summary>
public class EventBusTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 21, 20, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Window = TimeSpan.FromMilliseconds(400);

    private static (EventBus bus, BusClock clock) New()
    {
        var clock = new BusClock(T0);
        return (new EventBus(clock, Window, autoWake: false), clock);
    }

    /// <summary>An event carrying one number. Built by substitution rather than interpolation:
    /// a raw interpolated string would need six consecutive braces to be told apart from the
    /// payload's own, which nobody can read.</summary>
    private static string Numbered(int i)
        => """{"source":"a","data":{"n":N}}""".Replace("N", i.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);

    [Fact]
    public void An_Accepted_Event_Lands_In_Providers_And_The_Ring()
    {
        var (bus, _) = New();
        using var _d = bus;

        Assert.True(bus.Publish("""{"source":"build","data":{"status":"green"}}"""));

        var rec = bus.Providers["build"];
        Assert.Equal("green", ((TextValue)rec.Data.Get("status")!).Text);
        Assert.Equal(T0, rec.ReceivedAt);

        var entry = Assert.Single(bus.Recent);
        Assert.True(entry.Accepted);
        Assert.Null(entry.Reason);
        Assert.Equal("build", entry.Source);
        Assert.Equal(T0, entry.At);
    }

    [Fact]
    public void A_Rejected_Line_Is_In_The_Ring_With_Its_Reason_And_No_Provider_Appears()
    {
        var (bus, _) = New();
        using var _d = bus;

        Assert.False(bus.Publish("""{"data":{}}"""));

        Assert.Empty(bus.Providers);
        var entry = Assert.Single(bus.Recent);
        Assert.False(entry.Accepted);
        Assert.Contains("source", entry.Reason!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("""{"data":{}}""", entry.Line);
    }

    [Fact]
    public void The_Ring_Keeps_The_Last_Fifty_And_Drops_The_Oldest()
    {
        var (bus, _) = New();
        using var _d = bus;

        for (var i = 0; i < EventBus.RingSize + 10; i++) bus.Publish(Numbered(i));

        Assert.Equal(EventBus.RingSize, bus.Recent.Count);
        Assert.Contains("\"n\":10", bus.Recent[0].Line);                        // oldest kept
        Assert.Contains("\"n\":59", bus.Recent[^1].Line);                       // newest, in arrival order
    }

    [Fact]
    public void A_Burst_Inside_The_Window_Wakes_Once()
    {
        var (bus, _) = New();
        using var _d = bus;
        var wakes = 0;
        bus.WakeRequested += () => wakes++;

        for (var i = 0; i < 50; i++) bus.Publish(Numbered(i));

        Assert.False(bus.PumpWake(T0.AddMilliseconds(399)));
        Assert.Equal(0, wakes);
        Assert.True(bus.PumpWake(T0.AddMilliseconds(400)));                     // the trailing wake
        Assert.Equal(1, wakes);
        Assert.False(bus.PumpWake(T0.AddMilliseconds(401)));                    // and it does not repeat
        Assert.Equal(1, wakes);
        Assert.Equal(49, ((NumberValue)bus.Providers["a"].Data.Get("n")!).Number);
    }

    [Fact]
    public void An_Event_Arriving_After_A_Wake_Starts_A_New_Window()
    {
        var (bus, clock) = New();
        using var _d = bus;
        var wakes = 0;
        bus.WakeRequested += () => wakes++;

        bus.Publish("""{"source":"a","data":{"n":1}}""");
        Assert.True(bus.PumpWake(T0 + Window));
        Assert.Equal(1, wakes);

        clock.Now = T0 + Window + TimeSpan.FromSeconds(1);
        bus.Publish("""{"source":"a","data":{"n":2}}""");
        Assert.False(bus.PumpWake(clock.Now));                                  // the new window has just opened
        Assert.True(bus.PumpWake(clock.Now + Window));
        Assert.Equal(2, wakes);
    }

    [Fact]
    public void Wake_False_Updates_The_Provider_Without_Ever_Waking()
    {
        var (bus, _) = New();
        using var _d = bus;
        var wakes = 0;
        bus.WakeRequested += () => wakes++;

        Assert.True(bus.Publish("""{"source":"a","data":{"n":1},"wake":false}"""));

        Assert.Equal(1, ((NumberValue)bus.Providers["a"].Data.Get("n")!).Number);
        Assert.False(bus.PumpWake(T0.AddHours(1)));
        Assert.Equal(0, wakes);
    }

    [Fact]
    public void An_Exact_Repeat_Of_The_Previous_Id_Is_Dropped()
    {
        var (bus, clock) = New();
        using var _d = bus;

        Assert.True(bus.Publish("""{"source":"a","data":{"n":1},"id":"7"}"""));
        clock.Now = T0.AddSeconds(5);
        Assert.False(bus.Publish("""{"source":"a","data":{"n":2},"id":"7"}"""));

        Assert.Equal(1, ((NumberValue)bus.Providers["a"].Data.Get("n")!).Number);
        Assert.Equal(T0, bus.Providers["a"].ReceivedAt);                        // not even touched
        Assert.False(bus.Recent[^1].Accepted);
        Assert.Contains("id", bus.Recent[^1].Reason!, StringComparison.OrdinalIgnoreCase);

        // A different id from the same producer goes through.
        Assert.True(bus.Publish("""{"source":"a","data":{"n":3},"id":"8"}"""));
        Assert.Equal(3, ((NumberValue)bus.Providers["a"].Data.Get("n")!).Number);
    }

    [Fact]
    public void An_Event_Without_An_Id_Is_Never_A_Duplicate()
    {
        var (bus, _) = New();
        using var _d = bus;

        Assert.True(bus.Publish("""{"source":"a","data":{"n":1}}"""));
        Assert.True(bus.Publish("""{"source":"a","data":{"n":2}}"""));
        Assert.Equal(2, ((NumberValue)bus.Providers["a"].Data.Get("n")!).Number);
    }

    [Fact]
    public void Forget_Removes_A_Provider_And_Is_Silent_About_An_Unknown_One()
    {
        var (bus, _) = New();
        using var _d = bus;

        bus.Publish("""{"source":"a","data":{"n":1}}""");
        bus.Forget("a");
        Assert.Empty(bus.Providers);

        bus.Forget("never-heard-of-it");                                        // no throw
        Assert.Empty(bus.Providers);
    }

    [Fact]
    public void Restore_Puts_Records_Back_Without_Waking()
    {
        var (bus, _) = New();
        using var _d = bus;
        var wakes = 0;
        bus.WakeRequested += () => wakes++;

        bus.Restore([ProviderRecord.Empty("a", T0.AddMinutes(-5))
            .Apply(EventEnvelopeParser.Parse("""{"source":"a","data":{"n":9}}""").Event!, T0.AddMinutes(-5))]);

        Assert.Equal(9, ((NumberValue)bus.Providers["a"].Data.Get("n")!).Number);
        Assert.False(bus.PumpWake(T0.AddHours(1)));
        Assert.Equal(0, wakes);
        Assert.Empty(bus.Recent);                                               // a restore is not an event
    }

    /// <summary>The designer's panel wants every accepted event, not one per coalescing window.</summary>
    [Fact]
    public void ProviderChanged_Is_Raised_Uncoalesced_For_Every_Accepted_Event()
    {
        var (bus, _) = New();
        using var _d = bus;
        var names = new List<string>();
        bus.ProviderChanged += names.Add;

        bus.Publish("""{"source":"a","data":{}}""");
        bus.Publish("""{"source":"b","data":{}}""");
        bus.Publish("""not json""");

        Assert.Equal(new[] { "a", "b" }, names);
    }

    /// <summary>Providers and Recent are read by the tick thread while the pipe thread writes
    /// them, so each has to hand back a snapshot rather than the live collection.</summary>
    [Fact]
    public void Providers_And_Recent_Are_Snapshots_Not_Live_Views()
    {
        var (bus, _) = New();
        using var _d = bus;
        bus.Publish("""{"source":"a","data":{}}""");

        var providers = bus.Providers;
        var recent = bus.Recent;
        bus.Publish("""{"source":"b","data":{}}""");

        Assert.Single(providers);
        Assert.Single(recent);
        Assert.Equal(2, bus.Providers.Count);
    }

    /// <summary>The one test that exercises the real coalescing timer rather than PumpWake. It
    /// is the load-bearing bit: the wake has to arrive with nobody pumping, off the publishing
    /// thread, and an exception out of that callback would take the daemon down.</summary>
    [Fact]
    public void With_AutoWake_The_Wake_Arrives_On_Another_Thread_Without_Anyone_Pumping()
    {
        using var bus = new EventBus(SystemClock.Instance, TimeSpan.FromMilliseconds(50));
        using var woke = new ManualResetEventSlim();
        var publishingThread = Environment.CurrentManagedThreadId;
        var wakeThread = 0;
        bus.WakeRequested += () => { wakeThread = Environment.CurrentManagedThreadId; woke.Set(); };

        for (var i = 0; i < 20; i++) bus.Publish(Numbered(i));

        Assert.True(woke.Wait(TimeSpan.FromSeconds(5)), "the coalesced wake never arrived");
        Assert.NotEqual(publishingThread, wakeThread);
        Assert.Equal(19, ((NumberValue)bus.Providers["a"].Data.Get("n")!).Number);
    }

    /// <summary>A handler that throws must not reach the timer callback, which would end the
    /// process, and the bus must still be usable afterwards.</summary>
    [Fact]
    public void A_Throwing_Wake_Handler_Does_Not_Escape_The_Timer_Callback()
    {
        using var bus = new EventBus(SystemClock.Instance, TimeSpan.FromMilliseconds(50));
        using var tried = new ManualResetEventSlim();
        bus.WakeRequested += () => { tried.Set(); throw new InvalidOperationException("the window has gone"); };

        bus.Publish("""{"source":"a","data":{"n":1}}""");

        Assert.True(tried.Wait(TimeSpan.FromSeconds(5)));
        Thread.Sleep(50);                                                       // let the callback unwind
        Assert.True(bus.Publish("""{"source":"a","data":{"n":2}}"""));
    }

    [Fact]
    public void Dispose_Is_Idempotent()
    {
        var (bus, _) = New();
        bus.Dispose();
        bus.Dispose();
    }
}
