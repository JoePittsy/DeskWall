using DeskWall.Core.Sources;

namespace DeskWall.Core.Events;

/// <summary>The one rule for "something in this process has new values": the source says so by
/// name, the bus coalesces, the host wakes once.
/// <para>It lives here rather than in each host because there are two hosts - the daemon and the
/// designer's LiveSources - and before this each had its own wiring for AsyncSource, plus the
/// daemon had a third for the image cache. One rule, one implementation, one test.</para></summary>
public static class SourceSignals
{
    /// <summary>The handler a host attaches to <see cref="ISignalSource.Changed"/>.</summary>
    public static Action<ISource> To(EventBus bus)
    {
        ArgumentNullException.ThrowIfNull(bus);
        return src => bus.Signal(src.Name);
    }

    /// <summary>Attach <see cref="To"/> to every signalling source in the set, and hand the handler
    /// back so it can be detached again: the designer rebuilds its whole source set on every edit,
    /// and a handler left behind keeps the discarded set alive through the bus.</summary>
    public static Action<ISource> ConnectAll(EventBus bus, IEnumerable<ISource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var handler = To(bus);
        foreach (var s in sources.OfType<ISignalSource>()) s.Changed += handler;
        return handler;
    }
}
