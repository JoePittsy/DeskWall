using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeskWall.Core;
using DeskWall.Core.Bindings;
using DeskWall.Core.Layout;
using DeskWall.Designer.Model;

namespace DeskWall.Designer.Model.Widgets;

/// <summary>Adds, edits and removes widget instances on a <see cref="LayoutFile"/>. See
/// <c>docs/layout-format.md</c> "Widgets" for the instantiation rules and the knob <c>sets</c>
/// grammar this implements.</summary>
public static class WidgetInstance
{
    private static readonly Regex TokenSuffix = new(@":\{(\w+)\}$");
    private const string PartSeparator = "||";

    /// <summary>Adds the widget to the layout: components copied with ids "&lt;instanceId&gt;.&lt;id&gt;",
    /// Widget = instanceId, rects offset by origin; sources merged by name (same name+type reused;
    /// clash -&gt; "&lt;name&gt;2" and bindings rewritten); knob defaults applied; layout.Widgets[instanceId]
    /// = new WidgetRecord. Returns instanceId ("&lt;key&gt;-&lt;n&gt;", n = first free).</summary>
    public static string Add(LayoutFile layout, WidgetTemplate t, Rect origin)
    {
        layout.Widgets ??= new();
        var instanceId = NextInstanceId(layout, t.Key);

        var renameMap = MergeSources(layout, t.Sources);

        var clones = WidgetJson.CloneComponents(t.Components);
        foreach (var c in clones)
        {
            var originalId = c.Id;
            c.Id = $"{instanceId}.{originalId}";
            c.Widget = instanceId;
            c.Rect = c.Rect.Offset(origin.X, origin.Y);
            if (renameMap.Count > 0) RewriteSourceNames(c, renameMap);
        }
        layout.Components.AddRange(clones);

        layout.Widgets[instanceId] = new WidgetRecord { Template = t.Key };
        foreach (var knob in t.Knobs) SetKnob(layout, t, instanceId, knob.Id, knob.Default);

        return instanceId;
    }

    /// <summary>Components, the Widgets entry, then any source no remaining component binds to.</summary>
    public static void Remove(LayoutFile layout, string instanceId)
    {
        var removed = layout.Components.Where(c => c.Widget == instanceId).ToList();
        var touchedSources = removed.SelectMany(SourceNames).Distinct().ToList();

        layout.Components.RemoveAll(c => c.Widget == instanceId);
        layout.Widgets?.Remove(instanceId);

        if (touchedSources.Count == 0) return;
        var stillUsed = layout.Components.SelectMany(SourceNames).ToHashSet(StringComparer.Ordinal);
        layout.Sources.RemoveAll(s => touchedSources.Contains(s.Name) && !stillUsed.Contains(s.Name));
    }

    public static IReadOnlyList<ComponentDef> Components(LayoutFile layout, string instanceId)
        => layout.Components.Where(c => c.Widget == instanceId).ToList();

    /// <summary>Union of the instance's rects; an empty Rect if the instance owns no component.</summary>
    public static Rect Bounds(LayoutFile layout, string instanceId)
    {
        var comps = Components(layout, instanceId);
        if (comps.Count == 0) return new Rect(0, 0, 0, 0);
        var minX = comps.Min(c => c.Rect.X);
        var minY = comps.Min(c => c.Rect.Y);
        var maxRight = comps.Max(c => c.Rect.Right);
        var maxBottom = comps.Max(c => c.Rect.Bottom);
        return new Rect(minX, minY, maxRight - minX, maxBottom - minY);
    }

    /// <summary>Applies a knob's <c>sets</c> paths for <paramref name="value"/> (see "Widgets" for
    /// the plain-vs-composite value convention) and records the raw value on the instance's
    /// WidgetRecord so it can be shown back and re-applied later.</summary>
    public static void SetKnob(LayoutFile layout, WidgetTemplate t, string instanceId, string knobId, string value)
    {
        var knob = t.Knobs.FirstOrDefault(k => k.Id == knobId)
            ?? throw new ArgumentException($"widget \"{t.Key}\" has no knob \"{knobId}\"", nameof(knobId));

        var parts = value.Split(PartSeparator);
        for (var i = 0; i < knob.Sets.Count; i++)
        {
            var part = parts.Length > i + 1 ? parts[i + 1] : parts[0];
            ApplySet(layout, instanceId, knob.Sets[i], part);
        }

        layout.Widgets ??= new();
        if (!layout.Widgets.TryGetValue(instanceId, out var record))
            layout.Widgets[instanceId] = record = new WidgetRecord { Template = t.Key };
        record.Knobs[knobId] = value;
    }

