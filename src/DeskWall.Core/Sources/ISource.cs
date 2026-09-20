using DeskWall.Core.Values;

namespace DeskWall.Core.Sources;

/// <summary>A provider that runs on its own schedule and publishes a RecordValue.
/// Implementations must be safe to call from a background thread and must not hold
/// resources between refreshes.</summary>
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
