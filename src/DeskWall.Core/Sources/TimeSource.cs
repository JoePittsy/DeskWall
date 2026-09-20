using DeskWall.Core.Values;

namespace DeskWall.Core.Sources;

/// <summary>Publishes the local time. Due on every whole minute.</summary>
public sealed class TimeSource(string name, IClock clock) : ISource
{
    public string Name => name;

    public DateTimeOffset NextDue(DateTimeOffset? lastRefresh, DateTimeOffset now)
    {
        if (lastRefresh is null) return now;
        var l = lastRefresh.Value;
        return new DateTimeOffset(l.Year, l.Month, l.Day, l.Hour, l.Minute, 0, l.Offset).AddMinutes(1);
    }

    public ValueTask<RecordValue> RefreshAsync(CancellationToken ct)
    {
        var now = clock.Now;
        return new(new RecordValue(new Dictionary<string, Value>
        {
            ["now"] = new TimeValue(now),
            ["date"] = new TextValue(now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)),
            ["weekday"] = new TextValue(now.DayOfWeek.ToString()),
        }));
    }
}
