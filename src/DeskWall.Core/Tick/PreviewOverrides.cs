using System.Globalization;
using System.Text.RegularExpressions;
using DeskWall.Core.Sources;
using DeskWall.Core.Values;

namespace DeskWall.Core.Tick;

/// <summary>
/// `deskwall tick --preview key=value,...`: pins values in the source tree after the sources have
/// refreshed and before the resolve, so a layout can be looked at in a state the machine is not in
/// (midnight, a storm, a full drive). Keys are dotted paths into the tree
/// (<c>weather.json.current.weather_code=63</c>); a missing record on the way is created, and a
/// number after a list steps into that item (<c>disks.drives.0.usedFraction=0.95</c>). Values
/// become a <see cref="NumberValue"/> when they parse as an invariant number, a
/// <see cref="BoolValue"/> for true/false, otherwise a <see cref="TextValue"/>.
/// <para>Time is pinned as a whole: <c>time.at=HH:mm</c> (or <c>time.now=HH:mm</c>, or a bare
/// <c>time.dayFraction</c>) sets now, date, weekday, dayFraction, dayPercent and phase together,
/// so a layer bound to the phase and one bound to the fraction agree. The phase and sun fractions
/// follow the live <c>time.sunrise</c>/<c>time.sunset</c>, or <c>time.sunrise=HH:mm</c> /
/// <c>time.sunset=HH:mm</c> pins when given. Explicit pins win over the
/// derived ones. Pinning <c>hardware.cpu</c>, <c>ram</c> or <c>gpu</c> also pins the matching
/// history to a plausible curve that peaks at the pinned value, so a <c>line</c> has a shape.</para>
/// </summary>
public sealed partial class PreviewOverrides
{
    private readonly List<(string[] Path, string Raw)> _pins;

    private PreviewOverrides(List<(string[] Path, string Raw)> pins) => _pins = pins;

    public IReadOnlyList<string> Keys => _pins.Select(p => string.Join('.', p.Path)).ToList();

    /// <summary>A comma only separates pins when the next thing is another <c>key=</c>, so a media
    /// title can carry commas of its own.</summary>
    [GeneratedRegex(@",(?=\s*[A-Za-z_][A-Za-z0-9_.\-]*=)")]
    private static partial Regex PinSeparator();

