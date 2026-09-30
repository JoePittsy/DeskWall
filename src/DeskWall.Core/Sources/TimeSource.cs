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

    public ValueTask<RecordValue> RefreshAsync(CancellationToken ct) => new(new RecordValue(Fields(clock.Now)));

    /// <summary>Where the day fraction changes phase. The sun image rises at 0.25 and sets at 0.75,
    /// so dawn and dusk straddle those; stars start to show from 0.72, inside dusk.</summary>
    public const double DawnStart = 0.21, DayStart = 0.29, DuskStart = 0.71, NightStart = 0.83;

    /// <summary>"night", "dawn", "day" or "dusk" for a day fraction. One Step rule on this is the
    /// palette for a layer that only needs four colours.</summary>
    public static string Phase(double dayFraction) => dayFraction switch
    {
        < DawnStart => "night",
        < DayStart => "dawn",
        < DuskStart => "day",
        < NightStart => "dusk",
        _ => "night",
    };

    /// <summary>Every field this source publishes for <paramref name="now"/>. Public so a preview
    /// can pin the whole set to one instant rather than a fraction that disagrees with the clock.</summary>
    public static Dictionary<string, Value> Fields(DateTimeOffset now)
    {
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
        d["phase"] = new TextValue(Phase(day));
        return d;
    }

    private static void Progress(Dictionary<string, Value> d, string prefix, double fraction)
    {
        d[prefix + "Fraction"] = new NumberValue(Math.Round(fraction, 4));
        d[prefix + "Percent"] = new NumberValue(Math.Round(fraction * 100));
    }
}
