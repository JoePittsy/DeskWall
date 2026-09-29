using System.Text.Json;
using DeskWall.Core.Bindings;
using DeskWall.Core.Layout;

namespace DeskWall.Core.Widgets;

/// <param name="V2">The migrated layout (copies plus any loose components).</param>
/// <param name="Equivalent">Expanding <paramref name="V2"/> reproduces the v1 components, sources and paint order.</param>
/// <param name="Notes">Human-readable lines for <c>deskwall migrate --check</c>.</param>
public sealed record MigrationResult(LayoutFile V2, bool Equivalent, IReadOnlyList<string> Notes);

/// <summary>One-off v1 (stamped widget instances) to v2 (linked copies) conversion. Deleted in
/// Phase 6 with <see cref="LayoutFile.Widgets"/>.</summary>
public static class LayoutMigrator
{
    /// <summary>Each <see cref="WidgetRecord"/> becomes a copy: its origin is the most common
    /// offset of its parts from the widget's, its knobs are those that differ from the default, and
    /// whatever else differs from the knob-applied widget is an override (<see cref="Overrides.Diff"/>),
    /// with <c>hidden</c> for a part the instance lacks. A part no override can reproduce (and any
    /// component the widget lacks, or whose widget is missing) is left loose with its v1 id. A layout
    /// source is dropped only when no loose component binds it and the copies reproduce it. A
    /// version-2 file comes back unchanged. The input is never modified.</summary>
    public static MigrationResult Migrate(LayoutFile v1, Func<string, WidgetTemplate?> find)
    {
        if (v1.Version >= 2 || v1.Copies is not null) return new MigrationResult(v1, true, ["already version 2: nothing to migrate"]);

        var src = WidgetExpander.Clone(v1);
        var notes = new List<string>();
        var copies = new List<WidgetCopy>();
        var claimed = new HashSet<string>(StringComparer.Ordinal);

        var records = (src.Widgets ?? []).OrderBy(r => FirstIndex(src, r.Key)).ToList();
        foreach (var (instanceId, record) in records)
        {
            WidgetTemplate? template;
            try { template = find(record.Template); }
#pragma warning disable CA1031 // a widget that fails to load leaves its components loose, as a missing one does
            catch (Exception ex) { notes.Add($"{instanceId}: widget \"{record.Template}\" failed to load ({ex.Message}); its components stay loose"); continue; }
#pragma warning restore CA1031
            if (template is null) { notes.Add($"{instanceId}: widget \"{record.Template}\" not found; its components stay loose"); continue; }

            var copy = MigrateInstance(src, instanceId, record, template, claimed);
            copies.Add(copy);
            notes.Add($"copy {copy.Id} ({copy.Widget}) at {copy.X},{copy.Y}");
            foreach (var (k, v) in copy.Knobs) notes.Add($"  knob {k} = {v}");
            foreach (var (k, v) in copy.Overrides) notes.Add($"  override {k} = {v}");
        }

        var loose = src.Components.Where(c => !claimed.Contains(c.Id)).ToList();
        foreach (var c in loose)
        {
            if (c.Widget is not null) notes.Add($"loose {c.Id} (was part of {c.Widget})");
            c.Widget = null;
        }

        var v2 = new LayoutFile
        {
            Version = 2,
            BaseImage = src.BaseImage,
            BaseFit = src.BaseFit,
            Encode = src.Encode,
            JpegQuality = src.JpegQuality,
            Components = loose,
            Copies = copies,
        };

        // Sources: keep what a loose component binds, then drop only what the copies reproduce.
        var bound = loose.SelectMany(BoundSourceNames).ToHashSet(StringComparer.Ordinal);
        v2.Sources = src.Sources.Where(s => bound.Contains(s.Name)).ToList();
        var produced = WidgetExpander.Expand(v2, find).Layout.Sources;
        v2.Sources = src.Sources.Where(s => bound.Contains(s.Name)
            || !produced.Any(p => p.Name == s.Name && WidgetExpander.SameDefinition(p, s))).ToList();
        foreach (var s in src.Sources.Except(v2.Sources)) notes.Add($"source {s.Name} now comes from the copies");

        var differences = Differences(v1, WidgetExpander.Expand(v2, find).Layout);
        notes.AddRange(differences.Select(d => "not equivalent: " + d));
        return new MigrationResult(v2, differences.Count == 0, notes);
    }

    private static int FirstIndex(LayoutFile l, string instanceId)
    {
        var i = l.Components.FindIndex(c => c.Widget == instanceId);
        return i < 0 ? int.MaxValue : i;
    }

    private static WidgetCopy MigrateInstance(LayoutFile src, string instanceId, WidgetRecord record, WidgetTemplate template, HashSet<string> claimed)
    {
        var prefix = instanceId + ".";
        var owned = src.Components.Where(c => c.Widget == instanceId && c.Id.StartsWith(prefix, StringComparison.Ordinal))
            .ToDictionary(c => c.Id[prefix.Length..], StringComparer.Ordinal);

        // The origin: the most common offset of an instance part from the widget's own, first seen on a tie.
        var (x, y) = template.Components.Where(t => owned.ContainsKey(t.Id))
            .Select(t => (owned[t.Id].Rect.X - t.Rect.X, owned[t.Id].Rect.Y - t.Rect.Y))
            .GroupBy(o => o).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();

        var knobs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (id, value) in record.Knobs)
            if (template.Knobs.FirstOrDefault(k => k.Id == id) is not { } knob || knob.Default != value) knobs[id] = value;