    public static PreviewOverrides Parse(string spec)
    {
        var pins = new List<(string[], string)>();
        foreach (var part in PinSeparator().Split(spec))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) throw new FormatException($"--preview entry '{part}' is not key=value");
            var key = part[..eq].Trim();
            var path = key.Split('.');
            if (path.Any(s => s.Length == 0)) throw new FormatException($"--preview key '{key}' has an empty segment");
            pins.Add((path, part[(eq + 1)..]));
        }
        return new PreviewOverrides(pins);
    }

    public RecordValue Apply(RecordValue tree)
    {
        var root = tree;
        // Time first, so an explicit time.* pin after it still wins.
        var pinnedAt = TimePin();
        var timePinned = pinnedAt is not null;
        if (pinnedAt is { } at)
        {
            // The sun the live tick resolved (present only when the time source has sun settings),
            // unless a pin moves it: without it the phase falls back to the fixed thresholds and a
            // preview shows a different phase from the one the desktop would at that time.
            var rise = SunPin("sunrise") ?? LiveSun(tree, "sunrise", at);
            var set = SunPin("sunset") ?? LiveSun(tree, "sunset", at);
            foreach (var (name, value) in TimeSource.Fields(at, rise, set))
                root = Set(root, ["time", name], value);
        }
        foreach (var metric in new[] { "cpu", "ram", "gpu" })
            if (Find("hardware", metric) is { } raw && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var peak))
                root = Set(root, ["hardware", metric + "History"], Curve(peak, metric.Length * 7 + metric[0]));
        foreach (var (path, raw) in _pins)
        {
            if (path is ["time", var f] && f.Equals("at", StringComparison.OrdinalIgnoreCase)) continue;
            if (path is ["time", var n] && n.Equals("now", StringComparison.OrdinalIgnoreCase) && ParseClock(raw) is not null) continue;
            // Already derived from whichever time pin came last.
            if (timePinned && path is ["time", var d] && d.Equals("dayFraction", StringComparison.OrdinalIgnoreCase)) continue;
            // Already published as a TimeValue by the derivation, which it also moved.
            if (timePinned && path is ["time", var s] && IsSunKey(s) && ParseClock(raw) is not null) continue;
            root = Set(root, path, Typed(raw));
        }
        return root;
    }

    private string? Find(params string[] path)
        => _pins.LastOrDefault(p => p.Path.Length == path.Length && p.Path.Zip(path).All(z => z.First.Equals(z.Second, StringComparison.OrdinalIgnoreCase))).Raw;

    private static bool IsSunKey(string s)
        => s.Equals("sunrise", StringComparison.OrdinalIgnoreCase) || s.Equals("sunset", StringComparison.OrdinalIgnoreCase);

    /// <summary><c>time.sunrise=HH:mm</c> / <c>time.sunset=HH:mm</c>, the last of each.</summary>
    private TimeSpan? SunPin(string name) => Find("time", name) is { } raw ? ParseClock(raw) : null;

    /// <summary>The time source's own <c>time.sunrise</c>/<c>time.sunset</c>, as a local time of
    /// day in the pinned instant's offset.</summary>
    private static TimeSpan? LiveSun(RecordValue tree, string name, DateTimeOffset at)
        => tree.Get("time") is RecordValue time && time.Get(name) is TimeValue tv ? tv.Time.ToOffset(at.Offset).TimeOfDay : null;

    private DateTimeOffset? TimePin()
    {
        var today = DateTimeOffset.Now.Date;
        // The last of time.at / time.now / time.dayFraction wins, so a scene can override a baseline.
        var last = _pins.LastOrDefault(p => p.Path.Length == 2 && p.Path[0].Equals("time", StringComparison.OrdinalIgnoreCase)
            && (p.Path[1].Equals("at", StringComparison.OrdinalIgnoreCase)
                || (p.Path[1].Equals("now", StringComparison.OrdinalIgnoreCase) && ParseClock(p.Raw) is not null)
                || p.Path[1].Equals("dayFraction", StringComparison.OrdinalIgnoreCase)));
        if (last.Path is null) return null;
        if (!last.Path[1].Equals("dayFraction", StringComparison.OrdinalIgnoreCase))
            return ParseClock(last.Raw) is { } t ? new DateTimeOffset(today + t, TimeZoneInfo.Local.GetUtcOffset(today + t)) : null;
        if (double.TryParse(last.Raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var f))
        {
            var local = today + TimeSpan.FromDays(Math.Clamp(f, 0, 0.99999));
            return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
        }
        return null;
    }

    private static TimeSpan? ParseClock(string s)
        => TimeSpan.TryParseExact(s.Trim(), [@"h\:mm", @"hh\:mm", @"h\:mm\:ss", @"hh\:mm\:ss"], CultureInfo.InvariantCulture, out var t) ? t : null;

    private static Value Typed(string raw)
    {
        var s = raw.Trim();
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) return new NumberValue(n);
        if (s.Equals("true", StringComparison.OrdinalIgnoreCase)) return new BoolValue(true);
        if (s.Equals("false", StringComparison.OrdinalIgnoreCase)) return new BoolValue(false);
        return new TextValue(raw);
    }

    /// <summary>Thirty samples, oldest first, shaped like a range: a low start, a main summit at the
    /// pinned value about two thirds of the way along, a shoulder before it and a fall after.
    /// Deterministic, so the gallery renders the same picture every time.</summary>
    private static ListValue Curve(double peak, int seed)
    {
        const int n = 30;
        var items = new RecordValue[n];
        for (var i = 0; i < n; i++)
        {
            var t = i / (double)(n - 1);
            var summit = Math.Exp(-Math.Pow((t - 0.68) / 0.13, 2));
            var shoulder = 0.62 * Math.Exp(-Math.Pow((t - 0.3) / 0.1, 2));
            var texture = 0.07 * Math.Sin(i * 1.7 + seed) + 0.05 * Math.Sin(i * 3.1 + seed * 0.5);
            var v = Math.Clamp(peak * (0.18 + 0.82 * Math.Max(summit, shoulder) + texture), 0, Math.Clamp(peak, 0, 1));
            if (i == (int)Math.Round(0.68 * (n - 1))) v = Math.Clamp(peak, 0, 1);
            items[i] = new RecordValue(new Dictionary<string, Value> { ["v"] = new NumberValue(Math.Round(v, 3)) });
        }
        return new ListValue(items, null);
    }

    /// <summary>A copy of <paramref name="record"/> with <paramref name="path"/> set, creating any
    /// record missing on the way (and replacing a non-record that is in the way).</summary>
    private static RecordValue Set(RecordValue record, ReadOnlySpan<string> path, Value value)
    {
        var comparer = record.Fields is Dictionary<string, Value> d ? d.Comparer : StringComparer.OrdinalIgnoreCase;
        var copy = new Dictionary<string, Value>(record.Fields, comparer);
        var head = path[0];
        var key = copy.Keys.FirstOrDefault(k => k.Equals(head, StringComparison.OrdinalIgnoreCase)) ?? head;
        if (path.Length == 1) copy[key] = value;
        else if (copy.TryGetValue(key, out var list) && list is ListValue l
                 && int.TryParse(path[1], NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index < l.Items.Count)
        {
            // disks.drives.0.usedFraction: into one item of a list, leaving the others as they are.
            var items = l.Items.ToArray();
            items[index] = path.Length == 2 && value is RecordValue rv ? rv : path.Length == 2 ? items[index] : Set(items[index], path[2..], value);
            copy[key] = l with { Items = items };
        }
        else
        {
            var child = copy.TryGetValue(key, out var existing) && existing is RecordValue r ? r : new RecordValue(new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase));
            copy[key] = Set(child, path[1..], value);
        }
        return new RecordValue(copy);
    }
}
