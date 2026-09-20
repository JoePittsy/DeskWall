using DeskWall.Core.Sources;

namespace DeskWall.Core.Scheduling;

/// <summary>Pure next-wake maths. No timers here; the host owns the timer.</summary>
public sealed class Scheduler(IReadOnlyList<ISource> sources, SourceRegistry registry)
{
    public static readonly TimeSpan MinDelay = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(15);

    /// <summary>When a source is next due. A healthy source answers for itself through NextDue; a
    /// failing one is scheduled from its last *attempt* with an exponential back-off, because
    /// LastRefresh is still the last good refresh (or null) and NextDue would say "due now" forever -
    /// which pinned the daemon's wake at MinDelay, i.e. four ticks a second, for as long as the
    /// failure lasted (finding 1). The same rule decides both the wake and whether to run a source,
    /// so the loop cannot wake for a source the tick then declines to refresh.</summary>
    public static DateTimeOffset DueAt(ISource source, SourceSnapshot snapshot, DateTimeOffset now)
    {
        if (snapshot.ConsecutiveFailures == 0) return source.NextDue(snapshot.LastRefresh, now);
        var anchor = snapshot.LastAttempt ?? snapshot.LastRefresh;
        return anchor is null ? now : anchor.Value + BackOff(source.Interval(now), snapshot.ConsecutiveFailures);
    }

    /// <summary>Retry delay after n consecutive failures: the source's own interval doubled per
    /// failure, capped at MaxDelay and never shorter than the interval itself. Spec 3.2 asks for a
    /// failing source to be "retried on its next schedule"; this is that schedule, slowed down.</summary>
    public static TimeSpan BackOff(TimeSpan interval, int failures)
    {
        if (interval <= TimeSpan.Zero) interval = MinDelay;
        if (failures <= 1) return interval;
        var seconds = interval.TotalSeconds * Math.Pow(2, Math.Min(failures - 1, 32));
        var scaled = seconds >= MaxDelay.TotalSeconds ? MaxDelay : TimeSpan.FromSeconds(seconds);
        return scaled < interval ? interval : scaled;
    }

    /// <summary>Whether this source should be refreshed on a tick at <paramref name="now"/>.</summary>
    public static bool IsDue(ISource source, SourceSnapshot snapshot, DateTimeOffset now)
        => DueAt(source, snapshot, now) <= now;

    /// <summary>Earliest due time across all sources, clamped to [now + MinDelay, now + MaxDelay].</summary>
    public DateTimeOffset NextWake(DateTimeOffset now)
    {
        var earliest = now + MaxDelay;
        foreach (var s in sources)
        {
            var due = DueAt(s, registry.Get(s.Name), now);
            if (due < earliest) earliest = due;
        }
        var floor = now + MinDelay;
        return earliest < floor ? floor : earliest;
    }

    /// <summary>Sources due at or before now.</summary>
    public IReadOnlyList<ISource> Due(DateTimeOffset now)
        => sources.Where(s => IsDue(s, registry.Get(s.Name), now)).ToList();
}
