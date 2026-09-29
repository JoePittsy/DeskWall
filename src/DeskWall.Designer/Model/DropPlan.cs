using DeskWall.Core;
using DeskWall.Core.Bindings;
using DeskWall.Core.Layout;
using DeskWall.Core.Widgets;

namespace DeskWall.Designer.Model;

/// <summary>Where a value was dropped: a point on the canvas (in the coordinates of
/// <see cref="DesignerModel.Parts"/>), and the part under it, if any, by its id there.</summary>
public sealed record DropTarget(int X, int Y, string? PartId = null);

/// <summary>One thing a drop can do. Either <see cref="Create"/> is set (a new part, bound to the
/// value, centred on the drop point when applied) or <see cref="Property"/> is (bind that property
/// of the target part to <see cref="Value"/>), or at copy depth <see cref="Knob"/> is as well (set
/// that knob of the copy to <see cref="Choice"/>, which is what gives the property this value).</summary>
public sealed record DropOption(string Label, ComponentDef? Create, string? Property, PropertyValue Value, string? Knob = null, string? Choice = null);

/// <summary>What dropping a live value (brief section 3, "Binding by dragging") offers, worked out
/// without touching the document, so the canvas can show the options in a popover and then
/// <see cref="Apply"/> the one picked. The first option is the default.
/// <list type="bullet">
/// <item>On empty canvas: a Dial and a Bar for a fraction, a Clock for a timestamp, then a Text per
/// format preset; a list offers nothing (it feeds a repeater, which is bound, not dropped).</item>
/// <item>On a part: bind the first property that fits (fraction, text, image source, items). A text
/// property offers one option per preset. A part whose property already shows this value offers the
/// Text options instead, which is how dropping "CPU load" on its own dial gives the "27%" in the
/// middle; a part with nothing that fits offers the empty-canvas options.</item>
/// <item>At layout depth a drop on a placed copy's part is a drop on empty canvas: a new loose part
/// at that point. A copy's insides are bound only at copy depth, where it is visibly open.</item>
/// <item>At copy depth nothing that creates a part is offered: a copy's overrides cannot add one.
/// On a property a knob sets, the knob choice that gives this value comes first ("Set Metric to
/// GPU"), then the override, labelled as one; <see cref="Note"/> says which was done.</item>
/// <item>At widget depth a drop outside the widget's frame (<see cref="Insert.InFrame"/>) offers
/// nothing: the frame hugs its parts, so a stray drop would grow every copy.</item>
/// </list>
/// Applying is one undo entry, and adds the value's source to the layout (or to the widget, at
/// widget depth) when it is not there already.</summary>
public sealed class DropPlan
{
    public ValueEntry Value { get; }
    public DropTarget Target { get; }
    public IReadOnlyList<DropOption> Options { get; }

    private DropPlan(ValueEntry value, DropTarget target, IReadOnlyList<DropOption> options)
    { Value = value; Target = target; Options = options; }

    /// <summary>Properties a drop binds, in order of preference.</summary>
    private static readonly string[] Bindable = ["Fraction", "Text", "Source", "Items"];

