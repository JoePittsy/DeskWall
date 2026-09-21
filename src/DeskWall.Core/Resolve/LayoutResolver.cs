using DeskWall.Core.Bindings;
using DeskWall.Core.Layout;
using DeskWall.Core.Render;
using DeskWall.Core.Values;

namespace DeskWall.Core.Resolve;

public static class LayoutResolver
{
    /// <summary>Expand a layout against the current value tree into concrete components with
    /// absolute rects and content keys.
    /// <paramref name="remote"/>, when given, maps a remote image URL to a local file (or null on a
    /// miss) for the repeater's "auto" cell-height measurement; TickRunner passes images.Lookup.
    /// <para>Finding 16: there is deliberately no canvas parameter. Layout rects are absolute, in the
    /// layout's own coordinate space; fitting them to a different display is LayoutScaler's job and
    /// happens before resolution. The old canvas argument was never read, and clipping to it would
    /// have silently dropped a component the designer can still see.</para></summary>
    public static IReadOnlyList<Resolved> Resolve(LayoutFile layout, RecordValue tree, Func<string, string?>? remote = null)
    {
        var result = new List<Resolved>();
        foreach (var def in layout.Components) Emit(def, tree, def.Rect, def.Id, 0, result, remote);

        // Finding 6: a duplicate id must fail here, before any drawing or state is touched -
        // TickRunner later builds a Dictionary<string, ...> keyed by Id and must never reach it
        // with a duplicate (ToDictionary throws mid-tick, after the wallpaper may already be applied).
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in result)
            if (!seen.Add(c.Id))
                throw new InvalidOperationException($"duplicate component id '{c.Id}'");

