using System.Globalization;
using System.Text.RegularExpressions;
using DeskWall.Core.Bindings;
using DeskWall.Core.Layout;
using DeskWall.Core.Values;

namespace DeskWall.Core.Sources;

/// <summary>Publishes the local time. Due on every whole minute.
/// <para>With <c>sunrise</c>/<c>sunset</c> settings (a literal <c>HH:mm</c>, or a binding path such as
/// <c>weather.json.daily.sunrise[0]</c> read from <see cref="SourceTree.Latest"/>) the phase follows
/// the real sun, and <c>sunFraction</c>/<c>nightFraction</c> run from sunrise to sunset and back.</para></summary>
public sealed partial class TimeSource : ISource
{
    private readonly string _name;
    private readonly IClock _clock;
    private readonly SunSetting? _rise, _set;
    private readonly Func<RecordValue?> _tree;
    private TimeSpan? _goodRise, _goodSet;

    public TimeSource(string name, IClock clock) : this(name, clock, null, null) { }

    /// <param name="sunrise">Null, a local <c>HH:mm</c>, or a binding path to another source's value.</param>
    /// <param name="tree">Where a path is resolved; <see cref="SourceTree.Latest"/> by default.</param>
    public TimeSource(string name, IClock clock, string? sunrise, string? sunset, Func<RecordValue?>? tree = null)
    {
        _name = name;
        _clock = clock;
        _rise = SunSetting.Parse("sunrise", sunrise);
        _set = SunSetting.Parse("sunset", sunset);
        _tree = tree ?? (() => SourceTree.Latest);
    }

    public static TimeSource FromDef(SourceDef def, IClock clock)
        => new(def.Name, clock, def.Settings.GetValueOrDefault("sunrise"), def.Settings.GetValueOrDefault("sunset"));

    public string Name => _name;

    public DateTimeOffset NextDue(DateTimeOffset? lastRefresh, DateTimeOffset now)
    {
        if (lastRefresh is null) return now;
        var l = lastRefresh.Value;
        return new DateTimeOffset(l.Year, l.Month, l.Day, l.Hour, l.Minute, 0, l.Offset).AddMinutes(1);
    }

    public TimeSpan Interval(DateTimeOffset now) => TimeSpan.FromMinutes(1);

    public ValueTask<RecordValue> RefreshAsync(CancellationToken ct)
    {
        var now = _clock.Now;
        if (_rise is null && _set is null) return new(new RecordValue(Fields(now)));
        var tree = _rise?.Path is not null || _set?.Path is not null ? _tree() : null;
        var rise = _rise?.Read(tree, now) ?? _goodRise ?? DefaultSunrise;
        var set = _set?.Read(tree, now) ?? _goodSet ?? DefaultSunset;
        // A pair the wrong way round (a path that resolved to yesterday's sunset, a typo) is not a
        // day; keep the last pair that was one.
        if (rise < set) { _goodRise = rise; _goodSet = set; }
        else { rise = _goodRise ?? DefaultSunrise; set = _goodSet ?? DefaultSunset; }
        return new(new RecordValue(Fields(now, rise, set)));
    }

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

    /// <summary>Every field this source publishes for <paramref name="now"/>, with no sun settings.
    /// Public so a preview can pin the whole set to one instant rather than a fraction that
    /// disagrees with the clock.</summary>
    public static Dictionary<string, Value> Fields(DateTimeOffset now) => Fields(now, null, null);

    /// <summary>Where the sun is taken to rise and set when nothing says otherwise, for
    /// <c>sunFraction</c>/<c>nightFraction</c> and for a sun setting that has not resolved yet.</summary>
    public static readonly TimeSpan DefaultSunrise = TimeSpan.FromHours(6), DefaultSunset = TimeSpan.FromHours(18);

    /// <summary>Dawn is sunrise plus or minus this, dusk is sunset plus or minus this.</summary>
    public static readonly TimeSpan Twilight = TimeSpan.FromMinutes(40);

