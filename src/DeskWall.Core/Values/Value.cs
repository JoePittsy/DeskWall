using System.Globalization;

namespace DeskWall.Core.Values;

/// <summary>A value published by a source. Closed hierarchy; pattern-match on it.</summary>
public abstract record Value
{
    /// <summary>Render as text. <paramref name="format"/> is a .NET format string for the
    /// value's type, or a composite format containing {0}, or a map beginning with '?', or null
    /// for the default.
    /// A malformed format string (an argument index the value does not supply, an unbalanced
    /// brace, an unknown type specifier) falls back to the unformatted text rather than throwing:
    /// a user-authored layout must never abort the tick. Spec 3.2 / plan Task 6.</summary>
    public string ToText(string? format)
    {
        var inv = CultureInfo.InvariantCulture;
        try
        {
            // A map: "?true=#D13438,false=#EBFFFFFF" picks a string by the value's own text. It
            // lives here rather than in a component because color, text and an image path are all
            // bindable properties, so one feature makes all three react to a bool.
            if (format is not null && format.StartsWith('?') && format.Contains('='))
                return MapLookup(format, ToText(null));
            if (IsBlend(format))
                return Blend(format!, ToText(null));
            if (format is not null && format.Contains("{0"))
                return string.Format(inv, format, NumericIfAsked(format));
            return this switch
            {
                TextValue t => t.Text,
                NumberValue n => n.Number.ToString(format, inv),
                TimeValue t => t.Time.ToString(format ?? "o", inv),
                BoolValue b => b.Flag ? "True" : "False",
                ImageValue i => i.Path,
                ListValue l => $"[{l.Items.Count} items]",
                RecordValue r => $"{{{r.Fields.Count} fields}}",
                _ => throw new InvalidOperationException(),
            };
        }
        catch (FormatException) when (format is not null)
        {
            return ToText(null);
        }
    }

    /// <summary>"?a=one,b=two,*=other" -> the entry whose key matches <paramref name="text"/>,
    /// case-insensitively. No match and no '*' entry is the **empty string**, not the unformatted
    /// text: showing nothing when a flag is false is the whole point of the feature. Keys are
    /// trimmed (a space after the comma is a typo, not a key); the picked text is taken verbatim.
    /// A key or a value therefore cannot contain ',' or '='; docs/layout-format.md says so.</summary>
    private static string MapLookup(string format, string text)
    {
        var rest = format.AsSpan(1);
        string? fallback = null;
        while (NextMapPair(ref rest, out var key, out var to))
        {
            if (key.Length == 1 && key[0] == '*') fallback = new string(to);
            else if (key.Length > 0 && key[0] is '<' or '>') { if (Holds(key, text)) return new string(to); }
            else if (key.Equals(text, StringComparison.OrdinalIgnoreCase)) return new string(to);
        }
        return fallback ?? "";
    }

    /// <summary>Every string a map format can pick, in order, or none when
    /// <paramref name="format"/> is not a map. What the tick tries for a base image when the one
    /// picked is missing and there is no previous base to keep.</summary>
    public static IReadOnlyList<string> MapValues(string? format)
    {
        if (format is null || !format.StartsWith('?') || !format.Contains('=')) return [];
        var values = new List<string>();
        var rest = format.AsSpan(1);
        while (NextMapPair(ref rest, out _, out var to)) values.Add(new string(to));
        return values;
    }

    /// <summary>The next "key=value" pair of a map body, skipping entries that are not pairs (a map
    /// is allowed to carry junk). The key comes back trimmed, the value verbatim.</summary>
    private static bool NextMapPair(ref ReadOnlySpan<char> rest, out ReadOnlySpan<char> key, out ReadOnlySpan<char> to)
    {
        while (!rest.IsEmpty)
        {
            var comma = rest.IndexOf(',');
            var pair = comma < 0 ? rest : rest[..comma];
            rest = comma < 0 ? [] : rest[(comma + 1)..];
            // "<=0.3=x": the operator's own '=' is not the pair's.
            var lead = pair.Length - pair.TrimStart().Length;
            var skip = pair.Length > lead + 1 && pair[lead] is '<' or '>' && pair[lead + 1] == '=' ? lead + 2 : 0;
            var eq = pair[skip..].IndexOf('=');
            if (eq < 0) continue;
            eq += skip;
            key = pair[..eq].Trim();
            to = pair[(eq + 1)..];
            return true;
        }
        key = to = default;
        return false;
    }