        for (var i = 0; i < result.Count; i++) result[i] = result[i] with { ContentKey = ContentKey.Of(result[i]) };
        return result;
    }

    /// <param name="scope">record bindings resolve against (the tree, or a repeater item)</param>
    /// <param name="rect">absolute rect for this instance</param>
    private static void Emit(ComponentDef def, RecordValue scope, Rect rect, string id, int slotOffset, List<Resolved> result, Func<string, string?>? remote)
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
                result.Add(new ResolvedImage(id, rect, def.Z, ExpandRuntime(PropertyReader.Text(i.Source, scope) ?? ""),
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

            case DialDef dl:
                var dfrac = Math.Clamp(PropertyReader.Number(dl.Fraction, scope) ?? 0, 0, 1);
                var dthr = PropertyReader.Number(dl.Threshold, scope) ?? 1;
                var dfill = dfrac >= dthr
                    ? PropertyReader.Color(dl.ThresholdFill, scope) ?? Color.Parse("#FFD13438")
                    : PropertyReader.Color(dl.Fill, scope) ?? Color.Parse("#EBFFFFFF");
                result.Add(new ResolvedDial(id, rect, def.Z, dfrac,
                    PropertyReader.Color(dl.Track, scope) ?? Color.Parse("#46FFFFFF"), dfill,
                    (float)(PropertyReader.Number(dl.Thickness, scope) ?? 6),
                    (float)(PropertyReader.Number(dl.StartAngle, scope) ?? 225),
                    (float)(PropertyReader.Number(dl.Sweep, scope) ?? 270)));
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
                    var cell = CellExtent(r, item, out var imageExtent, remote);
                    if (cursor + cell > limit) break;   // never overflow the block
                    var origin = vertical ? rect.Offset(0, cursor) : rect.Offset(cursor, 0);
                    foreach (var child in r.Template)
                    {
                        var childRect = child.Rect.Offset(origin.X, origin.Y);
                        if (child is ImageDef && IsAuto(r.CellHeight)) childRect = vertical ? childRect with { H = imageExtent } : childRect with { W = imageExtent };
                        Emit(child, item, ClampToCell(childRect, origin, cell, rect, vertical), $"{id}[{idx}].{child.Id}", slotOffset + idx, result, remote);
                    }
                    cursor += cell + r.Gap;
                }
                break;
        }
    }

    /// <summary>"runtime:assets/weather/61.png" -> %LOCALAPPDATA%\DeskWall\assets\weather\61.png. Lets a
    /// committed starter name a per-user file without a per-user absolute path. Braces are not used
    /// for the token because a composite format string would swallow them.</summary>
    private static string ExpandRuntime(string source)
    {
        const string prefix = "runtime:";
        if (!source.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return source;
        var rest = source[prefix.Length..].Replace('/', '\\').TrimStart('\\');
        return Paths.InRuntime(rest.Split('\\', StringSplitOptions.RemoveEmptyEntries));
    }

    private static bool IsAuto(PropertyValue p) => !p.IsBound && string.Equals(p.LiteralText, "auto", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Cell extent along the axis: the larger of the declared extent (the literal number, or for
    /// "auto" the first image child's aspect ratio applied to its template width / height) and the
    /// furthest template child's Bottom (vertical) or Right (horizontal). Taking only the declared
    /// extent let a child that sits below the image spill into the next item's cell.
    /// </summary>
    /// <param name="imageExtent">the auto-sized extent for the first image child alone, so that
    /// widening the cell to fit a sibling does not stretch the cover out of its aspect ratio.</param>
    private static int CellExtent(RepeaterDef r, RecordValue item, out int imageExtent, Func<string, string?>? remote)
    {
        var vertical = r.Axis == Axis.Vertical;
        imageExtent = AutoImageExtent(r, item, vertical, remote);
        var declared = IsAuto(r.CellHeight) ? imageExtent : (int)Math.Round(PropertyReader.Number(r.CellHeight, item) ?? 0);
        var children = r.Template.Count == 0 ? 0 : r.Template.Max(c => vertical ? c.Rect.Bottom : c.Rect.Right);
        return Math.Max(Math.Max(declared, children), 0);
    }

    /// <summary>The first image child's own extent along the axis, from its aspect ratio. Zero when
    /// the template has no image child. A remote URL is resolved through <paramref name="remote"/>
    /// (the image cache's Lookup) rather than File.Exists; a cache miss falls back to the same 2:3
    /// placeholder as a missing local file, and the landing wake re-resolves with the real aspect.</summary>
    private static int AutoImageExtent(RepeaterDef r, RecordValue item, bool vertical, Func<string, string?>? remote)
    {
        var img = r.Template.OfType<ImageDef>().FirstOrDefault();
        if (img is null) return 0;
        // Expand before the remote check and File.Exists, exactly as the ImageDef case does: a
        // "runtime:" source measured as written never exists, and every cover silently fell back
        // to the 2:3 placeholder.
        var path = PropertyReader.Text(img.Source, item) is { } src ? ExpandRuntime(src) : null;
        if (path is not null && RemoteImageCache.IsRemote(path)) path = remote?.Invoke(path);
        if (path is null || !File.Exists(path))
            return vertical ? (int)Math.Round(img.Rect.W * 1.5) : (int)Math.Round(img.Rect.H / 1.5);   // 2:3 placeholder, as the POC did
        var (w, h) = PixelSize(path);
        return vertical
            ? (int)Math.Round((double)img.Rect.W * h / w)
            : (int)Math.Round((double)img.Rect.H * w / h);
    }

    /// <summary>An image's pixel size, cached by path and mtime.
    /// <para>
    /// Resolve runs before the tick's skip gate, so without this every repeater image was fully
    /// decoded every minute - four covers on a tick that changes nothing - purely to read two
    /// numbers off it. The mtime is part of the key, so a file replaced in place is still re-read;
    /// the cache is bounded by clearing it wholesale, which only a layout cycling through hundreds
    /// of images would ever reach.
    /// </para></summary>
    private static (int Width, int Height) PixelSize(string path)
    {
        var stamp = File.GetLastWriteTimeUtc(path).Ticks;
        if (s_sizes.TryGetValue(path, out var hit) && hit.Stamp == stamp) return (hit.Width, hit.Height);
        using var s = Surface.Load(path);
        if (s_sizes.Count > SizeCacheCap) s_sizes.Clear();
        s_sizes[path] = (stamp, s.Width, s.Height);
        return (s.Width, s.Height);
    }

    private const int SizeCacheCap = 256;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Stamp, int Width, int Height)> s_sizes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Keep a template child inside its cell on the main axis and inside the repeater on the cross
    /// axis. Children are clamped, never dropped: a too-wide child paints a narrower box rather than
    /// painting over whatever sits beside the block, and the resolved rect stays an honest
    /// description of the pixels so the dirty-rect bookkeeping still holds.
    /// </summary>
    private static Rect ClampToCell(Rect child, Rect origin, int cell, Rect block, bool vertical)
    {
        var mainLo = vertical ? origin.Y : origin.X;
        var mainHi = mainLo + cell;
        var crossLo = vertical ? block.X : block.Y;
        var crossHi = vertical ? block.Right : block.Bottom;
        if (crossHi < crossLo) crossHi = crossLo;

        int x0 = child.X, y0 = child.Y, x1 = child.Right, y1 = child.Bottom;
        if (vertical)
        {
            y0 = Math.Clamp(y0, mainLo, mainHi); y1 = Math.Clamp(y1, mainLo, mainHi);
            x0 = Math.Clamp(x0, crossLo, crossHi); x1 = Math.Clamp(x1, crossLo, crossHi);
        }
        else
        {
            x0 = Math.Clamp(x0, mainLo, mainHi); x1 = Math.Clamp(x1, mainLo, mainHi);
            y0 = Math.Clamp(y0, crossLo, crossHi); y1 = Math.Clamp(y1, crossLo, crossHi);
        }
        return new Rect(x0, y0, x1 - x0, y1 - y0);
    }
}
