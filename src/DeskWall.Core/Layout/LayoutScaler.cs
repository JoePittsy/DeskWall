using System.Globalization;
using DeskWall.Core.Display;

namespace DeskWall.Core.Layout;

public static class LayoutScaler
{
    /// <summary>Scale every rect from one canvas to another. Repeater templates scale too.
    /// Text sizes and gaps scale by the geometric mean of sx, sy. Widths of images keep their aspect
    /// (the repeater's auto cell height handles the rest).</summary>
    public static LayoutFile Scale(LayoutFile source, DisplaySignature from, DisplaySignature to)
    {
        var sx = (double)to.Width / from.Width;
        var sy = (double)to.Height / from.Height;
        var sm = Math.Sqrt(sx * sy);
        // Deep copy through JSON so the source is untouched; the source generator makes this AOT-safe.
        var copy = LayoutFile.Parse(source.ToJson());
        foreach (var c in copy.Components) ScaleComponent(c, sx, sy, sm);
        return copy;
    }

    private static void ScaleComponent(ComponentDef c, double sx, double sy, double sm)
    {
        c.Rect = c.Rect.Scale(sx, sy);
        switch (c)
        {
            case TextDef t:
                ScaleLiteral(t, nameof(TextDef.Size), sm);
                ScaleLiteral(t, nameof(TextDef.EffectRadius), sm);
                break;
            case ImageDef i:
                ScaleLiteral(i, nameof(ImageDef.Radius), sm);
                break;
            case DialDef dl:
                // Not sm: the arc's radius is taken from the short side of the rect, so a stroke
                // scaled by the geometric mean would eat a dial whose width halved while its
                // height doubled (sm = 1 there). The smaller factor keeps the ring proportional.
                ScaleLiteral(dl, nameof(DialDef.Thickness), Math.Min(sx, sy));
                break;
            case RepeaterDef r:
                r.Gap = (int)Math.Round(r.Gap * sm);
                if (!r.CellHeight.IsBound && !string.Equals(r.CellHeight.LiteralText, "auto", StringComparison.OrdinalIgnoreCase))
                    r.CellHeight = ScaleNumber(r.CellHeight, r.Axis == Axis.Vertical ? sy : sx);
                foreach (var child in r.Template) ScaleComponent(child, sx, sy, sm);
                break;
        }
    }

    private static void ScaleLiteral(ComponentDef c, string property, double factor)
    {
        // Only the few numeric literals we know about; keep it explicit for AOT (no reflection).
        switch (c, property)
        {
            case (TextDef t, nameof(TextDef.Size)): t.Size = ScaleNumber(t.Size, factor); break;
            case (TextDef t, nameof(TextDef.EffectRadius)): t.EffectRadius = ScaleNumber(t.EffectRadius, factor); break;
            case (ImageDef i, nameof(ImageDef.Radius)): i.Radius = ScaleNumber(i.Radius, factor); break;
            case (DialDef dl, nameof(DialDef.Thickness)): dl.Thickness = ScaleNumber(dl.Thickness, factor); break;
        }
    }

    private static PropertyValue ScaleNumber(PropertyValue p, double factor)
    {
        if (p.IsBound || !double.TryParse(p.LiteralText, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return p;
        return PropertyValue.Literal(Math.Round(v * factor));
    }
}
