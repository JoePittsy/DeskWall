using System.IO;
using DeskWall.Core.Layout;
using DeskWall.Core.Widgets;

namespace DeskWall.Designer.Model;

public enum LayerKind
{
    /// <summary>A placed copy of a widget; its children are its parts, then any orphan rows.</summary>
    Copy,
    /// <summary>One part of a copy, as the expansion emits it.</summary>
    Part,
    /// <summary>A loose component (placed by hand, or a v1 stamped component no copy owns).</summary>
    Component,
    /// <summary>A repeater's template child, under its repeater (a part or a component).</summary>
    Child,
    /// <summary>An override or knob on a copy that names something its widget no longer has.</summary>
    Orphan,
}

/// <summary>One row of the Layers panel.</summary>
/// <param name="Name">What the row says: the widget's name for a copy (its key when the widget is
/// missing), the part or component id, or the orphan override key.</param>
/// <param name="Detail">A secondary word: the copy id, or the component type.</param>
/// <param name="SelectId">What <see cref="DesignerModel.Select"/> takes for this row: the copy id, the
/// expanded part id ("&lt;copy&gt;.&lt;part&gt;"), the component id; for a child, its repeater's id
/// (the child itself is reached with <see cref="ChildId"/>); for an orphan, its copy's id.</param>
/// <param name="ChildId">A <see cref="LayerKind.Child"/> row's own id inside its repeater, else null.</param>
/// <param name="HasOverride">A copy with any override; a part or child that one of its copy's
/// override keys names.</param>
/// <param name="IsOrphan">An <see cref="LayerKind.Orphan"/> row, or a copy that has one.</param>
/// <param name="IsBroken">A copy whose widget file is missing or fails to load.</param>
/// <param name="IsForkedShipped">A copy whose widget is the owner's fork of a shipped key.</param>
public sealed record LayerRow(
    LayerKind Kind, string Name, string Detail, string SelectId, string? ChildId,
    bool HasOverride, bool IsOrphan, bool IsBroken, bool IsForkedShipped,
    IReadOnlyList<LayerRow> Children)
{
    /// <summary>Unique within one tree: what the panel keys its rows on across rebuilds.</summary>
    public string Key => Kind switch
    {
        LayerKind.Child => SelectId + "\u0000" + ChildId,
        LayerKind.Orphan => SelectId + "\u0000!" + Name,
        _ => SelectId,
    };
}

/// <summary>The Layers panel's rows, from a layout and its expansion. Pure.
/// <para>Order is paint order reversed: the first row is the frontmost thing, as in Figma, so the
/// list reads top-down the way the eye sees the canvas. Paint order is the renderer's: a stable sort
/// by z over the expansion's component order (<c>FrameRenderer</c>). A copy sits where its
/// frontmost part paints; a copy with no parts (its widget is missing, or every part is hidden) is
/// drawn as a box over the canvas and so lists first. Parts and repeater children are ordered the
/// same way inside their parent.</para></summary>
public static class LayerTree
{
    public static IReadOnlyList<LayerRow> Build(DesignerModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var find = model.Finder();
        // The overlay's templates do not carry OverridesShipped (it is set by the catalog as it
        // loads), so an edited or applied fork still in the overlay is decided by the shipped file.
        bool Forked(string key) =>
            (Copies.TryFind(find, key)?.OverridesShipped ?? false)
            || (model.WidgetEdits.ContainsKey(key) && File.Exists(Path.Combine(WidgetCatalog.ShippedDir, key + ".json")));
        return Build(model.Layout, model.Expanded(), find, Forked);
    }

    public static IReadOnlyList<LayerRow> Build(LayoutFile layout, Expansion expansion, Func<string, WidgetTemplate?> find, Func<string, bool> isForked)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(expansion);
        ArgumentNullException.ThrowIfNull(isForked);
        var components = expansion.Layout.Components;
        var copies = layout.Copies ?? [];

