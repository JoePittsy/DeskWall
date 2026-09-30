using DeskWall.Core.Bindings;
using DeskWall.Core.Layout;

namespace DeskWall.Core.Widgets;

public enum ExpandProblemKind { MissingWidget, BrokenWidget, OrphanOverride, OrphanKnob }

/// <summary>One thing the expander skipped. <paramref name="Detail"/> is the widget key for
/// Missing/Broken (plus the load error for Broken), else the override key or knob id.</summary>
public sealed record ExpandProblem(ExpandProblemKind Kind, string CopyId, string Detail);

/// <param name="Layout">The expanded layout: no copies, only ordinary sources and components.</param>
/// <param name="WidgetKeys">Every widget key the copies referenced, found or not, once each.</param>
public sealed record Expansion(LayoutFile Layout, IReadOnlyList<ExpandProblem> Problems, IReadOnlyList<string> WidgetKeys);

/// <summary>Turns a v2 layout's <see cref="LayoutFile.Copies"/> into ordinary components before
/// resolve (<c>docs/layout-format.md</c> "Copies"). Runs at load, never per tick.</summary>
public static class WidgetExpander
{
    /// <param name="find">Widget key to its template; null when no file has that key. May throw
    /// for a file that fails to load, which becomes <see cref="ExpandProblemKind.BrokenWidget"/>.</param>
    public static Expansion Expand(LayoutFile layout, Func<string, WidgetTemplate?> find)
    {
        if (layout.Copies is null or { Count: 0 }) return new Expansion(layout, [], []);

        var result = Clone(layout);
        result.Copies = null;
        var problems = new List<ExpandProblem>();
        var keys = new List<string>();
        foreach (var copy in layout.Copies)
        {
            if (!keys.Contains(copy.Widget, StringComparer.OrdinalIgnoreCase)) keys.Add(copy.Widget);

            WidgetTemplate? template;
            try { template = find(copy.Widget); }
#pragma warning disable CA1031 // any load failure is one broken copy, never a failed layout
            catch (Exception ex)
#pragma warning restore CA1031
            {
                problems.Add(new(ExpandProblemKind.BrokenWidget, copy.Id, $"{copy.Widget}: {ex.Message}"));
                continue;
            }
            if (template is null)
            {
                problems.Add(new(ExpandProblemKind.MissingWidget, copy.Id, copy.Widget));
                continue;
            }

            var parts = Parts(template);
            foreach (var knob in ApplyKnobs(parts, template, copy.Knobs))
                problems.Add(new(ExpandProblemKind.OrphanKnob, copy.Id, knob));
            foreach (var id in copy.Knobs.Keys)
                if (!template.Knobs.Any(k => k.Id == id)) problems.Add(new(ExpandProblemKind.OrphanKnob, copy.Id, id));

            // hidden last, so another override on a part the copy hides is not reported as an orphan.
            foreach (var (key, value) in copy.Overrides.OrderBy(o => IsHidden(o.Key)))
                if (!KnobSets.Apply(parts, key, value)) problems.Add(new(ExpandProblemKind.OrphanOverride, copy.Id, key));

            var renamed = MergeSources(result.Sources, parts.Sources);
            foreach (var c in parts.Components)
            {
                if (renamed.Count > 0) RewriteSourceNames(c, renamed);
                c.Id = $"{copy.Id}.{c.Id}";
                c.Widget = copy.Id;
                c.Rect = c.Rect.Offset(copy.X, copy.Y);
                c.GeometryOffsetX += copy.X; c.GeometryOffsetY += copy.Y;
                c.Z += copy.Z;
            }
            result.Components.AddRange(parts.Components);
        }
        return new Expansion(result, problems, keys);
    }

    /// <summary>The widget's parts with <paramref name="knobs"/> applied (a knob not in the
    /// dictionary takes its default) and no overrides: template-local ids and rects, the widget's
    /// own source names. What an override is a difference from (<see cref="Overrides.Diff"/>).</summary>
    public static LayoutFile Baseline(WidgetTemplate template, IReadOnlyDictionary<string, string> knobs)
    {
        var parts = Parts(template);
        ApplyKnobs(parts, template, knobs);
        return parts;
    }

    /// <summary>A deep copy of the template's sources and components, as a parts container.</summary>
    private static LayoutFile Parts(WidgetTemplate t)
        => Clone(new LayoutFile { BaseImage = "", Sources = [.. t.Sources], Components = [.. t.Components] });

    internal static LayoutFile Clone(LayoutFile l) => LayoutFile.Parse(l.ToJson());

    /// <summary>Every knob in template order; returns the ids of the knobs whose <c>sets</c> found
    /// no target.</summary>
    private static List<string> ApplyKnobs(LayoutFile parts, WidgetTemplate template, IReadOnlyDictionary<string, string> knobs)
    {
        var failed = new List<string>();
        foreach (var knob in template.Knobs)
            if (KnobSets.ApplyKnob(parts, template, knob, knobs.TryGetValue(knob.Id, out var v) ? v : knob.Default).Count > 0)
                failed.Add(knob.Id);
        return failed;
    }

    private static bool IsHidden(string key) => key.EndsWith("." + ComponentProperties.Hidden, StringComparison.OrdinalIgnoreCase);

    /// <summary>Adds each of a copy's sources to <paramref name="into"/>: same name and identical
    /// definition is shared; a difference takes the first free (or identical) "&lt;name&gt;2",
    /// "&lt;name&gt;3", ... Returns widget name to layout name for the renamed ones only.</summary>
    private static Dictionary<string, string> MergeSources(List<SourceDef> into, List<SourceDef> add)
    {
        var renamed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var source in add)
        {
            var original = source.Name;
            for (var n = 2; ; n++)
            {
                var existing = into.Find(s => s.Name == source.Name);
                if (existing is null) { into.Add(source); break; }
                if (SameDefinition(existing, source)) break;
                source.Name = original + n;
            }
            if (source.Name != original) renamed[original] = source.Name;
        }
        return renamed;
    }

    internal static bool SameDefinition(SourceDef a, SourceDef b)
        => a.Type == b.Type && a.EverySeconds == b.EverySeconds && a.Settings.Count == b.Settings.Count
           && a.Settings.All(kv => b.Settings.TryGetValue(kv.Key, out var v) && v == kv.Value);

    /// <summary>Repoints a part's bindings at its copy's renamed sources. Top-level properties
    /// only: a repeater child binds against its item, never a source by name.</summary>
    internal static void RewriteSourceNames(ComponentDef c, IReadOnlyDictionary<string, string> renamed)
    {
        foreach (var prop in ComponentProperties.For(c))
        {
            var v = prop.Get(c);
            if (!v.IsBound || v.Binding!.Path is not [NameSegment first, ..] path) continue;
            if (!renamed.TryGetValue(first.Name, out var name)) continue;
            prop.Set(c, PropertyValue.Bound(new Binding([new NameSegment(name), .. path.Skip(1)], v.Binding.Format)));
        }
    }
}