        var baseline = WidgetExpander.Baseline(template, knobs);

        // The layout name each widget source ended up under: its own, or the "<name>N" a v1 type
        // clash gave it.
        var toWidget = new Dictionary<string, string>(StringComparer.Ordinal);
        var edited = new LayoutFile { BaseImage = "" };
        foreach (var ws in template.Sources)
        {
            var actual = src.Sources.Find(s => s.Name == ws.Name && s.Type == ws.Type)
                ?? Enumerable.Range(2, 98).Select(n => src.Sources.Find(s => s.Name == ws.Name + n && s.Type == ws.Type)).FirstOrDefault(s => s is not null);
            if (actual is null) continue;
            if (actual.Name != ws.Name) toWidget[actual.Name] = ws.Name;
            edited.Sources.Add(new SourceDef { Name = ws.Name, Type = actual.Type, EverySeconds = actual.EverySeconds, Settings = new(actual.Settings) });
        }
        foreach (var (localId, c) in owned)
        {
            var local = CloneComponent(c);
            local.Id = localId;
            local.Widget = null;
            local.Rect = local.Rect.Offset(-x, -y);
            if (toWidget.Count > 0) WidgetExpander.RewriteSourceNames(local, toWidget);
            edited.Components.Add(local);
        }

        var overrides = Overrides.Diff(baseline, edited);

        // Check every part against what the overrides reproduce; one they cannot is hidden in the
        // copy and stays loose instead.
        var trial = WidgetExpander.Clone(baseline);
        foreach (var (k, v) in overrides.OrderBy(o => o.Key.EndsWith("." + ComponentProperties.Hidden, StringComparison.Ordinal)))
            KnobSets.Apply(trial, k, v);
        foreach (var e in edited.Components)
        {
            var got = trial.Components.Find(t => t.Id == e.Id);
            if (got is not null && Json(got) == Json(e)) { claimed.Add(prefix + e.Id); continue; }
            if (!baseline.Components.Any(b => b.Id == e.Id)) continue;
            foreach (var k in overrides.Keys.Where(k => k.StartsWith($"components.{e.Id}.", StringComparison.Ordinal)).ToList()) overrides.Remove(k);
            overrides[$"components.{e.Id}.{ComponentProperties.Hidden}"] = PropertyValue.Literal("true");
        }

        return new WidgetCopy { Id = instanceId, Widget = record.Template, X = x, Y = y, Knobs = knobs, Overrides = overrides };
    }

    private static ComponentDef CloneComponent(ComponentDef c)
        => LayoutFile.Parse(new LayoutFile { BaseImage = "", Components = [c] }.ToJson()).Components[0];

    private static string Json(ComponentDef c)
    {
        var widget = c.Widget;
        c.Widget = null;
        try { return JsonSerializer.Serialize(c, LayoutJsonContext.Default.ComponentDef); }
        finally { c.Widget = widget; }
    }

    private static IEnumerable<string> BoundSourceNames(ComponentDef c)
        => ComponentProperties.For(c).Select(p => p.Get(c))
            .Where(v => v.IsBound && v.Binding!.Path is [NameSegment, ..])
            .Select(v => ((NameSegment)v.Binding!.Path[0]).Name);

    /// <summary>Why <paramref name="expanded"/> would paint differently from <paramref name="v1"/>:
    /// components structurally unequal by id (the <c>widget</c> field aside), sources unequal, or an
    /// overlapping pair of equal z painting in the other order.</summary>
    private static List<string> Differences(LayoutFile v1, LayoutFile expanded)
    {
        var diffs = new List<string>();
        var before = v1.Components.ToLookup(c => c.Id, Json);
        var after = expanded.Components.ToLookup(c => c.Id, Json);
        foreach (var id in before.Select(g => g.Key).Union(after.Select(g => g.Key)))
            if (!before[id].SequenceEqual(after[id]))
                diffs.Add(!after.Contains(id) ? $"component {id} is missing" : !before.Contains(id) ? $"component {id} is new" : $"component {id} differs");

        foreach (var s in v1.Sources)
            if (!expanded.Sources.Any(e => e.Name == s.Name && WidgetExpander.SameDefinition(e, s))) diffs.Add($"source {s.Name} differs or is missing");
        foreach (var e in expanded.Sources)
            if (!v1.Sources.Any(s => s.Name == e.Name)) diffs.Add($"source {e.Name} is new");

        var order = expanded.Components.Select((c, i) => (c.Id, i)).GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First().i, StringComparer.Ordinal);
        var comps = v1.Components;
        for (var i = 0; i < comps.Count; i++)
            for (var j = i + 1; j < comps.Count; j++)
                if (comps[i].Z == comps[j].Z && comps[i].Rect.Intersects(comps[j].Rect)
                    && order.TryGetValue(comps[i].Id, out var a) && order.TryGetValue(comps[j].Id, out var b) && a > b)
                    diffs.Add($"{comps[i].Id} and {comps[j].Id} overlap at z {comps[i].Z} and would paint in the other order");
        return diffs;
    }
}
