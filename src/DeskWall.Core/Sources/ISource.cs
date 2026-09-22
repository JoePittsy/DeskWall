using DeskWall.Core.Values;

namespace DeskWall.Core.Sources;

/// <summary>A provider that runs on its own schedule and publishes a RecordValue.
/// Implementations must be safe to call from a background thread, and should not hold resources
/// between refreshes.
/// <para>A source that has to - <c>hardware</c> holds a sampling timer and, on an NVIDIA machine,
/// an open NVML library - implements <see cref="IDisposable"/> as well, and its host lets go of it
/// with <see cref="SourceFactory.DisposeAll"/> whenever the set of sources is replaced (a layout
/// change, a display change, an edit in the designer) and at shutdown. Dispose must be idempotent:
/// nothing guarantees it is called exactly once.</para></summary>
public interface ISource
{
    /// <summary>Instance name used as the root field in the value tree (from the layout).</summary>
    string Name { get; }

    /// <summary>Next time this source is due, given the last refresh (null = never ran).</summary>
    DateTimeOffset NextDue(DateTimeOffset? lastRefresh, DateTimeOffset now);

    /// <summary>How long this source wants between refreshes. NextDue cannot answer that for a source
    /// that has never succeeded, which is exactly the case the scheduler has to back off (finding 1);
    /// a source with no interval of its own inherits the daemon's one-minute granularity.</summary>
    TimeSpan Interval(DateTimeOffset now) => TimeSpan.FromMinutes(1);

    /// <summary>Produce the current values. Throwing marks the source failed (spec 3.2).</summary>
    ValueTask<RecordValue> RefreshAsync(CancellationToken ct);
}

/// <summary>A source that knows when it has something new, rather than waiting to be asked.
/// <para>One event for what used to be four bespoke wakes: an async fetch landing after its tick
/// gave up, an image arriving, a watched file changing, a streaming command printing a line. The
/// host attaches <see cref="Events.SourceSignals"/> to every one of these, the bus coalesces, and
/// the whole lot costs one repaint.</para>
/// <para>Raising <see cref="Changed"/> is a claim that the source is due, so an implementation's
/// <see cref="ISource.NextDue"/> must answer <c>now</c> while it holds something unharvested. The
/// wake is not forced: the tick still asks the scheduler which sources to refresh, and a source
/// that signals but says it is not due wakes the machine for nothing.</para>
/// <para>Changed is raised on whatever thread noticed - a pool thread, a watcher thread, a reader
/// thread - so a handler must be thread safe and must not block.</para></summary>
public interface ISignalSource
{
    event Action<ISource>? Changed;

    /// <summary>True while the source is holding something no refresh has published yet.
    /// <para>The scheduler asks, because a failing source is otherwise scheduled from its back-off
    /// and its own NextDue is ignored (finding 1, which must stay true). A push saying "due now" is
    /// not a request to retry a failure, it is a statement that the answer is already in hand, and
    /// without this a volume change or a file save is swallowed for the whole back-off.</para>
    /// <para>Every implementation must clear it when a refresh is <em>attempted</em>, not when one
    /// succeeds. That is what stops a source that keeps failing being due forever and pinning the
    /// daemon at <see cref="Scheduling.Scheduler.MinDelay"/>: one signal buys one attempt.</para></summary>
    bool HasPending { get; }
}

/// <summary>Helper for the common "every N" schedule.</summary>
public abstract class PeriodicSource(string name, TimeSpan every) : ISource
{
    public string Name => name;
    public TimeSpan Every => every;

    public virtual DateTimeOffset NextDue(DateTimeOffset? lastRefresh, DateTimeOffset now)
        => lastRefresh is null ? now : lastRefresh.Value + every;

    public virtual TimeSpan Interval(DateTimeOffset now) => every;

    public abstract ValueTask<RecordValue> RefreshAsync(CancellationToken ct);
}