    /// <summary>As <see cref="Fields(DateTimeOffset)"/>, with the sun at local times of day
    /// <paramref name="sunrise"/> and <paramref name="sunset"/>. Given both, <c>phase</c> follows
    /// them and <c>sunrise</c>/<c>sunset</c> are published; given neither, <c>phase</c> keeps the
    /// fixed day-fraction thresholds.</summary>
    public static Dictionary<string, Value> Fields(DateTimeOffset now, TimeSpan? sunrise, TimeSpan? sunset)
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
        var sun = sunrise is { } r && sunset is { } s && r < s ? (Rise: r, Set: s) : ((TimeSpan Rise, TimeSpan Set)?)null;
        var (rise, set) = sun ?? (DefaultSunrise, DefaultSunset);
        var t = now.TimeOfDay;
        d["phase"] = new TextValue(sun is null ? Phase(day) : Phase(t, rise, set));
        d["sunFraction"] = new NumberValue(Math.Round(Math.Clamp((t - rise) / (set - rise), 0, 1), 4));
        var night = TimeSpan.FromDays(1) - (set - rise);
        var intoNight = t >= set ? t - set : t < rise ? t + TimeSpan.FromDays(1) - set : TimeSpan.Zero;
        d["nightFraction"] = new NumberValue(Math.Round(Math.Clamp(intoNight / night, 0, 1), 4));
        if (sun is not null)
        {
            d["sunrise"] = new TimeValue(At(now, rise));
            d["sunset"] = new TimeValue(At(now, set));
        }
        return d;
    }

    /// <summary>The phase for a local time of day, with the sun rising at <paramref name="sunrise"/>
    /// and setting at <paramref name="sunset"/>: dawn and dusk are <see cref="Twilight"/> either side.
    /// A day too short for both twilights is dawn then dusk, with no day between.</summary>
    public static string Phase(TimeSpan timeOfDay, TimeSpan sunrise, TimeSpan sunset)
    {
        if (timeOfDay >= sunrise - Twilight && timeOfDay < sunrise + Twilight) return "dawn";
        if (timeOfDay >= sunset - Twilight && timeOfDay < sunset + Twilight) return "dusk";
        return timeOfDay > sunrise && timeOfDay < sunset ? "day" : "night";
    }

    private static DateTimeOffset At(DateTimeOffset now, TimeSpan timeOfDay)
        => new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset) + timeOfDay;

    /// <summary>One of the two settings: a fixed time of day, or a path into the value tree.</summary>
    private sealed partial class SunSetting
    {
        public TimeSpan? Fixed { get; private init; }
        public Binding? Path { get; private init; }

        public static SunSetting? Parse(string key, string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            text = text.Trim();
            if (ClockText(text) is { } fixedAt) return new SunSetting { Fixed = fixedAt };
            try { return new SunSetting { Path = Binding.Parse(text) }; }
            catch (FormatException ex) { throw new ArgumentException($"time: {key} '{text}' is neither HH:mm nor a binding path ({ex.Message})", ex); }
        }

        /// <summary>This setting's time of day, or null when its path has nothing usable yet.</summary>
        public TimeSpan? Read(RecordValue? tree, DateTimeOffset now)
        {
            if (Fixed is { } f) return f;
            if (tree is null || Path is null) return null;
            var v = BindingResolver.Resolve(Path, tree);
            // A JSON array of scalars arrives as records of one field, "value" (JsonValues.List).
            if (v is RecordValue { Fields.Count: 1 } one && one.Get("value") is { } inner) v = inner;
            return v switch
            {
                TimeValue tv => tv.Time.ToOffset(now.Offset).TimeOfDay,
                NumberValue n when n.Number > 0 => DateTimeOffset.FromUnixTimeSeconds((long)n.Number).ToOffset(now.Offset).TimeOfDay,
                TextValue tx => FromText(tx.Text, now),
                _ => null,
            };
        }

        /// <summary>"07:02", or an ISO date-time: Open-Meteo's <c>2026-09-30T07:02</c> with
        /// <c>timezone=auto</c> is already local and is taken as written; one with an offset is
        /// converted to the clock's.</summary>
        private static TimeSpan? FromText(string text, DateTimeOffset now)
        {
            text = text.Trim();
            if (ClockText(text) is { } c) return c;
            if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt)) return null;
            return dt.Kind == DateTimeKind.Unspecified ? dt.TimeOfDay : new DateTimeOffset(dt.ToUniversalTime(), TimeSpan.Zero).ToOffset(now.Offset).TimeOfDay;
        }

        private static TimeSpan? ClockText(string text)
            => ClockPattern().Match(text) is { Success: true } m
               && int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) is var h and < 24
               && int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) is var mi and < 60
                ? new TimeSpan(h, mi, 0) : null;

        [GeneratedRegex(@"^(\d{1,2}):(\d{2})$")]
        private static partial Regex ClockPattern();
    }

    private static void Progress(Dictionary<string, Value> d, string prefix, double fraction)
    {
        d[prefix + "Fraction"] = new NumberValue(Math.Round(fraction, 4));
        d[prefix + "Percent"] = new NumberValue(Math.Round(fraction * 100));
    }
}
