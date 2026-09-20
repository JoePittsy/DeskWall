using DeskWall.Core.Sources;

namespace DeskWall.Core.Scheduling;

/// <summary>Pure next-wake maths. No timers here; the host owns the timer.</summary>
public sealed class Scheduler(IReadOnlyList<ISource> sources, SourceRegistry registry)
{
    public static readonly TimeSpan MinDelay = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(15);

    /// <summary>Earliest NextDue across all sources, clamped to [now + MinDelay, now + MaxDelay].</summary>
    public DateTimeOffset NextWake(DateTimeOffset now)
    {
        var earliest = now + MaxDelay;
        foreach (var s in sources)
        {
            var due = s.NextDue(registry.Get(s.Name).LastRefresh, now);
            if (due < earliest) earliest = due;
        }
        var floor = now + MinDelay;
        return earliest < floor ? floor : earliest;
    }

    /// <summary>Sources due at or before now.</summary>
    public IReadOnlyList<ISource> Due(DateTimeOffset now)
        => sources.Where(s => s.NextDue(registry.Get(s.Name).LastRefresh, now) <= now).ToList();
}
