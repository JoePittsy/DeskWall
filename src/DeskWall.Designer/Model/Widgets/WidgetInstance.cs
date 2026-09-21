using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using DeskWall.Core;
using DeskWall.Core.Bindings;
using DeskWall.Core.Layout;

namespace DeskWall.Designer.Model;

/// <summary>Stamping a template into a layout, and everything that then has to be kept true about
/// it: ids are prefixed with the instance, sources are shared by name, knobs write through to the
/// components and sources they name, and removing the widget takes its sources with it.</summary>
public static class WidgetInstance
{
    /// <summary>Add the widget at <paramref name="origin"/>. Returns the instance id
    /// ("&lt;key&gt;-&lt;n&gt;", n the first free number).</summary>
    public static string Add(LayoutFile layout, WidgetTemplate t, Rect origin)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(t);

        var instanceId = NextInstanceId(layout, t.Key);
        var rename = MergeSources(layout, t);

        foreach (var c in Clone(t.Components))
        {
            c.Id = $"{instanceId}.{c.Id}";
            c.Widget = instanceId;
            c.Rect = c.Rect.Offset(origin.X, origin.Y);
            foreach (var (from, to) in rename) RewriteSource(c, from, to);
            layout.Components.Add(c);
        }

        layout.Widgets ??= new Dictionary<string, WidgetRecord>(StringComparer.Ordinal);
        var record = new WidgetRecord { Template = t.Key };
        layout.Widgets[instanceId] = record;
        foreach (var knob in t.Knobs) SetKnob(layout, t, instanceId, knob.Id, knob.Default);
        return instanceId;
    }

    /// <summary>Remove the widget: its components, its record, and then any source no remaining
    /// binding names.</summary>
    public static void Remove(LayoutFile layout, string instanceId)
    {
        ArgumentNullException.ThrowIfNull(layout);
        layout.Components.RemoveAll(c => string.Equals(c.Widget, instanceId, StringComparison.Ordinal));
        layout.Widgets?.Remove(instanceId);
        var used = SourceNamesInUse(layout);
        layout.Sources.RemoveAll(s => !used.Contains(s.Name));
    }

    public static IReadOnlyList<ComponentDef> Components(LayoutFile layout, string instanceId)
        => layout.Components.Where(c => string.Equals(c.Widget, instanceId, StringComparison.Ordinal)).ToList();

    /// <summary>The union of the instance's declared rects. Zero-size when the instance is gone.</summary>
    public static Rect Bounds(LayoutFile layout, string instanceId)
    {
        var rects = Components(layout, instanceId).Select(c => c.Rect).ToList();
        if (rects.Count == 0) return default;
        int x = rects.Min(r => r.X), y = rects.Min(r => r.Y);
        return new Rect(x, y, rects.Max(r => r.Right) - x, rects.Max(r => r.Bottom) - y);
    }

    /// <summary>Every instance id present, in the order their components first appear.</summary>
    public static IReadOnlyList<string> Instances(LayoutFile layout)
    {
        var seen = new List<string>();
        foreach (var c in layout.Components)
            if (c.Widget is { } w && !seen.Contains(w, StringComparer.Ordinal)) seen.Add(w);
        return seen;
    }

    /// <summary>Turn one knob: apply each of its <c>sets</c> paths and record the value so it can be
    /// shown back and re-applied after a template update.</summary>
    public static void SetKnob(LayoutFile layout, WidgetTemplate t, string instanceId, string knobId, string value)
    {
        var knob = t.Knobs.FirstOrDefault(k => string.Equals(k.Id, knobId, StringComparison.OrdinalIgnoreCase));
        if (knob is null) return;

        // A town knob carries its resolved coordinates alongside the name ("Leeds|53.8008|-1.5491")
        // so one value can fill both {lat} and {lon}; only the name is recorded and shown back.
        var tokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var recorded = value;
        if (knob.Type == KnobType.Town)
        {
            var parts = value.Split('|');
            recorded = parts[0];
            if (parts.Length >= 3) { tokens["lat"] = parts[1]; tokens["lon"] = parts[2]; }
            tokens["town"] = parts[0];
        }

        foreach (var path in knob.Sets) ApplySet(layout, instanceId, path, value, tokens);

        if (layout.Widgets is not null && layout.Widgets.TryGetValue(instanceId, out var record))
            record.Knobs[knob.Id] = recorded;
    }

    /// <summary>Open-Meteo geocoding, first hit only. Null when the town is not found or the service
    /// cannot be reached - the caller keeps the text the owner typed either way.</summary>
    public static async Task<(double lat, double lon)?> ResolveTownAsync(string town, HttpClient http)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (string.IsNullOrWhiteSpace(town)) return null;
        var url = "https://geocoding-api.open-meteo.com/v1/search?count=1&name=" + Uri.EscapeDataString(town.Trim());
        try
        {
            var json = await http.GetStringAsync(url).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("results", out var results) ||
                results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0) return null;
            var first = results[0];
            return (first.GetProperty("latitude").GetDouble(), first.GetProperty("longitude").GetDouble());
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            return null;
        }
    }

    // ---- ids and cloning ---------------------------------------------------------------------

    private static string NextInstanceId(LayoutFile layout, string key)
    {
        var taken = Instances(layout).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var n = 1; ; n++)
        {
            var id = $"{key}-{n}";
            if (taken.Add(id)) return id;
        }
    }

    /// <summary>Deep copy through the layout's own serializer, so a repeater brings its template.</summary>
    private static List<ComponentDef> Clone(IReadOnlyList<ComponentDef> defs)
        => LayoutFile.Parse(new LayoutFile { BaseImage = "", Components = defs.ToList() }.ToJson()).Components;

    // ---- sources ------------------------------------------------------------------------------

    /// <summary>Add each of the template's sources the layout does not already have. Same name and
    /// same type is a reuse (one `time` source serves every clock); same name, different type gets
    /// a suffixed name, and the instance's bindings are rewritten to it.</summary>
    private static List<(string From, string To)> MergeSources(LayoutFile layout, WidgetTemplate t)
    {
        var rename = new List<(string, string)>();
        foreach (var def in t.Sources)
        {
            var existing = layout.Sources.FirstOrDefault(s => string.Equals(s.Name, def.Name, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                layout.Sources.Add(CloneSource(def, def.Name));
                continue;
            }
            if (string.Equals(existing.Type, def.Type, StringComparison.OrdinalIgnoreCase)) continue;   // reuse

            var name = def.Name; var n = 2;
            while (layout.Sources.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))) name = $"{def.Name}{n++}";
            layout.Sources.Add(CloneSource(def, name));
            rename.Add((def.Name, name));
        }
        return rename;
    }

    private static SourceDef CloneSource(SourceDef def, string name) => new()
    {
        Name = name,
        Type = def.Type,
        EverySeconds = def.EverySeconds,
        Settings = new Dictionary<string, string>(def.Settings, StringComparer.Ordinal),
    };

    /// <summary>Every source name a binding anywhere in the layout starts with.</summary>
    private static HashSet<string> SourceNamesInUse(LayoutFile layout)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in layout.Components) Visit(c, (_, v) => { if (Root(v) is { } r) used.Add(r); return v; });
        return used;
    }

    private static string? Root(PropertyValue v)
        => v.Binding?.Path.Count > 0 && v.Binding.Path[0] is NameSegment n ? n.Name : null;

    private static void RewriteSource(ComponentDef c, string from, string to) => Visit(c, (_, v) =>
    {
        if (v.Binding is not { } b || Root(v) is not { } r || !string.Equals(r, from, StringComparison.OrdinalIgnoreCase)) return v;
        return PropertyValue.Bound(new Binding([new NameSegment(to), .. b.Path.Skip(1)], b.Format));
    });

    /// <summary>Every PropertyValue on a component and, for a repeater, on its template children.
    /// The visitor returns the value to keep, so this is both a read and a rewrite.</summary>
    private static void Visit(ComponentDef def, Func<string, PropertyValue, PropertyValue> visit)
    {
        foreach (var prop in PropertySchema.For(def))
        {
            var current = prop.Get(def);
            if (current is null) continue;
            var next = visit(prop.Name, current);
            if (!ReferenceEquals(next, current)) prop.Set(def, next);
        }
        if (def is RepeaterDef r)
            foreach (var child in r.Template) Visit(child, visit);
    }

    // ---- knob set paths -------------------------------------------------------------------------

    /// <summary>One `sets` entry. Grammar:
    /// <c>components.&lt;id&gt;.&lt;property&gt;</c>, <c>sources.&lt;name&gt;.settings.&lt;key&gt;</c>,
    /// either plain (write the value), with <c>:{token}</c> (substitute that token inside the current
    /// string), or with <c>=bind:&lt;text&gt;</c> (write a binding, with {value} substituted).</summary>
    private static void ApplySet(LayoutFile layout, string instanceId, string path, string value, Dictionary<string, string> tokens)
    {
        string? bindText = null;
        var eq = path.IndexOf("=bind:", StringComparison.Ordinal);
        if (eq >= 0) { bindText = path[(eq + 6)..].Replace("{value}", value, StringComparison.Ordinal); path = path[..eq]; }

        string? token = null;
        var colon = path.IndexOf(':');
        if (colon >= 0) { token = path[(colon + 1)..].Trim('{', '}'); path = path[..colon]; }

        var parts = path.Split('.');
        if (parts.Length >= 3 && parts[0] == "components")
        {
            var component = layout.Components.FirstOrDefault(c =>
                string.Equals(c.Id, $"{instanceId}.{parts[1]}", StringComparison.Ordinal));
            if (component is null) return;
            var prop = PropertySchema.For(component).FirstOrDefault(p => string.Equals(p.Name, parts[2], StringComparison.OrdinalIgnoreCase));
            if (prop is null) return;
            if (bindText is not null) { prop.Set(component, PropertyValue.Bound(Binding.Parse(bindText))); return; }
            var current = prop.Get(component);
            prop.Set(component, PropertyValue.Literal(Substitute(current is { IsBound: false } ? current.LiteralText ?? "" : "", token, value, tokens)));
        }
        else if (parts.Length >= 4 && parts[0] == "sources" && parts[2] == "settings")
        {
            var source = layout.Sources.FirstOrDefault(s => string.Equals(s.Name, parts[1], StringComparison.OrdinalIgnoreCase));
            if (source is null) return;
            source.Settings.TryGetValue(parts[3], out var current);
            source.Settings[parts[3]] = Substitute(current ?? "", token, value, tokens);
        }
    }

    /// <summary>No token: the value replaces the whole string. A token: only "{token}" inside the
    /// current string is replaced, by the matching piece of the value (a town's {lat}/{lon}) or by
    /// the value itself.</summary>
    private static string Substitute(string current, string? token, string value, Dictionary<string, string> tokens)
    {
        if (token is null) return value;
        var replacement = tokens.TryGetValue(token, out var t) ? t : value;
        return current.Replace("{" + token + "}", replacement, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The number a Number knob holds, for a control that wants one.</summary>
    public static double? Number(string text)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
}
