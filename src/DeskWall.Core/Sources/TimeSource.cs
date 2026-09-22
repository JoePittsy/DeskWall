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

    public TimeSpan Interval(DateTimeOffset now) => TimeSpan.FromMinutes(1);

    public ValueTask<RecordValue> RefreshAsync(CancellationToken ct)
    {
        var now = clock.Now;
        var day = now.TimeOfDay.TotalDays;
        // Monday first: DayOfWeek counts from Sunday = 0, and a week that rolls over on Sunday
        // evening is not the week anyone here plans by.
        var week = (((int)now.DayOfWeek + 6) % 7 + day) / 7d;
        var year = (now.DayOfYear - 1 + day) / (DateTime.IsLeapYear(now.Year) ? 366d : 365d);
        var d = new Dictionary<string, Value>
        {
            ["now"] = new TimeValue(now),
            ["date"] = new TextValue(now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)),
            ["weekday"] = new TextValue(now.DayOfWeek.ToString()),
        };
        // Both forms of each: a bar binds the fraction, a text binds the percent, and a format
        // string cannot multiply by 100. Fractions are quantised so a content key does not change
        // for a difference below a pixel, the same rule as ResolvedBar.
        Progress(d, "day", day);
        Progress(d, "week", week);
        Progress(d, "year", year);
        return new(new RecordValue(d));
    }

    private static void Progress(Dictionary<string, Value> d, string prefix, double fraction)
    {
        d[prefix + "Fraction"] = new NumberValue(Math.Round(fraction, 4));
        d[prefix + "Percent"] = new NumberValue(Math.Round(fraction * 100));
    }
}