    /// <summary>A comparison key: "&lt;0.3", "&lt;=0.3", "&gt;0.7", "&gt;=0.7" against the value as a number.
    /// Entries are tried in order, so "?&lt;0.3=a,&lt;0.7=b,*=c" is three bands. A value that is not a
    /// number matches no comparison.</summary>
    private static bool Holds(ReadOnlySpan<char> key, string text)
    {
        var inclusive = key.Length > 1 && key[1] == '=';
        var inv = CultureInfo.InvariantCulture;
        if (!double.TryParse(key[(inclusive ? 2 : 1)..], NumberStyles.Float, inv, out var bound)
            || !double.TryParse(text, NumberStyles.Float, inv, out var x)) return false;
        return key[0] == '<' ? (inclusive ? x <= bound : x < bound) : (inclusive ? x >= bound : x > bound);
    }

    /// <summary>"~0=12,1=48": a blend. The value, as a number, is placed between the two stops
    /// either side of it and the result interpolated: numbers as numbers, colours per ARGB channel,
    /// anything else steps at the midpoint. Below the first stop or above the last, that stop.</summary>
    public static bool IsBlend(string? format) => format is not null && format.StartsWith('~') && format.Contains('=');

    private static string Blend(string format, string text)
    {
        var inv = CultureInfo.InvariantCulture;
        if (!double.TryParse(text, NumberStyles.Float, inv, out var x)) return "";
        var stops = new List<(double At, string To)>();
        foreach (var pair in format[1..].Split(','))
        {
            var eq = pair.IndexOf('=');
            if (eq > 0 && double.TryParse(pair[..eq].Trim(), NumberStyles.Float, inv, out var at)) stops.Add((at, pair[(eq + 1)..].Trim()));
        }
        if (stops.Count == 0) return "";
        stops.Sort((a, b) => a.At.CompareTo(b.At));
        if (x <= stops[0].At) return stops[0].To;
        if (x >= stops[^1].At) return stops[^1].To;
        var i = stops.FindIndex(s => s.At > x);
        var (a0, v0) = stops[i - 1];
        var (a1, v1) = stops[i];
        var t = (x - a0) / (a1 - a0);
        if (double.TryParse(v0, NumberStyles.Float, inv, out var n0) && double.TryParse(v1, NumberStyles.Float, inv, out var n1))
            return (n0 + (n1 - n0) * t).ToString("0.###", inv);
        try
        {
            var c0 = Render.Color.Parse(v0);
            var c1 = Render.Color.Parse(v1);
            byte L(byte p, byte q) => (byte)Math.Round(p + (q - p) * t);
            return new Render.Color(L(c0.A, c1.A), L(c0.R, c1.R), L(c0.G, c1.G), L(c0.B, c1.B)).ToHex();
        }
        catch (FormatException) { return t < 0.5 ? v0 : v1; }
    }

    /// <summary>The argument for a composite format. A JSON API is free to return a number as a
    /// string ("amount": "64394.01" from Coinbase), and `| "{0:N0}"` then printed the raw text and
    /// drew 64632.235 on the wallpaper. When the author wrote a *specifier* they asked for a
    /// number, so a text that parses as one under the invariant culture is handed over as a double.
    /// Narrow on purpose: bare "{0}" still formats the original text, so "007" and "1.10" survive.
    /// NumberStyles.Float excludes thousands separators, so "1234,6" is not silently read as 12346
    /// on its way to a machine whose locale would have meant something else by the comma.</summary>
    private object NumericIfAsked(string format)
        => this is TextValue t && format.Contains("{0:")
           && double.TryParse(t.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? d
            : Raw();

    /// <summary>The CLR object for composite formatting.</summary>
    public object Raw() => this switch
    {
        TextValue t => t.Text,
        NumberValue n => n.Number,
        TimeValue t => t.Time,
        BoolValue b => b.Flag,
        ImageValue i => i.Path,
        _ => ToText(null),
    };
}

public sealed record TextValue(string Text) : Value;
public sealed record NumberValue(double Number) : Value;
public sealed record TimeValue(DateTimeOffset Time) : Value;
public sealed record BoolValue(bool Flag) : Value;

/// <summary>Local path or http(s) URL. The render path only ever opens local paths; the
/// image cache (Phase 4) rewrites URLs to cache paths before rendering.</summary>
public sealed record ImageValue(string Path) : Value;

public sealed record RecordValue(IReadOnlyDictionary<string, Value> Fields) : Value
{
    public Value? Get(string name) => Fields.TryGetValue(name, out var v) ? v : null;
}

/// <summary>Ordered records. <paramref name="KeyField"/> names the field used by [key] lookups.</summary>
public sealed record ListValue(IReadOnlyList<RecordValue> Items, string? KeyField) : Value
{
    public RecordValue? ByKey(string key)
    {
        if (KeyField is null) return null;
        // Compare by the value's text so numeric keys (Steam appids) match [620] lookups too.
        foreach (var r in Items)
            if (r.Get(KeyField) is { } kv && string.Equals(kv.ToText(null), key, StringComparison.OrdinalIgnoreCase)) return r;
        return null;
    }
}
