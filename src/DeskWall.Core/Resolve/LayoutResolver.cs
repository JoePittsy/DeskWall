using DeskWall.Core.Bindings;
using DeskWall.Core.Layout;
using DeskWall.Core.Render;
using DeskWall.Core.Values;

namespace DeskWall.Core.Resolve;

public static class LayoutResolver
{
    /// <summary>Expand a layout against the current value tree into concrete components with
    /// absolute rects and content keys. <paramref name="canvas"/> is the monitor's canvas.</summary>
    public static IReadOnlyList<Resolved> Resolve(LayoutFile layout, RecordValue tree, Rect canvas)
    {
        var result = new List<Resolved>();
        foreach (var def in layout.Components) Emit(def, tree, def.Rect, def.Id, 0, result);
        for (var i = 0; i < result.Count; i++) result[i] = result[i] with { ContentKey = ContentKey.Of(result[i]) };
        return result;
    }

    /// <param name="scope">record bindings resolve against (the tree, or a repeater item)</param>
    /// <param name="rect">absolute rect for this instance</param>
    private static void Emit(ComponentDef def, RecordValue scope, Rect rect, string id, int slotOffset, List<Resolved> result)
    {
        switch (def)
        {
            case TextDef t:
                result.Add(new ResolvedText(id, rect, def.Z, PropertyReader.Text(t.Text, scope) ?? "", new TextStyle(
                    Font: PropertyReader.Text(t.Font, scope) ?? "Segoe UI",
                    Size: (float)(PropertyReader.Number(t.Size, scope) ?? 16),
                    Weight: (int)(PropertyReader.Number(t.Weight, scope) ?? 400),
                    Color: PropertyReader.Color(t.Color, scope) ?? Color.White,
                    Align: PropertyReader.Enum<Align>(t.Align, scope) ?? Align.Left,
                    Effect: PropertyReader.Enum<TextEffect>(t.Effect, scope) ?? TextEffect.Shadow,
                    EffectRadius: (float)(PropertyReader.Number(t.EffectRadius, scope) ?? 6),
                    EffectColor: PropertyReader.Color(t.EffectColor, scope) ?? new Color(160, 0, 0, 0))));
                break;

            case ImageDef i:
                result.Add(new ResolvedImage(id, rect, def.Z, PropertyReader.Text(i.Source, scope) ?? "",
                    PropertyReader.Enum<Fit>(i.Fit, scope) ?? Fit.Cover,
                    (float)(PropertyReader.Number(i.Radius, scope) ?? 0),
                    (float)(PropertyReader.Number(i.Opacity, scope) ?? 1)));
                break;

            case BarDef b:
                var frac = PropertyReader.Number(b.Fraction, scope) ?? 0;
                var threshold = PropertyReader.Number(b.Threshold, scope) ?? 1;
                var fill = frac >= threshold
                    ? PropertyReader.Color(b.ThresholdFill, scope) ?? Color.Parse("#FFD13438")
                    : PropertyReader.Color(b.Fill, scope) ?? Color.Parse("#EBFFFFFF");
                result.Add(new ResolvedBar(id, rect, def.Z, frac,
                    PropertyReader.Color(b.Track, scope) ?? Color.Parse("#46FFFFFF"), fill,
                    PropertyReader.Enum<Axis>(b.Direction, scope) ?? Axis.Horizontal));
                break;

            case ShortcutDef s:
                var target = PropertyReader.Text(s.Target, scope);
                if (string.IsNullOrWhiteSpace(target)) break;   // nothing to launch: no icon
                result.Add(new ResolvedShortcut(id, rect, def.Z, target, PropertyReader.Text(s.Tooltip, scope) ?? "", s.Slot + slotOffset));
                break;

            case RepeaterDef r:
                var itemsBinding = r.Items.Binding ?? throw new InvalidOperationException($"repeater '{id}' items must be a binding");
                if (BindingResolver.Resolve(itemsBinding, scope) is not ListValue list) break;
                var vertical = r.Axis == Axis.Vertical;
                var limit = vertical ? rect.H : rect.W;
                var cursor = 0;
                for (var idx = 0; idx < list.Items.Count; idx++)
                {
                    var item = list.Items[idx];
                    var cell = CellExtent(r, item);
                    if (cursor + cell > limit) break;   // never overflow the block
                    var origin = vertical ? rect.Offset(0, cursor) : rect.Offset(cursor, 0);
                    foreach (var child in r.Template)
                    {
                        var childRect = child.Rect.Offset(origin.X, origin.Y);
                        if (child is ImageDef && IsAuto(r.CellHeight)) childRect = vertical ? childRect with { H = cell } : childRect with { W = cell };
                        Emit(child, item, childRect, $"{id}[{idx}].{child.Id}", slotOffset + idx, result);
                    }
                    cursor += cell + r.Gap;
                }
                break;
        }
    }

    private static bool IsAuto(PropertyValue p) => !p.IsBound && string.Equals(p.LiteralText, "auto", StringComparison.OrdinalIgnoreCase);

    /// <summary>Cell extent along the axis: the literal number, or for "auto" the first image
    /// child's aspect ratio applied to its template width (or height for horizontal).</summary>
    private static int CellExtent(RepeaterDef r, RecordValue item)
    {
        if (!IsAuto(r.CellHeight)) return (int)Math.Round(PropertyReader.Number(r.CellHeight, item) ?? 0);
        var img = r.Template.OfType<ImageDef>().FirstOrDefault();
        if (img is null) return r.Template.Count == 0 ? 0 : r.Template.Max(c => r.Axis == Axis.Vertical ? c.Rect.Bottom : c.Rect.Right);
        var path = PropertyReader.Text(img.Source, item);
        var vertical = r.Axis == Axis.Vertical;
        if (path is null || !File.Exists(path))
            return vertical ? (int)Math.Round(img.Rect.W * 1.5) : (int)Math.Round(img.Rect.H / 1.5);   // 2:3 placeholder, as the POC did
        using var s = Surface.Load(path);
        return vertical
            ? (int)Math.Round((double)img.Rect.W * s.Height / s.Width)
            : (int)Math.Round((double)img.Rect.H * s.Width / s.Height);
    }
}