    /// <summary>Open-Meteo geocoding, first match. Null when the town has no match or the request
    /// fails (never throws for a bad town name; a network fault is the caller's problem).</summary>
    public static async Task<(double lat, double lon)?> ResolveTownAsync(string town, HttpClient http)
    {
        var url = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(town)}&count=1";
        using var response = await http.GetAsync(url).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0)
            return null;
        var first = results[0];
        return (first.GetProperty("latitude").GetDouble(), first.GetProperty("longitude").GetDouble());
    }

    // ---- internals ------------------------------------------------------------------------

    private static string NextInstanceId(LayoutFile layout, string key)
    {
        var n = 1;
        while (layout.Widgets!.ContainsKey($"{key}-{n}")) n++;
        return $"{key}-{n}";
    }

    /// <summary>Adds/reuses each template source in the layout; returns the template-local source
    /// name to the actual name used in the layout (identity unless a clash renamed it).</summary>
    private static Dictionary<string, string> MergeSources(LayoutFile layout, IReadOnlyList<SourceDef> templateSources)
    {
        var renameMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var src in templateSources)
        {
            var existing = layout.Sources.FirstOrDefault(s => s.Name == src.Name);
            if (existing is null)
            {
                layout.Sources.Add(WidgetJson.CloneSource(src));
                renameMap[src.Name] = src.Name;
            }
            else if (existing.Type == src.Type)
            {
                renameMap[src.Name] = src.Name;
            }
            else
            {
                var newName = NextFreeSourceName(layout, src.Name);
                var copy = WidgetJson.CloneSource(src);
                copy.Name = newName;
                layout.Sources.Add(copy);
                renameMap[src.Name] = newName;
            }
        }
        return renameMap;
    }

    private static string NextFreeSourceName(LayoutFile layout, string baseName)
    {
        var n = 2;
        while (layout.Sources.Any(s => s.Name == baseName + n)) n++;
        return baseName + n;
    }

    /// <summary>Rewrites every bound property's leading path segment from a template-local source
    /// name to the actual (possibly renamed) one. Only ever touches a component this same Add call
    /// just placed, never another widget's.</summary>
    private static void RewriteSourceNames(ComponentDef c, Dictionary<string, string> renameMap)
    {
        foreach (var prop in PropertySchema.For(c))
        {
            var v = prop.Get(c);
            if (v is null || !v.IsBound) continue;
            var binding = v.Binding!;
            if (binding.Path.Count == 0 || binding.Path[0] is not NameSegment ns) continue;
            if (!renameMap.TryGetValue(ns.Name, out var actualName) || actualName == ns.Name) continue;
            var newPath = new List<PathSegment> { new NameSegment(actualName) };
            newPath.AddRange(binding.Path.Skip(1));
            prop.Set(c, PropertyValue.Bound(new Binding(newPath, binding.Format)));
        }
    }

    private static IEnumerable<string> SourceNames(ComponentDef c)
    {
        foreach (var prop in PropertySchema.For(c))
        {
            var v = prop.Get(c);
            if (v?.IsBound == true && v.Binding!.Path.Count > 0 && v.Binding.Path[0] is NameSegment ns)
                yield return ns.Name;
        }
    }

    /// <summary>Applies one <c>sets</c> entry. See "Widgets" for the grammar: an optional trailing
    /// ":{token}" substitutes into the target's current string; otherwise "=bind:" writes a
    /// binding parsed from <paramref name="part"/> (never from the sets path's own text, which
    /// only documents the default choice's shape) and anything else writes a literal.</summary>
    private static void ApplySet(LayoutFile layout, string instanceId, string setPath, string part)
    {
        var s = setPath;
        string? token = null;
        var tokenMatch = TokenSuffix.Match(s);
        if (tokenMatch.Success)
        {
            token = tokenMatch.Groups[1].Value;
            s = s[..tokenMatch.Index];
        }

        var isBind = false;
        var bindIndex = s.IndexOf("=bind:", StringComparison.Ordinal);
        if (bindIndex >= 0)
        {
            isBind = true;
            s = s[..bindIndex];
        }

        var segments = s.Split('.');
        if (segments.Length >= 3 && segments[0] == "components")
        {
            var id = $"{instanceId}.{segments[1]}";
            var propertyName = segments[2];
            var component = layout.Components.FirstOrDefault(c => c.Id == id)
                ?? throw new InvalidOperationException($"sets path \"{setPath}\": no component \"{id}\"");
            var prop = PropertySchema.For(component).FirstOrDefault(p => string.Equals(p.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"sets path \"{setPath}\": \"{id}\" has no property \"{propertyName}\"");

            if (token is not null)
            {
                var current = prop.Get(component)?.LiteralText ?? "";
                prop.Set(component, PropertyValue.Literal(current.Replace("{" + token + "}", part)));
            }
            else if (isBind)
            {
                prop.Set(component, PropertyValue.Bound(Binding.Parse(part)));
            }
            else
            {
                prop.Set(component, PropertyValue.Literal(part));
            }
        }
        else if (segments.Length >= 4 && segments[0] == "sources" && segments[2] == "settings")
        {
            var name = segments[1];
            var key = segments[3];
            var source = layout.Sources.FirstOrDefault(x => x.Name == name)
                ?? throw new InvalidOperationException($"sets path \"{setPath}\": no source \"{name}\"");
            if (token is not null)
            {
                var current = source.Settings.TryGetValue(key, out var v) ? v : "";
                source.Settings[key] = current.Replace("{" + token + "}", part);
            }
            else
            {
                source.Settings[key] = part;
            }
        }
        else
        {
            throw new InvalidOperationException($"bad sets path \"{setPath}\"");
        }
    }
}
