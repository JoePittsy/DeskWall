using System.Globalization;

namespace DeskWall.Core.Values;

/// <summary>A value published by a source. Closed hierarchy; pattern-match on it.</summary>
public abstract record Value
{
    /// <summary>Render as text. <paramref name="format"/> is a .NET format string for the
    /// value's type, or a composite format containing {0}, or null for the default.
    /// A malformed format string (an argument index the value does not supply, an unbalanced
    /// brace, an unknown type specifier) falls back to the unformatted text rather than throwing:
    /// a user-authored layout must never abort the tick. Spec 3.2 / plan Task 6.</summary>
    public string ToText(string? format)
    {
        var inv = CultureInfo.InvariantCulture;
        try
        {
            if (format is not null && format.Contains("{0"))
                return string.Format(inv, format, Raw());
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
        foreach (var r in Items)
            if (r.Get(KeyField) is TextValue t && string.Equals(t.Text, key, StringComparison.OrdinalIgnoreCase)) return r;
        return null;
    }
}
