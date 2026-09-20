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

    public static double? Number(PropertyValue p, RecordValue scope)
    {
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
