using DeskWall.Core.Layout;
using DeskWall.Core.Widgets;

namespace DeskWall.Designer.Model;

/// <summary>Knobs and overrides that name nothing (#78, #80). Two rules, by what killed them:
/// <list type="bullet">
/// <item>An edit to the widget in this document (a part deleted at widget depth, a source removed)
/// takes what it killed with it, in the same undo entry, and says so: a knob entry whose target
/// went, the knob when that was its last, and the copies' own values for them. The owner did it and
/// the cause is on screen; Ctrl+Z brings it all back.</item>
/// <item>One that arrived dead (the widget file changed outside this document) is kept and listed on
/// the copy (plan D1: it applies again if what it names comes back), and <see cref="RemoveOrphan"/>
/// takes it off when the owner says so.</item>
/// </list></summary>
public static partial class Lens
{
    /// <summary>One knob that lost <c>sets</c> entries: their indices, and the knob as it is now
    /// (null when it lost every entry and so went).</summary>
    private sealed record KnobCut(Knob Knob, IReadOnlyList<int> Gone, Knob? Kept);

    /// <summary>Take an orphan the Layers panel lists off copy <paramref name="copyId"/>, as ONE undo
    /// entry: an override (<paramref name="knob"/> false) or a knob value by key. A knob the widget
    /// still has but whose <c>sets</c> name nothing in it is the widget's orphan, not the copy's, so
    /// its dead entries come out of the widget and every copy's value follows. One whose targets are
    /// all there fails on a value instead: the copy's own value goes, or, when the copy has none, the
    /// knob comes off the widget. False only when the copy lists no such orphan.</summary>
    public static bool RemoveOrphan(DesignerModel model, string copyId, string key, bool knob)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(key);
        var kind = knob ? ExpandProblemKind.OrphanKnob : ExpandProblemKind.OrphanOverride;
        if (FindCopy(model.Layout, copyId) is not { } copy
            || !model.Expanded().Problems.Any(p => p.Kind == kind && p.CopyId == copyId && p.Detail == key)) return false;
        const string Label = "Remove orphan";
        if (!knob)
        {
            model.Edit(Label, l => FindCopy(l, copyId)!.Overrides.Remove(key));
            return true;
        }
        if (Template(model, copy.Widget) is not { } template || !template.Knobs.Any(k => k.Id == key))
        {
            model.Edit(Label, l => FindCopy(l, copyId)!.Knobs.Remove(key));
            return true;
        }
        var edited = CutDeadKnobs(null, template, k => k.Id == key, out var cuts);
        if (cuts.Count == 0)
        {
            // Every target is there: what fails is a value (one that will not parse as a binding, or
            // a binding written to a setting). The copy's own, when it has one; else the widget's
            // default, which no copy can fix, so the knob comes off the widget.
            if (copy.Knobs.ContainsKey(key))
            {
                model.Edit(Label, l => FindCopy(l, copyId)!.Knobs.Remove(key));
                return true;
            }
            var dead = template.Knobs.First(k => k.Id == key);
            cuts = [new KnobCut(dead, Enumerable.Range(0, dead.Sets.Count).ToList(), null)];
            edited = WithKnobs(template, template.Knobs.Where(k => k.Id != key).ToList());
        }
        model.Edit(Label, (l, edits) =>
        {
            edits[template.Key] = edited;
            SettleCopies(l, template, edited, cuts, overrides: false);
        });
        return true;
    }

    /// <summary>Write <paramref name="edited"/> over <paramref name="was"/> as ONE undo entry, with
    /// what the change killed taken out (the first rule above), <paramref name="also"/> run inside the
    /// same entry, and a notice when that took a knob or a copy's own value.</summary>
    private static void CommitWidget(DesignerModel model, string label, WidgetTemplate was, WidgetTemplate edited, Action<LayoutFile>? also = null)
    {
        edited = CutDeadKnobs(was, edited, _ => true, out var cuts);
        var lost = 0;
        model.Edit(label, (l, edits) =>
        {
            edits[was.Key] = edited;
            lost = SettleCopies(l, was, edited, cuts, overrides: true);
            also?.Invoke(l);
        });

        // A knob that still works with fewer targets is not news; one that went, or a copy's value, is.
        var dropped = cuts.Where(c => c.Kept is null).Select(c => c.Knob.Label).ToList();
        var total = dropped.Count + lost;
        if (total == 0) return;
        var said = new List<string>(2);
        if (dropped.Count > 0) said.Add($"the {Listed(dropped)} knob{(dropped.Count == 1 ? "" : "s")}");
        if (lost > 0) said.Add(lost == 1 ? "a value one copy had of its own" : $"{lost} values the copies had of their own");
        model.Notify($"Removed {string.Join(" and ", said)}: what {(total == 1 ? "it" : "they")} set went with your edit. Ctrl+Z brings {(total == 1 ? "it" : "them")} back.");
    }

    /// <summary>"A", "A and B", "A, B and C".</summary>
    private static string Listed(IReadOnlyList<string> items)
        => items.Count < 3 ? string.Join(" and ", items) : string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1];

    /// <summary><paramref name="edited"/> with, for each knob <paramref name="which"/> picks, every
    /// <c>sets</c> entry that names nothing in it (and, given <paramref name="was"/>, named something
    /// there: an entry dead before this edit is not this edit's to take) taken out. A composite value
    /// loses the parts those entries took, in its default, its choices and (<see cref="SettleCopies"/>)
    /// the copies' values, so the entries left keep their own parts.</summary>
    private static WidgetTemplate CutDeadKnobs(WidgetTemplate? was, WidgetTemplate edited, Func<Knob, bool> which, out List<KnobCut> cuts)
    {
        cuts = [];
        var now = TemplateParts(edited);
        var then = was is null ? null : TemplateParts(was);
        var knobs = new List<Knob>(edited.Knobs.Count);
        foreach (var knob in edited.Knobs)
        {
            var gone = !which(knob) ? [] : Enumerable.Range(0, knob.Sets.Count)
                .Where(i => !KnobSets.Names(now, knob.Sets[i]) && (then is null || KnobSets.Names(then, knob.Sets[i])))
                .ToList();
            if (gone.Count == 0) { knobs.Add(knob); continue; }
            var kept = gone.Count == knob.Sets.Count ? null : knob with
            {
                Sets = knob.Sets.Where((_, i) => !gone.Contains(i)).ToList(),
                Default = Trim(knob.Default, gone),
                Choices = knob.Choices?.Select(c => Trim(c, gone)).ToList(),
            };
            cuts.Add(new KnobCut(knob, gone, kept));
            if (kept is not null) knobs.Add(kept);
        }
        // A knob the edit itself took out because its targets went (WidgetDocument drops a source
        // setting's knob with the source, before this sees it) is a cut too, so it is counted and
        // said; one whose targets are all still there was taken out on purpose (SettleCopies).
        if (then is not null)
            foreach (var knob in was!.Knobs)
            {
                if (!which(knob) || edited.Knobs.Any(k => k.Id == knob.Id)) continue;
                var named = Enumerable.Range(0, knob.Sets.Count).Where(i => KnobSets.Names(then, knob.Sets[i])).ToList();
                if (named.Count > 0 && named.All(i => !KnobSets.Names(now, knob.Sets[i])))
                    cuts.Add(new KnobCut(knob, Enumerable.Range(0, knob.Sets.Count).ToList(), null));
            }
        return cuts.Count == 0 ? edited : WithKnobs(edited, knobs);
    }

    private static WidgetTemplate WithKnobs(WidgetTemplate t, IReadOnlyList<Knob> knobs) => new()
    {
        Name = t.Name, Key = t.Key, Path = t.Path, Description = t.Description, Width = t.Width, Height = t.Height,
        Anchor = t.Anchor, Requires = t.Requires, Knobs = knobs, Sources = t.Sources, Components = t.Components,
    };

    /// <summary>The copies of <paramref name="now"/> in <paramref name="l"/>, after the widget went
    /// from <paramref name="was"/> to it: a value for a knob that went is deleted, one for a trimmed
    /// composite loses the same parts (and is deleted when that leaves the default), and, with
    /// <paramref name="overrides"/>, an override that applied before and applies to nothing now is
    /// deleted. A value for a knob the edit removed on purpose goes too, uncounted: the owner took
    /// the knob off and knows. Returns how many of the copies' own values went.</summary>
    private static int SettleCopies(LayoutFile l, WidgetTemplate was, WidgetTemplate now, IReadOnlyList<KnobCut> cuts, bool overrides)
    {
        var lost = 0;
        foreach (var copy in l.Copies ?? [])
        {
            if (!string.Equals(copy.Widget, now.Key, StringComparison.OrdinalIgnoreCase)) continue;
            var live = overrides ? Applying(was, copy) : null;
            foreach (var cut in cuts)
            {
                if (!copy.Knobs.TryGetValue(cut.Knob.Id, out var value)) continue;
                if (cut.Kept is null) { copy.Knobs.Remove(cut.Knob.Id); lost++; continue; }
                var trimmed = Trim(value, cut.Gone);
                if (trimmed == cut.Kept.Default) copy.Knobs.Remove(cut.Knob.Id);
                else copy.Knobs[cut.Knob.Id] = trimmed;
            }
            foreach (var knob in was.Knobs)
                if (!now.Knobs.Any(k => k.Id == knob.Id) && !cuts.Any(c => c.Knob.Id == knob.Id)) copy.Knobs.Remove(knob.Id);
            if (live is null) continue;
            var still = Applying(now, copy);
            foreach (var key in live.Where(k => !still.Contains(k)))
            {
                copy.Overrides.Remove(key);
                lost++;
            }
        }
        return lost;
    }

    /// <summary>The copy's override keys that apply to something in <paramref name="t"/>, tried in
    /// the expander's order (hidden last).</summary>
    private static HashSet<string> Applying(WidgetTemplate t, WidgetCopy copy)
    {
        var parts = WidgetExpander.Baseline(t, copy.Knobs);
        var applied = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, value) in copy.Overrides.OrderBy(o => IsHidden(o.Key)))
            if (KnobSets.Apply(parts, key, value)) applied.Add(key);
        return applied;
    }

    /// <summary>A composite value (parts joined by <c>||</c>, part 0 the label) without the parts
    /// the <paramref name="gone"/> entries took (entry i takes part i + 1). A plain value, which every
    /// entry takes whole, is unchanged.</summary>
    private static string Trim(string value, IReadOnlyList<int> gone)
    {
        var parts = value.Split("||").ToList();
        if (parts.Count == 1) return value;
        foreach (var i in gone.OrderDescending())
            if (i + 1 < parts.Count) parts.RemoveAt(i + 1);
        return string.Join("||", parts);
    }
}
