using System.Globalization;
using DeskWall.Core.Layout;

namespace DeskWall.Core.Widgets;

/// <summary>Override keys from a difference between two sets of a widget's parts (template-local
/// ids, rects and source names, as <see cref="WidgetExpander.Baseline"/> gives them).</summary>
public static class Overrides
{
    /// <summary>What <paramref name="edited"/> changes relative to <paramref name="baseline"/>: every
    /// property, <c>rect</c> or <c>z</c> that differs; <c>hidden</c> for a part (or repeater child)
    /// <paramref name="edited"/> lacks; a source's <c>every</c> and settings. A value equal to the
    /// baseline gives no key, which is how setting a value back resets it. Some differences have no
    /// override to express them, and are left out: a part <paramref name="edited"/> adds, a part
    /// whose type changed, a child of a nested repeater, a source removed, a setting removed, and
    /// <c>every</c> cleared back to the type's default. Callers that must be exact (the migrator)
    /// check the result by expanding it.</summary>
    public static Dictionary<string, PropertyValue> Diff(LayoutFile baseline, LayoutFile edited)
    {
        var result = new Dictionary<string, PropertyValue>(StringComparer.Ordinal);
        DiffParts("components.", baseline.Components, edited.Components, topLevel: true, result);
        foreach (var b in baseline.Sources)
        {
            if (edited.Sources.Find(s => s.Name == b.Name) is not { } e) continue;
            if (e.EverySeconds is int every && every != b.EverySeconds)
                result[$"sources.{b.Name}.every"] = PropertyValue.Literal(every.ToString(CultureInfo.InvariantCulture));
            foreach (var (key, value) in e.Settings)
                if (!b.Settings.TryGetValue(key, out var was) || was != value)
                    result[$"sources.{b.Name}.settings.{key}"] = PropertyValue.Literal(value);
        }
        return result;
    }

    public static bool Same(PropertyValue a, PropertyValue b)
        => a.IsBound == b.IsBound && (a.IsBound ? a.Binding!.ToString() == b.Binding!.ToString() : a.LiteralText == b.LiteralText);

    private static void DiffParts(string prefix, List<ComponentDef> baseline, List<ComponentDef> edited, bool topLevel, Dictionary<string, PropertyValue> result)
    {
        foreach (var b in baseline)
        {
            var key = prefix + b.Id + ".";
            var e = edited.Find(c => c.Id == b.Id);
            if (e is null)
            {
                result[key + ComponentProperties.Hidden] = PropertyValue.Literal("true");
                continue;
            }
            if (e.GetType() != b.GetType()) continue;
            foreach (var p in ComponentProperties.All(b))
            {
                // x/y/w/h unset on both sides only echo the rect, which has its own key.
                if (ComponentProperties.IsGeometry(p.Name) && !ComponentProperties.IsHeldGeometry(b, p.Name)
                    && !ComponentProperties.IsHeldGeometry(e, p.Name)) continue;
                var now = p.Get(e);
                if (!Same(p.Get(b), now)) result[key + char.ToLowerInvariant(p.Name[0]) + p.Name[1..]] = now;
            }
            if (topLevel && b is RepeaterDef br && e is RepeaterDef er) DiffParts(key, br.Template, er.Template, topLevel: false, result);
        }
    }
}
