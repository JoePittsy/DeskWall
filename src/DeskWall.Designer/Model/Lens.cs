using System.IO;
using System.Text.RegularExpressions;
using DeskWall.Core;
using DeskWall.Core.Bindings;
using DeskWall.Core.Layout;
using DeskWall.Core.Widgets;

namespace DeskWall.Designer.Model;

/// <summary>The one edit path for all three depths (plan Task 2.4). <see cref="Project"/> gives
/// the parts the canvas and properties panel edit at the model's depth, as an ordinary
/// <see cref="LayoutFile"/>; the model's own Move/Scale/SetRect/Resize code mutates that
/// (<see cref="DesignerModel.EditAtDepth"/>); <see cref="Commit"/> turns the result back into the
/// document: overrides at copy depth, the overlay template at widget depth.
/// <para>Projected part ids are <c>"&lt;copyId&gt;.&lt;partId&gt;"</c>, the same as the expanded
/// ids the canvas hit-tests, and rects are absolute; a widget depth with no copy has bare ids at
/// (0, 0).</para></summary>
public static partial class Lens
{
    /// <summary>A new widget from <see cref="NewWidget"/>: the size the plan gives an empty frame.</summary>
    public const int NewWidth = 172, NewHeight = 40;

    public static LayoutFile Project(DesignerModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var depth = model.Depth;
        switch (depth.Kind)
        {
            case DepthKind.Copy when FindCopy(model.Layout, depth.CopyId) is { } copy && Template(model, copy.Widget) is { } template:
            {
                var parts = CopyParts(template, copy);
                Place(parts, copy.Id + ".", copy.X, copy.Y, copy.Z);
                return parts;
            }
            case DepthKind.Widget when depth.WidgetKey is { } key && Template(model, key) is { } template:
            {
                var parts = TemplateParts(template);
                if (FindCopy(model.Layout, depth.CopyId) is { } origin) Place(parts, origin.Id + ".", origin.X, origin.Y, origin.Z);
                return parts;
            }
            case DepthKind.Layout:
                return LayoutParts(model);
            default:
                return new LayoutFile { BaseImage = "" };   // the copy or widget went; PruneDepth climbs out
        }
    }