    public static DropPlan For(DesignerModel model, ValueEntry value, DropTarget target)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(target);
        if (!Insert.InFrame(model, target.X, target.Y)) return new(value, target, []);
        var layoutDepth = model.Depth.Kind == DepthKind.Layout;
        if (target.PartId is { } id && model.Find(id) is { } part
            && !(layoutDepth && part.Widget is { } w && Copies.Find(model.Layout, w) is not null))
        {
            var prop = PropertySchema.For(part).FirstOrDefault(p => Bindable.Contains(p.Name) && Fits(p, value.Kind));
            if (prop is null) return new(value, target, CanvasOptions(model, value));
            var current = prop.Get(part);
            if (current?.Binding is { } b && SamePath(b, value.Path))
                return new(value, target, model.Depth.Kind == DepthKind.Copy ? [] : TextOptions(value));
            var onCopy = model.Depth.Kind == DepthKind.Copy;
            List<DropOption> options = prop.Name == "Text"
                ? FormatPresets.For(value.Kind).Select(p => new DropOption((onCopy ? OverrideShowAs : "Show as ") + p.Label, null, prop.Name, Bound(value, p.Format))).ToList()
                : [new DropOption(onCopy ? OverridePrefix : "Bind " + prop.Name.ToLowerInvariant(), null, prop.Name, Bound(value, null))];
            if (onCopy && KnobOption(model, part.Id, prop.Name, value) is { } knob) options.Insert(0, knob);
            return new(value, target, options);
        }
        // A copy's part at layout depth is not a target: the drop makes a loose part there.
        return new(value, target with { PartId = null }, CanvasOptions(model, value));
    }

    /// <summary>What the last <see cref="Apply"/> did to a copy, in a sentence for the status line
    /// (an override written or taken off, or a knob set); null when it touched no copy.</summary>
    public string? Note { get; private set; }

    /// <summary>How an override option's label starts at copy depth.</summary>
    public const string OverridePrefix = "Override on this copy";

    /// <summary>The same, for a text format option ("Override on this copy: show as 27%").</summary>
    public const string OverrideShowAs = OverridePrefix + ": show as ";

    /// <summary>At copy depth, a knob of the copy's widget that sets <paramref name="property"/> of
    /// <paramref name="partId"/> and has a choice under which that property, or another the knob
    /// sets, shows <paramref name="value"/> (format aside): "Set Metric to GPU". Null when there is none.</summary>
    private static DropOption? KnobOption(DesignerModel model, string partId, string property, ValueEntry value)
    {
        if (model.Depth.CopyId is not { } copyId || Copies.Find(model.Layout, copyId) is not { } copy
            || Copies.TryFind(model.Finder(), copy.Widget) is not { } template) return null;
        var key = OverrideKey(copyId, partId, property);
        foreach (var knob in template.Knobs)
        {
            if (knob.Choices is not { Count: > 0 } choices || !knob.Sets.Any(s => string.Equals(SetPath(s), key, StringComparison.OrdinalIgnoreCase))) continue;
            foreach (var choice in choices)
            {
                // The copy's own choice already says this, unless an override is hiding it.
                if (choice == Copies.KnobValue(copy, knob) && !copy.Overrides.ContainsKey(key)) continue;
                var knobs = new Dictionary<string, string>(copy.Knobs, StringComparer.Ordinal) { [knob.Id] = choice };
                var baseline = WidgetExpander.Baseline(template, knobs);
                // This property, or another the same knob sets: CPU load dropped on the RAM dial's
                // "50%" still means "make this the CPU dial".
                if (knob.Sets.Any(s => KnobSets.Get(baseline, SetPath(s))?.Binding is { } b && SamePath(b, value.Path)))
                    return new DropOption($"Set {knob.Label} to {choice.Split("||")[0]}", null, property, Bound(value, null), knob.Id, choice);
            }
        }
        return null;
    }

    /// <summary>A knob <c>sets</c> entry's target path, without its <c>=bind:</c> or <c>:{token}</c> tail.</summary>
    private static string SetPath(string set)
    {
        var bind = set.IndexOf("=bind:", StringComparison.Ordinal);
        if (bind >= 0) return set[..bind];
        var token = set.LastIndexOf(":{", StringComparison.Ordinal);
        return token >= 0 ? set[..token] : set;
    }

    /// <summary>The override key for a projected part's property: <c>components.dial.fraction</c>.</summary>
    private static string OverrideKey(string copyId, string partId, string property)
    {
        var local = partId.StartsWith(copyId + ".", StringComparison.Ordinal) ? partId[(copyId.Length + 1)..] : partId;
        return $"components.{local}.{char.ToLowerInvariant(property[0])}{property[1..]}";
    }

    /// <summary>Do <paramref name="option"/> (one of <see cref="Options"/>) as one undo entry.
    /// Returns the id, as <see cref="DesignerModel.Parts"/> names it, of the part created or bound.</summary>
    public string Apply(DesignerModel model, DropOption option)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(option);
        Note = null;
        return option.Create is { } create ? ApplyCreate(model, create, option.Label) : ApplyBind(model, option);
    }

    private string ApplyCreate(DesignerModel model, ComponentDef create, string label)
    {
        var def = Clone(create);
        var prefix = model.Depth is { Kind: DepthKind.Widget, CopyId: { } origin } ? origin + "." : "";
        def.Rect = def.Rect with { X = Target.X - def.Rect.W / 2, Y = Target.Y - def.Rect.H / 2 };
        var id = "";
        model.EditAtDepth(label, l =>
        {
            AddSource(model, l.Sources);
            id = prefix + def.Id;
            for (var n = 2; l.Components.Any(c => c.Id == id); n++) id = $"{prefix}{def.Id}-{n}";
            def.Id = id;
            l.Components.Add(def);
        });
        return id;
    }

    private string ApplyBind(DesignerModel model, DropOption option)
    {
        var partId = Target.PartId ?? throw new InvalidOperationException("a bind needs a part");
        var property = option.Property ?? throw new InvalidOperationException("a bind needs a property");
        var label = "Bind " + Value.Label;
        if (model.Depth is { Kind: DepthKind.Copy, CopyId: { } copyId })
        {
            if (option is { Knob: { } knob, Choice: { } choice }) SetKnobOnCopy(model, copyId, partId, property, knob, choice, option.Label);
            else BindOnCopy(model, copyId, partId, property, option.Value, label);
            return partId;
        }
        model.EditAtDepth(label, l =>
        {
            AddSource(model, l.Sources);
            if (l.Components.FirstOrDefault(c => c.Id == partId) is { } part && ComponentProperties.Find(part, property) is { } p)
                p.Set(part, option.Value);
        });
        return partId;
    }

    /// <summary>At copy depth the source belongs to the layout, not the copy (a copy's overrides can
    /// change its sources' settings but not add one), so this is one <see cref="DesignerModel.Edit"/>
    /// writing the override and the layout source together, rather than <c>EditAtDepth</c>, whose
    /// commit could only carry the first. A value equal to the widget's own loses its override, as
    /// <see cref="Overrides.Diff"/> would do.</summary>
    private void BindOnCopy(DesignerModel model, string copyId, string partId, string property, PropertyValue value, string label)
    {
        if (model.Layout.Copies?.Find(c => c.Id == copyId) is not { } copy || model.Finder()(copy.Widget) is not { } template) return;
        var key = OverrideKey(copyId, partId, property);
        var baseline = KnobSets.Get(WidgetExpander.Baseline(template, copy.Knobs), key);
        var matchesWidget = baseline is not null && Overrides.Same(baseline, value);
        var current = copy.Overrides.GetValueOrDefault(key);
        var unchanged = matchesWidget ? current is null : current is not null && Overrides.Same(current, value);
        if (unchanged && !MissingSource(model)) return;
        model.Edit(label, l =>
        {
            AddSource(model, l.Sources);
            var c = l.Copies!.Find(x => x.Id == copyId)!;
            if (matchesWidget) c.Overrides.Remove(key);
            else c.Overrides[key] = value;
        });
        Note = matchesWidget
            ? $"{Value.Label} is the widget's own again on this copy: its override is gone."
            : $"{OverridePrefix}: {Value.Label}. The widget and its other copies are unchanged; Reset in the properties panel takes it off.";
    }

    /// <summary>The knob route at copy depth: the copy's knob takes the choice (one undo entry), and
    /// an override of the same property goes, or it would go on hiding what the knob now says.</summary>
    private void SetKnobOnCopy(DesignerModel model, string copyId, string partId, string property, string knobId, string choice, string label)
    {
        if (Copies.Find(model.Layout, copyId) is not { } copy || Copies.TryFind(model.Finder(), copy.Widget) is not { } template) return;
        var key = OverrideKey(copyId, partId, property);
        model.Edit(label, l =>
        {
            Copies.SetKnob(l, template, copyId, knobId, choice);
            Copies.Find(l, copyId)!.Overrides.Remove(key);
        });
        Note = label + " on this copy.";
    }

    /// <summary>The sources the value's source would join: the widget's at widget depth, else the
    /// layout's.</summary>
    private bool MissingSource(DesignerModel model)
    {
        if (Value.Source is not { } def) return false;
        var sources = model.Depth.Kind == DepthKind.Widget ? model.Parts.Sources : model.Layout.Sources;
        return !sources.Exists(s => string.Equals(s.Name, def.Name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Adds a copy of the value's source to <paramref name="into"/> unless one of that name
    /// is already there. <paramref name="into"/> is what the edit is mutating, so a second drop of
    /// the same value, or of another value from the same source, finds it and adds nothing.</summary>
    private void AddSource(DesignerModel model, List<SourceDef> into)
    {
        if (Value.Source is not { } def || !MissingSource(model)) return;
        if (into.Exists(s => string.Equals(s.Name, def.Name, StringComparison.OrdinalIgnoreCase))) return;
        into.Add(new SourceDef { Name = def.Name, Type = def.Type, EverySeconds = def.EverySeconds, Settings = new(def.Settings) });
    }

    // ---- planning ------------------------------------------------------------------------------

    private static IReadOnlyList<DropOption> CanvasOptions(DesignerModel model, ValueEntry value)
    {
        if (model.Depth.Kind == DepthKind.Copy) return [];
        var shapes = value.Kind switch
        {
            ValueKind.Fraction => new[]
            {
                new DropOption("Dial", new DialDef { Id = "dial", Rect = new Rect(0, 0, 80, 80), Fraction = Bound(value, null) }, null, Bound(value, null)),
                new DropOption("Bar", new BarDef { Id = "bar", Rect = new Rect(0, 0, 120, 6), Fraction = Bound(value, null) }, null, Bound(value, null)),
            },
            ValueKind.Timestamp =>
            [
                new DropOption("Clock", new TextDef
                {
                    Id = "clock", Rect = new Rect(0, 0, 172, 78), Text = Bound(value, FormatPresets.Time),
                    Font = PropertyValue.Literal("Segoe UI Light"), Size = PropertyValue.Literal(64), Weight = PropertyValue.Literal(300),
                }, null, Bound(value, FormatPresets.Time)),
            ],
            _ => [],
        };
        return [.. shapes, .. TextOptions(value)];
    }

    /// <summary>Centred in its box, so the text lands where it was dropped (on a dial, in its middle).</summary>
    private static IReadOnlyList<DropOption> TextOptions(ValueEntry value)
        => FormatPresets.For(value.Kind).Select(p => new DropOption("Text " + p.Label,
            new TextDef { Id = "text", Rect = new Rect(0, 0, 120, 24), Text = Bound(value, p.Format), Align = PropertyValue.Literal("center") }, null, Bound(value, p.Format))).ToList();

    /// <summary>The Fraction property takes only a documented 0..1 value: a byte count bound to an
    /// arc would draw full at every size. Everything else is <see cref="ValueCatalog.Fits"/>.</summary>
    private static bool Fits(PropertySchema.Prop p, ValueKind kind)
        => p.Name == "Fraction" ? kind == ValueKind.Fraction : ValueCatalog.Fits(p.Editor, kind);

    private static PropertyValue Bound(ValueEntry value, string? format)
        => PropertyValue.Bound(Binding.Parse(value.Path) with { Format = format });

    private static bool SamePath(Binding b, string path) => (b with { Format = null }).ToString() == Binding.Parse(path).ToString();

    private static ComponentDef Clone(ComponentDef c)
        => LayoutFile.Parse(new LayoutFile { BaseImage = "", Components = [c] }.ToJson()).Components[0];
}
