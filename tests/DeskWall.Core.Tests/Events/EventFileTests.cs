using DeskWall.Core.Events;
using Xunit;

namespace DeskWall.Core.Tests.Events;

/// <summary>Issue #17: the designer's Forget rewrites events.json, and a running daemon must take
/// that as an instruction rather than write the forgotten record straight back.</summary>
public class EventFileTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    public EventFileTests() => Clear();

    public void Dispose()
    {
        Clear();
        GC.SuppressFinalize(this);
    }

    private static void Clear()
    {
        // DESKWALL_HOME points at a temp folder for the whole assembly.
        if (File.Exists(EventStore.Path)) File.Delete(EventStore.Path);
    }

    private static EventBus Bus() => Bus(new BusClock(T0));

    private static EventBus Bus(BusClock clock) => new(clock, TimeSpan.FromMilliseconds(400), autoWake: false);

    private static void Send(EventBus bus, string source)
        => Assert.True(bus.Publish("""{"source":"S","data":{"v":1}}""".Replace("S", source, StringComparison.Ordinal)));

    /// <summary>What the designer's Forget does: read the file, drop one record, write the rest.</summary>
    private static void DesignerForgets(string name)
        => EventStore.Save(EventStore.Load().Where(r => !string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)).ToList());

    private static string[] OnDisk() => [.. EventStore.Load().Select(r => r.Name).Order(StringComparer.Ordinal)];

    [Fact]
    public void A_Forget_Written_To_The_File_Is_Dropped_From_The_Bus_And_Stays_Gone()
    {
        using var bus = Bus();
        var file = new EventFile(bus);
        Send(bus, "build");
        Send(bus, "hearth");
        file.Save();

        DesignerForgets("build");
        var forgot = file.Reconcile();

        Assert.Equal(["build"], forgot);
        Assert.False(bus.Providers.ContainsKey("build"));
        Assert.True(bus.Providers.ContainsKey("hearth"));
        file.Save();                                     // the daemon's next save
        Assert.Equal(["hearth"], OnDisk());              // before #17 this was build, hearth again
    }

    [Fact]
    public void The_Daemons_Own_Save_Forgets_Nothing()
    {
        using var bus = Bus();
        var file = new EventFile(bus);
        Send(bus, "build");
        file.Save();

        Assert.Empty(file.Reconcile());
        Assert.True(bus.Providers.ContainsKey("build"));
    }

    [Fact]
    public void A_Provider_That_Arrived_Since_The_Last_Save_Is_Not_Taken_For_Forgotten()
    {
        // It is missing from the file only because the daemon has not written it yet.
        using var bus = Bus();
        var file = new EventFile(bus);
        Send(bus, "build");
        file.Save();
        Send(bus, "fresh");

        DesignerForgets("build");

        Assert.Equal(["build"], file.Reconcile());
        Assert.True(bus.Providers.ContainsKey("fresh"));
    }

    [Fact]
    public void Records_Restored_At_Start_Can_Be_Forgotten()
    {
        using (var before = Bus())
        {
            var first = new EventFile(before);
            Send(before, "build");
            Send(before, "hearth");
            first.Save();
        }

        using var bus = Bus();
        var file = new EventFile(bus);
        file.Restore();
        Assert.True(bus.Providers.ContainsKey("build"));

        DesignerForgets("hearth");

        Assert.Equal(["hearth"], file.Reconcile());
        Assert.Equal(["build"], bus.Providers.Keys);
    }

    [Fact]
    public void A_Missing_Or_Unreadable_File_Is_Not_A_Forget_Of_Everything()
    {
        using var bus = Bus();
        var file = new EventFile(bus);
        Send(bus, "build");
        file.Save();

        File.Delete(EventStore.Path);
        Assert.Empty(file.Reconcile());

        File.WriteAllText(EventStore.Path, "{ \"providers\": [ {\"name\": ");   // torn or mid-edit
        Assert.Empty(file.Reconcile());

        Assert.True(bus.Providers.ContainsKey("build"));
    }

    [Fact]
    public void A_Forgotten_Provider_That_Sends_Again_Comes_Back()
    {
        using var bus = Bus();
        var file = new EventFile(bus);
        Send(bus, "build");
        file.Save();
        DesignerForgets("build");
        file.Reconcile();

        Send(bus, "build");
        file.Save();

        Assert.Equal(["build"], OnDisk());
    }

    [Fact]
    public void A_Provider_That_Sends_Between_The_Forget_And_The_Read_Back_Is_Kept()
    {
        var clock = new BusClock(T0);
        using var bus = Bus(clock);
        var file = new EventFile(bus);
        Send(bus, "build");
        file.Save();

        DesignerForgets("build");
        clock.Now = T0.AddSeconds(1);
        Send(bus, "build");                              // sent after the click, before the watcher fired

        Assert.Empty(file.Reconcile());
        Assert.True(bus.Providers.ContainsKey("build"));
    }
}