    /// <summary>Write <paramref name="after"/> (an edited <see cref="Project"/>) back as ONE undo
    /// entry, or none when it equals <paramref name="before"/>.
    /// <list type="bullet">
    /// <item>Copy depth: the copy's overrides become <see cref="Overrides.Diff"/> against the
    /// knob-applied baseline, so a value put back to the baseline loses its override. Overrides the
    /// diff cannot see are kept: orphans (plan D1: never deleted silently) and those on a part the
    /// copy hides.</item>
    /// <item>Widget depth: the parts and sources, minus the origin, become the overlay template.</item>
    /// </list></summary>
    public static void Commit(DesignerModel model, LayoutFile before, LayoutFile after, string label = "Edit")
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (before.ToJson() == after.ToJson()) return;
        var depth = model.Depth;
        switch (depth.Kind)
        {
            case DepthKind.Layout:
                CommitLayout(model, before, after, label);
                return;
            case DepthKind.Copy when FindCopy(model.Layout, depth.CopyId) is { } copy && Template(model, copy.Widget) is { } template:
            {
                var local = Clone(after);
                Unplace(local, copy.Id + ".", copy.X, copy.Y, copy.Z);
                var overrides = CopyOverrides(template, copy, local);
                model.Edit(label, l => FindCopy(l, copy.Id)!.Overrides = overrides);
                return;
            }
            case DepthKind.Widget when depth.WidgetKey is { } key && Template(model, key) is { } template:
            {
                var local = Clone(after);
                if (FindCopy(model.Layout, depth.CopyId) is { } origin) Unplace(local, origin.Id + ".", origin.X, origin.Y, origin.Z);
                var edited = With(template, local.Components, local.Sources);
                model.Edit(label, (_, edits) => edits[key] = edited);
                return;
            }
        }
    }

    /// <summary>Delete one override: the part goes back to the widget's value.</summary>
    public static void Reset(DesignerModel model, string copyId, string key)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (FindCopy(model.Layout, copyId) is not { } copy || !copy.Overrides.ContainsKey(key)) return;
        model.Edit("Reset", l => FindCopy(l, copyId)!.Overrides.Remove(key));
    }

    /// <summary>Move one override's value into the widget (the overlay template), then delete the
    /// override: every copy without its own override of <paramref name="key"/> follows. False when
    /// there is no such override, or it names nothing the widget has.</summary>
    public static bool PushToWidget(DesignerModel model, string copyId, string key)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (FindCopy(model.Layout, copyId) is not { } copy || !copy.Overrides.TryGetValue(key, out var value)) return false;
        if (Template(model, copy.Widget) is not { } template) return false;
        var parts = TemplateParts(template);
        if (!KnobSets.Apply(parts, key, value)) return false;
        var edited = With(template, parts.Components, parts.Sources);
        model.Edit("Push to widget", (l, edits) =>
        {
            edits[template.Key] = edited;
            FindCopy(l, copyId)!.Overrides.Remove(key);
        });
        return true;
    }

    /// <summary>Turn loose components into a new widget and put a copy of it where they were, as ONE
    /// edit: a new key (never one a user or shipped file, the overlay or a copy already uses), the
    /// parts made relative to their bounds, the layout sources they bind copied into the widget.
    /// Returns the new copy's id, or null when none of <paramref name="componentIds"/> is loose.</summary>
    public static string? MakeWidget(DesignerModel model, IReadOnlyList<string> componentIds)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(componentIds);
        var picked = model.Layout.Components.Where(c => componentIds.Contains(c.Id)).ToList();
        if (picked.Count == 0) return null;

        var parts = Clone(new LayoutFile { BaseImage = "", Components = picked }).Components;
        int x = parts.Min(c => c.Rect.X), y = parts.Min(c => c.Rect.Y);
        int w = parts.Max(c => c.Rect.Right) - x, h = parts.Max(c => c.Rect.Bottom) - y;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in parts)
        {
            c.Rect = c.Rect.Offset(-x, -y);
            c.Widget = null;
            c.Id = PartId(ids, c.Id);
            if (c is RepeaterDef r) foreach (var t in r.Template) t.Id = PartId(ids, t.Id);
        }
        var bound = parts.SelectMany(c => ComponentProperties.For(c).Select(p => p.Get(c)))
            .Where(v => v.IsBound && v.Binding!.Path is [NameSegment, ..])
            .Select(v => ((NameSegment)v.Binding!.Path[0]).Name).ToHashSet(StringComparer.Ordinal);
        var sources = Clone(new LayoutFile { BaseImage = "", Sources = model.Layout.Sources.Where(s => bound.Contains(s.Name)).ToList() }).Sources;

        var key = FreeKey(model);
        var template = new WidgetTemplate
        {
            Name = "New widget", Key = key, Description = $"Made from {parts.Count} part{(parts.Count == 1 ? "" : "s")}.",
            Width = Math.Max(1, w), Height = Math.Max(1, h), Sources = sources, Components = parts,
        };
        var copyId = FreeCopyId(model.Layout, key);
        var set = picked.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        model.Edit("Make widget", (l, edits) =>
        {
            edits[key] = template;
            AddCopy(l, new WidgetCopy { Id = copyId, Widget = key, X = x, Y = y });
            l.Components.RemoveAll(c => set.Contains(c.Id));
        });
        return copyId;
    }

    /// <summary>An empty <see cref="NewWidth"/>x<see cref="NewHeight"/> widget under a new key, a copy
    /// of it at (<paramref name="x"/>, <paramref name="y"/>), one edit; then widget depth on it.
    /// Returns the copy's id.</summary>
    public static string NewWidget(DesignerModel model, int x, int y)
    {
        ArgumentNullException.ThrowIfNull(model);
        var key = FreeKey(model);
        var template = new WidgetTemplate { Name = "New widget", Key = key, Description = "A new widget.", Width = NewWidth, Height = NewHeight };
        var copyId = FreeCopyId(model.Layout, key);
        model.Edit("New widget", (l, edits) =>
        {
            edits[key] = template;
            AddCopy(l, new WidgetCopy { Id = copyId, Widget = key, X = x, Y = y });
        });
        model.SetDepth(Depth.Widget(key, copyId));
        return copyId;
    }

    // ---- helpers ------------------------------------------------------------------------------

    private static WidgetCopy? FindCopy(LayoutFile layout, string? copyId)
        => copyId is null ? null : layout.Copies?.Find(c => c.Id == copyId);

    private static WidgetTemplate? Template(DesignerModel model, string key) => TryTemplate(model.Finder(), key);

    private static WidgetTemplate? TryTemplate(Func<string, WidgetTemplate?> find, string key)
    {
        try { return find(key); }
#pragma warning disable CA1031 // a widget that fails to load has nothing to project; the canvas shows the broken link
        catch (Exception) { return null; }
#pragma warning restore CA1031
    }

    private static LayoutFile Clone(LayoutFile l) => LayoutFile.Parse(l.ToJson());

    /// <summary>The template's own parts and sources, placeholders and all: what widget depth edits.</summary>
    private static LayoutFile TemplateParts(WidgetTemplate t)
        => Clone(new LayoutFile { BaseImage = "", Sources = [.. t.Sources], Components = [.. t.Components] });

    /// <summary>The copy's parts in template-local terms: the knob-applied baseline plus its
    /// overrides, in the expander's order (hidden last).</summary>
    private static LayoutFile CopyParts(WidgetTemplate template, WidgetCopy copy)
    {
        var parts = WidgetExpander.Baseline(template, copy.Knobs);
        foreach (var (key, value) in copy.Overrides.OrderBy(o => IsHidden(o.Key))) KnobSets.Apply(parts, key, value);
        return parts;
    }

    /// <summary>Layout depth, for a layout with copies: the layout, plus every copy's parts (knobs and
    /// overrides applied) placed at its origin as copy depth places them, with <c>Widget</c> set to
    /// the copy's id. So the model's Move, Scale, SetRect and Remove reach a copy's parts unchanged,
    /// and <see cref="CommitLayout"/> turns them back into the copy's position and overrides. A copy
    /// whose widget is missing has no parts here; it is still in <c>Copies</c>, which the model's
    /// verbs move and remove directly.</summary>
    private static LayoutFile LayoutParts(DesignerModel model)
    {
        var parts = Clone(model.Layout);
        if (parts.Copies is not { Count: > 0 } copies) return parts;
        var find = model.Finder();
        foreach (var copy in copies)
        {
            if (TryTemplate(find, copy.Widget) is not { } template) continue;
            var own = CopyParts(template, copy);
            Place(own, copy.Id + ".", copy.X, copy.Y, copy.Z);
            foreach (var c in own.Components) { c.Widget = copy.Id; parts.Components.Add(c); }
        }
        return parts;
    }

    /// <summary>The inverse of <see cref="LayoutParts"/>: loose components back into the layout, and
    /// for each copy its (possibly moved) origin from <c>after.Copies</c> and, only when its parts
    /// changed relative to that origin, new overrides against the knob-applied baseline. A pure move
    /// therefore changes <c>x</c>/<c>y</c> and nothing else; a resize writes rect (and, on a corner
    /// drag, size) overrides; a copy removed from <c>Copies</c> takes its parts with it.</summary>
    private static void CommitLayout(DesignerModel model, LayoutFile before, LayoutFile after, string label)
    {
        var copyIds = (model.Layout.Copies ?? []).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var loose = model.Layout.Components.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        bool PartOf(ComponentDef c, string copyId) => !loose.Contains(c.Id) && c.Widget == copyId;
        bool IsPart(ComponentDef c) => !loose.Contains(c.Id) && c.Widget is { } w && copyIds.Contains(w);

        var copies = after.Copies;
        if (copies is not null)
        {
            var find = model.Finder();
            foreach (var copy in copies)
            {
                if (FindCopy(model.Layout, copy.Id) is not { } was || TryTemplate(find, copy.Widget) is not { } template) continue;
                var sources = CopyParts(template, was).Sources;
                var then = Local(before.Components.Where(c => PartOf(c, copy.Id)), was, sources);
                var now = Local(after.Components.Where(c => PartOf(c, copy.Id)), copy, sources);
                if (then.ToJson() != now.ToJson()) copy.Overrides = CopyOverrides(template, was, now);
            }
        }
        var components = after.Components.Where(c => !IsPart(c)).ToList();
        model.Edit(label, l => { l.Components = components; l.Sources = after.Sources; l.Copies = copies; });
    }

    /// <summary>A copy's placed parts in template-local terms, with the copy's sources beside them
    /// so <see cref="Overrides.Diff"/> keeps its source overrides.</summary>
    private static LayoutFile Local(IEnumerable<ComponentDef> parts, WidgetCopy at, List<SourceDef> sources)
    {
        var local = Clone(new LayoutFile { BaseImage = "", Components = parts.ToList(), Sources = sources });
        foreach (var c in local.Components) c.Widget = null;
        Unplace(local, at.Id + ".", at.X, at.Y, at.Z);
        return local;
    }

    /// <summary>The overrides that give <paramref name="local"/>: the diff against the baseline, plus
    /// the existing ones it cannot regenerate (<see cref="Unseen"/>).</summary>
    private static Dictionary<string, PropertyValue> CopyOverrides(WidgetTemplate template, WidgetCopy copy, LayoutFile local)
    {
        var overrides = Overrides.Diff(WidgetExpander.Baseline(template, copy.Knobs), local);
        foreach (var (key, value) in Unseen(template, copy, overrides)) overrides.TryAdd(key, value);
        return overrides;
    }

    /// <summary>The copy's overrides that <see cref="Overrides.Diff"/> cannot regenerate: those that
    /// apply to nothing (orphans), and those on a part (or repeater child) the new overrides hide.</summary>
    private static IEnumerable<KeyValuePair<string, PropertyValue>> Unseen(WidgetTemplate template, WidgetCopy copy, Dictionary<string, PropertyValue> now)
    {
        var scratch = WidgetExpander.Baseline(template, copy.Knobs);
        foreach (var o in copy.Overrides.OrderBy(o => IsHidden(o.Key)))
        {
            var orphan = !KnobSets.Apply(scratch, o.Key, o.Value);
            var s = o.Key.Split('.');
            var onHidden = !IsHidden(o.Key) && s[0] == "components" && s.Length >= 3
                && (now.ContainsKey($"components.{s[1]}.{ComponentProperties.Hidden}")
                    || (s.Length == 4 && now.ContainsKey($"components.{s[1]}.{s[2]}.{ComponentProperties.Hidden}")));
            if (orphan || onHidden) yield return o;
        }
    }

    private static bool IsHidden(string key) => key.EndsWith("." + ComponentProperties.Hidden, StringComparison.OrdinalIgnoreCase);

    private static void Place(LayoutFile parts, string prefix, int dx, int dy, int dz)
    {
        foreach (var c in parts.Components) { c.Id = prefix + c.Id; c.Rect = c.Rect.Offset(dx, dy); c.Z += dz; }
    }

    /// <summary>The inverse of <see cref="Place"/>. A part added on the canvas may not carry the
    /// prefix; its id is kept as it is.</summary>
    private static void Unplace(LayoutFile parts, string prefix, int dx, int dy, int dz)
    {
        foreach (var c in parts.Components)
        {
            if (c.Id.StartsWith(prefix, StringComparison.Ordinal)) c.Id = c.Id[prefix.Length..];
            c.Rect = c.Rect.Offset(-dx, -dy);
            c.Z -= dz;
        }
    }

    private static WidgetTemplate With(WidgetTemplate t, List<ComponentDef> components, List<SourceDef> sources) => new()
    {
        Name = t.Name, Key = t.Key, Path = t.Path, Description = t.Description, Width = t.Width, Height = t.Height,
        Anchor = t.Anchor, Requires = t.Requires, Knobs = t.Knobs, Sources = sources, Components = components,
    };

    private static void AddCopy(LayoutFile l, WidgetCopy copy)
    {
        (l.Copies ??= []).Add(copy);
        l.Version = Math.Max(l.Version, 2);
    }

    /// <summary>"widget", "widget-2", ...: the first key no widget file (user or shipped, loadable or
    /// not), overlay entry or copy in this layout already uses. A copy pointing at a missing key
    /// would otherwise silently link to the new widget.</summary>
    private static string FreeKey(DesignerModel model)
    {
        for (var n = 1; ; n++)
        {
            var key = n == 1 ? "widget" : $"widget-{n}";
            if (model.WidgetEdits.ContainsKey(key)) continue;
            if (File.Exists(Path.Combine(WidgetCatalog.UserDir, key + ".json")) || File.Exists(Path.Combine(WidgetCatalog.ShippedDir, key + ".json"))) continue;
            if (model.Layout.Copies?.Exists(c => string.Equals(c.Widget, key, StringComparison.OrdinalIgnoreCase)) == true) continue;
            return key;
        }
    }

    private static string FreeCopyId(LayoutFile layout, string key)
    {
        for (var n = 1; ; n++)
            if (layout.Copies?.Exists(c => c.Id == $"{key}-{n}") != true) return $"{key}-{n}";
    }

    [GeneratedRegex("[^A-Za-z0-9_-]")]
    private static partial Regex NotPartIdChar();

    /// <summary>A layout id as a valid, unique part id (plan D1: no dots, not starting with a digit).</summary>
    private static string PartId(HashSet<string> taken, string id)
    {
        var baseId = NotPartIdChar().Replace(id, "-");
        if (baseId.Length == 0 || !(char.IsAsciiLetter(baseId[0]) || baseId[0] == '_')) baseId = "_" + baseId;
        var result = baseId;
        for (var n = 2; !taken.Add(result); n++) result = $"{baseId}-{n}";
        return result;
    }
}