        // (row, z, order): sorted ascending that is paint order, and the list is its reverse.
        var top = new List<(LayerRow Row, int Z, int Order)>();
        for (var i = 0; i < components.Count; i++)
        {
            var c = components[i];
            if (c.Widget is { Length: > 0 } w && copies.Any(k => k.Id == w)) continue;
            top.Add((ComponentRow(c, LayerKind.Component, c.Id, null), c.Z, i));
        }
        for (var n = 0; n < copies.Count; n++)
        {
            var copy = copies[n];
            var parts = Enumerable.Range(0, components.Count).Where(i => components[i].Widget == copy.Id).ToList();
            var row = CopyRow(copy, parts.Select(i => components[i]).ToList(), expansion, find, isForked);
            if (parts.Count == 0) top.Add((row, int.MaxValue, n));
            else
            {
                var front = parts.Select(i => (components[i].Z, i)).Max();
                top.Add((row, front.Z, front.i));
            }
        }
        return Front(top);
    }

    private static List<LayerRow> Front(IEnumerable<(LayerRow Row, int Z, int Order)> rows)
        => rows.OrderByDescending(r => r.Z).ThenByDescending(r => r.Order).Select(r => r.Row).ToList();

    private static LayerRow CopyRow(WidgetCopy copy, List<ComponentDef> parts, Expansion expansion, Func<string, WidgetTemplate?> find, Func<string, bool> isForked)
    {
        var problems = expansion.Problems.Where(p => p.CopyId == copy.Id).ToList();
        var broken = problems.Any(p => p.Kind is ExpandProblemKind.MissingWidget or ExpandProblemKind.BrokenWidget);
        var template = broken ? null : Copies.TryFind(find, copy.Widget);
        var orphans = problems
            .Where(p => p.Kind is ExpandProblemKind.OrphanOverride or ExpandProblemKind.OrphanKnob)
            .Select(p => new LayerRow(LayerKind.Orphan, p.Detail, p.Kind == ExpandProblemKind.OrphanKnob ? "knob" : "override",
                copy.Id, null, true, true, false, false, []))
            .ToList();
        var orphanKeys = orphans.Where(o => o.Detail == "override").Select(o => o.Name).ToHashSet(StringComparer.Ordinal);
        var live = copy.Overrides.Keys.Where(k => !orphanKeys.Contains(k)).ToList();

        var prefix = copy.Id + ".";
        var children = Front(parts.Select((c, i) => (
            ComponentRow(c, LayerKind.Part, c.Id, (local, child) => Overridden(live, local, child), c.Id.StartsWith(prefix, StringComparison.Ordinal) ? c.Id[prefix.Length..] : c.Id),
            c.Z, i)));
        children.AddRange(orphans);

        return new LayerRow(LayerKind.Copy, template?.Name ?? copy.Widget, copy.Id, copy.Id, null,
            HasOverride: copy.Overrides.Count > 0, IsOrphan: orphans.Count > 0, IsBroken: broken,
            IsForkedShipped: !broken && isForked(copy.Widget), children);
    }

    /// <summary>Whether an override key names this part (<c>components.&lt;part&gt;.&lt;prop&gt;</c>)
    /// or, given <paramref name="child"/>, this repeater child (<c>components.&lt;part&gt;.&lt;child&gt;.&lt;prop&gt;</c>).
    /// Part ids never contain dots (plan D1), so the segment count decides.</summary>
    private static bool Overridden(IEnumerable<string> keys, string part, string? child)
        => keys.Any(k =>
        {
            var s = k.Split('.');
            return s.Length >= 3 && s[0] == "components" && s[1] == part
                && (child is null ? s.Length == 3 : s.Length == 4 && s[2] == child);
        });

    /// <param name="local">The id to show and to match override keys on (template-local for a part).</param>
    private static LayerRow ComponentRow(ComponentDef c, LayerKind kind, string selectId, Func<string, string?, bool>? overridden, string? local = null)
    {
        local ??= c.Id;
        var children = c is RepeaterDef r
            ? Front(r.Template.Select((t, i) => (
                new LayerRow(LayerKind.Child, t.Id, TypeName(t), selectId, t.Id, overridden?.Invoke(local, t.Id) ?? false, false, false, false, []),
                t.Z, i)))
            : [];
        return new LayerRow(kind, local, TypeName(c), selectId, null, overridden?.Invoke(local, null) ?? false, false, false, false, children);
    }

    public static string TypeName(ComponentDef c) => c switch
    {
        TextDef => "text",
        ImageDef => "image",
        BarDef => "bar",
        DialDef => "dial",
        ShortcutDef => "shortcut",
        RepeaterDef => "repeater",
        _ => "component",
    };

    /// <summary>Every row, depth first, parents before children.</summary>
    public static IEnumerable<LayerRow> Flatten(IEnumerable<LayerRow> rows)
    {
        foreach (var r in rows)
        {
            yield return r;
            foreach (var c in Flatten(r.Children)) yield return c;
        }
    }
}
