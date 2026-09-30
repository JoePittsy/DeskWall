using System.Globalization;
using DeskWall.Core.Bindings;
using DeskWall.Core.Layout;
using DeskWall.Core.Values;

namespace DeskWall.Core.Resolve;

/// <summary>Turns a PropertyValue into a typed value against a scope record (the tree, or a
/// repeater item). Null means "binding did not resolve"; the caller picks the fallback.</summary>
public static class PropertyReader
{
    public static string? Text(PropertyValue p, RecordValue scope)
        => p.IsBound ? BindingResolver.ResolveText(p.Binding!, scope) : p.LiteralText;

    /// <summary>NaN and the infinities count as "did not resolve": every caller clamps or scales
    /// the result, and NaN passes straight through Math.Clamp into Direct2D.</summary>
    public static double? Number(PropertyValue p, RecordValue scope)
        => RawNumber(p, scope) is { } n && double.IsFinite(n) ? n : null;

    private static double? RawNumber(PropertyValue p, RecordValue scope)
    {
        // A map or a blend turns the value into another one ("?<0.9=400,*=700", "~0=12,1=48"), so
        // its text is the number. Any other format ("N0") only prettifies, and is ignored as before.
        if (p.IsBound && p.Binding!.Format is { } f && (f.StartsWith('?') || Value.IsBlend(f)))
            return double.TryParse(BindingResolver.ResolveText(p.Binding, scope), NumberStyles.Float, CultureInfo.InvariantCulture, out var m) ? m : null;
        if (p.IsBound)
            return BindingResolver.Resolve(p.Binding!, scope) switch
            {
                NumberValue n => n.Number,
                TextValue t when double.TryParse(t.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
                BoolValue b => b.Flag ? 1 : 0,
                _ => null,
            };
        return double.TryParse(p.LiteralText, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    public static Render.Color? Color(PropertyValue p, RecordValue scope)
    {
        var s = Text(p, scope);
        if (s is null) return null;
        try { return Render.Color.Parse(s); } catch (FormatException) { return null; }
    }

    public static T? Enum<T>(PropertyValue p, RecordValue scope) where T : struct, System.Enum
    {
        var s = Text(p, scope);
        return s is not null && System.Enum.TryParse<T>(s, ignoreCase: true, out var e) ? e : null;
    }
}
