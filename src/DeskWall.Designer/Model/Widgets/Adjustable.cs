using DeskWall.Core.Bindings;
using DeskWall.Core.Layout;

namespace DeskWall.Designer.Model.Widgets;

/// <summary>One property or one source setting the widget's author has exposed as a knob. A knob
/// built from this writes a literal to exactly one place: no composites, no <c>{token}</c>
/// splicing. Those still exist in shipped widgets and pass through the editor untouched
/// (<see cref="WidgetDocument.PassThroughKnobs"/>).
/// <para>Exactly one of the two pairs is set: (<see cref="ComponentId"/>, <see cref="Property"/>)
/// or (<see cref="SourceName"/>, <see cref="SettingKey"/>).</para>
/// <para>The one exception is a <b>Drive</b> target (<see cref="IsDrive"/>), the editor's only
/// knob over a binding: it carries a list of component properties rather than one, because a
/// drive widget's bar and its caption have to move together and two pickers for one drive is two
/// ways to get it wrong.</para></summary>
public sealed class AdjustableTarget
{
    private AdjustableTarget(string? componentId, string? property, string? sourceName, string? settingKey, string id, string label)
    {
        _componentId = componentId;
        _property = property;
        SourceName = sourceName;
        SettingKey = settingKey;
        Id = id;
        Label = label;
    }

    private readonly string? _componentId;
    private readonly string? _property;
    private readonly List<(string ComponentId, string Property)> _driveTargets = [];

    /// <summary>A Drive knob: <see cref="DriveTargets"/> is the list it writes, and its first
    /// entry is what <see cref="ComponentId"/>/<see cref="Property"/> report.</summary>
    public bool IsDrive { get; private init; }

    /// <summary>Drive knobs only: every component property this one knob repoints, in the order
    /// they were exposed. Empty for every other kind of target.</summary>
    public IReadOnlyList<(string ComponentId, string Property)> DriveTargets => _driveTargets;

    public string? ComponentId => IsDrive ? First().ComponentId : _componentId;
    /// <summary>The <see cref="PropertySchema.Prop.Name"/>, in its schema casing ("EffectRadius").</summary>
    public string? Property => IsDrive ? First().Property : _property;
    public string? SourceName { get; }
    public string? SettingKey { get; }

    /// <summary>The knob id written to the file. Derived from the label when the target is first
    /// exposed and then kept, so re-saving a shipped widget does not renumber ids that a label
    /// never matched exactly ("Arguments" -> <c>args</c>, "Warn at" -> <c>warnAt</c>).</summary>
    public string Id { get; set; }

    public string Label { get; set; }
    public double? Min { get; set; }
    public double? Max { get; set; }

    public bool IsComponent => IsDrive || _componentId is not null;

    public static AdjustableTarget ForComponent(string componentId, string property, string label, string? id = null)
        => new(componentId, property, null, null, id ?? WidgetDocument.Slug(label), label);

    public static AdjustableTarget ForSource(string sourceName, string settingKey, string label, string? id = null)
        => new(null, null, sourceName, settingKey, id ?? WidgetDocument.Slug(label), label);

    public static AdjustableTarget ForDrive(string componentId, string property, string label, string? id = null)
    {
        var target = new AdjustableTarget(null, null, null, null, id ?? WidgetDocument.Slug(label), label) { IsDrive = true };
        target.AddDriveTarget(componentId, property);
        return target;
    }

    /// <summary>Put another component property under this same Drive knob.</summary>
    public void AddDriveTarget(string componentId, string property)
    {
        if (!IsDrive || Targets(componentId, property)) return;
        _driveTargets.Add((componentId, property));
    }

    /// <summary>Take one component property back off this Drive knob, leaving the rest. The knob
    /// itself goes only when the last one does, which <see cref="WidgetDocument"/> decides.</summary>
    public void RemoveDriveTarget(string componentId, string property)
        => _driveTargets.RemoveAll(t => t.ComponentId == componentId && string.Equals(t.Property, property, StringComparison.OrdinalIgnoreCase));

    /// <summary>Replace the target list with the ones still worth writing (see
    /// <see cref="Adjustable.LiveDriveTargets"/>).</summary>
    public void KeepDriveTargets(IReadOnlyList<(string ComponentId, string Property)> keep)
    {
        ArgumentNullException.ThrowIfNull(keep);
        if (!IsDrive) return;
        _driveTargets.Clear();
        _driveTargets.AddRange(keep);
    }

    /// <summary>What the knob's <c>sets</c> entry says (docs/layout-format.md, "Widgets").</summary>
    public string SetsPath => IsComponent
        ? $"components.{ComponentId}.{Property!.ToLowerInvariant()}"
        : string.Equals(SettingKey, Adjustable.EveryKey, StringComparison.Ordinal)
            ? $"sources.{SourceName}.every"
            : $"sources.{SourceName}.settings.{SettingKey}";

    /// <summary>Every <c>sets</c> entry this target writes: one, except for a Drive knob, which
    /// writes a <c>:{drive}</c> substitution per component property it holds.</summary>
    public IReadOnlyList<string> SetsPaths => IsDrive
        ? _driveTargets.Select(t => Adjustable.DriveSetsPath(t.ComponentId, t.Property)).ToList()
        : [SetsPath];

    /// <summary>True when this is the same place in the document as the arguments name.</summary>
    public bool Targets(string componentId, string property)
        => IsDrive
            ? _driveTargets.Any(t => t.ComponentId == componentId && string.Equals(t.Property, property, StringComparison.OrdinalIgnoreCase))
            : _componentId is not null && _componentId == componentId && string.Equals(_property, property, StringComparison.OrdinalIgnoreCase);

    public bool TargetsSetting(string sourceName, string settingKey)
        => !IsComponent && SourceName == sourceName && string.Equals(SettingKey, settingKey, StringComparison.Ordinal);

    private (string? ComponentId, string? Property) First()
        => _driveTargets.Count == 0 ? (null, null) : _driveTargets[0];
}

/// <summary>Turning a target into the knob a template file carries, and deciding which targets may
/// become one at all.</summary>
public static class Adjustable
{
    /// <summary>The one source field that is not a setting: <c>SourceDef.EverySeconds</c>. Its
    /// <c>sets</c> path is <c>sources.&lt;name&gt;.every</c>, not <c>...settings.every</c>, because
    /// a value in <c>settings</c> would be silently ignored by every source factory.</summary>
    public const string EveryKey = "every";

    /// <summary>The token a Drive knob substitutes, and the two path segments that say a binding
    /// is about one drive: <c>disks.drives[C]...</c>. Deliberately just this one shape -- making
    /// any part of any binding adjustable is a much bigger feature than a drive picker.</summary>
    public const string DriveToken = "drive";
    private const string DisksSource = "disks";
    private const string DrivesField = "drives";

    /// <summary>Which knob control a property gets. A <see cref="PropertySchema.Editor.Binding"/>
    /// property is not offered at all: a knob writes literals, and a repeater's item list is not a
    /// literal.</summary>
    public static KnobType? TypeFor(PropertySchema.Editor editor) => editor switch
    {
        PropertySchema.Editor.Text or PropertySchema.Editor.Font or PropertySchema.Editor.Path => KnobType.Text,
        PropertySchema.Editor.Number or PropertySchema.Editor.AutoNumber => KnobType.Number,
        PropertySchema.Editor.Color => KnobType.Color,
        PropertySchema.Editor.Enum => KnobType.Choice,
        _ => null,
    };

    /// <summary>A source setting is text, except the two that are durations.</summary>
    public static KnobType TypeForSetting(string settingKey)
        => settingKey is EveryKey or "timeout" ? KnobType.Number : KnobType.Text;

    /// <summary>Whether "Make adjustable" is offered for this property at all: only for a literal
    /// value in an editor a knob can write. A property that is currently bound is refused, because
    /// setting the knob would replace the binding with a literal without saying so.</summary>
    public static bool CanAdjust(ComponentDef def, PropertySchema.Prop prop)
    {
        ArgumentNullException.ThrowIfNull(prop);
        if (TypeFor(prop.Editor) is null) return false;
        return prop.Get(def) is not { IsBound: true };
    }

    // ---- drive knobs ---------------------------------------------------------------------------

    /// <summary>The drive a binding is keyed by -- the <c>C</c> of <c>disks.drives[C].freeGB</c>,
    /// or the <c>{drive}</c> a saved template carries there -- or null when the binding is about
    /// anything else. An index (<c>disks.drives[0]</c>) is not a drive: it names whichever drive
    /// happens to be first, which is not a thing a picker can set.</summary>
    public static string? DriveKey(Binding? binding)
    {
        if (binding is null || binding.Path.Count < 3) return null;
        if (binding.Path[0] is not NameSegment source || !string.Equals(source.Name, DisksSource, StringComparison.OrdinalIgnoreCase)) return null;
        if (binding.Path[1] is not NameSegment list || !string.Equals(list.Name, DrivesField, StringComparison.OrdinalIgnoreCase)) return null;
        return binding.Path[2] is KeySegment key ? key.Key : null;
    }

    /// <summary>Whether this property may join a Drive knob: it has to be bound, and bound to a
    /// drive-keyed path. Everything else bound stays refused (<see cref="CanAdjust"/>).</summary>
    public static bool CanAdjustAsDrive(ComponentDef def, PropertySchema.Prop prop)
    {
        ArgumentNullException.ThrowIfNull(prop);
        var value = prop.Get(def);
        return value is { IsBound: true } && DriveKey(value.Binding) is not null;
    }

    public static string DriveSetsPath(string componentId, string property)
        => "components." + componentId + "." + property.ToLowerInvariant() + ":{" + DriveToken + "}";

    /// <summary>The drive-knob targets still worth writing: the part is still on the canvas and
    /// its property is still bound to a drive-keyed path. Re-binding one of a drive widget's parts
    /// to something else takes that part off the knob without taking the knob away.</summary>
    public static IReadOnlyList<(string ComponentId, string Property)> LiveDriveTargets(WidgetDocument document, AdjustableTarget target)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(target);
        return target.DriveTargets.Where(t => KeyAt(document.Model.Layout.Components, t.ComponentId, t.Property) is not null).ToList();
    }

    /// <summary>Rewrite the drive key of every one of this knob's targets in
    /// <paramref name="components"/>. Used twice: with "{drive}" onto the copy
    /// <see cref="WidgetDocument.ToTemplate"/> is about to save, and with the knob's default
    /// letter onto a document just opened from a saved template, so its canvas draws real data
    /// instead of resolving a drive literally called "{drive}".</summary>
    public static void SetDriveKey(IReadOnlyList<ComponentDef> components, AdjustableTarget target, string key)
    {
        ArgumentNullException.ThrowIfNull(components);
        ArgumentNullException.ThrowIfNull(target);
        if (string.IsNullOrEmpty(key)) return;
        foreach (var (componentId, property) in target.DriveTargets)
        {
            var found = Find(components, componentId, property);
            if (found.Def is not { } def || found.Prop is not { } prop) continue;
            if (prop.Get(def) is not { IsBound: true } value || DriveKey(value.Binding) is null) continue;
            var path = value.Binding!.Path.ToList();
            path[2] = new KeySegment(key);
            prop.Set(def, PropertyValue.Bound(new Binding(path, value.Binding.Format)));
        }
    }

    private static string? KeyAt(IReadOnlyList<ComponentDef> components, string componentId, string property)
    {
        var found = Find(components, componentId, property);
        if (found.Def is not { } def || found.Prop is not { } prop) return null;
        return prop.Get(def) is { IsBound: true } value ? DriveKey(value.Binding) : null;
    }

    private static (ComponentDef? Def, PropertySchema.Prop? Prop) Find(IReadOnlyList<ComponentDef> components, string componentId, string property)
    {
        if (components.FirstOrDefault(c => c.Id == componentId) is not { } def) return (null, null);
        return (def, PropertySchema.For(def).FirstOrDefault(p => string.Equals(p.Name, property, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>The knob for this target as the document stands <em>now</em>. Null when the target
    /// has gone (the part was deleted, the source removed) or has become a binding: the default is
    /// read here and nowhere else, so there is no stored copy to keep in step with the value.</summary>
    public static Knob? ToKnob(WidgetDocument document, AdjustableTarget target)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(target);
        var layout = document.Model.Layout;

        if (target.IsDrive)
        {
            // The default is the letter the canvas is drawing right now, the same rule every
            // other knob follows; the file's "{drive}" is written by ToTemplate, not stored here.
            var live = LiveDriveTargets(document, target);
            if (live.Count == 0) return null;
            var letter = KeyAt(layout.Components, live[0].ComponentId, live[0].Property) ?? "";
            return new Knob(target.Id, target.Label, KnobType.Drive, letter,
                live.Select(t => DriveSetsPath(t.ComponentId, t.Property)).ToList(), null, null, null);
        }

        if (target.IsComponent)
        {
            if (layout.Components.FirstOrDefault(c => c.Id == target.ComponentId) is not { } def) return null;
            var prop = PropertySchema.For(def).FirstOrDefault(p => string.Equals(p.Name, target.Property, StringComparison.OrdinalIgnoreCase));
            if (prop is null || !CanAdjust(def, prop)) return null;
            var type = TypeFor(prop.Editor)!.Value;
            var current = prop.Get(def)?.LiteralText ?? "";
            var choices = prop.Editor == PropertySchema.Editor.Enum ? prop.Choices : null;
            return new Knob(target.Id, target.Label, type, Canonical(current, choices), [target.SetsPath],
                choices, target.Min, target.Max);
        }

        if (layout.Sources.FirstOrDefault(s => string.Equals(s.Name, target.SourceName, StringComparison.Ordinal)) is not { } source) return null;
        var value = string.Equals(target.SettingKey, EveryKey, StringComparison.Ordinal)
            ? source.EverySeconds?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? ""
            : source.Settings.GetValueOrDefault(target.SettingKey!, "");
        return new Knob(target.Id, target.Label, TypeForSetting(target.SettingKey!), value, [target.SetsPath],
            null, target.Min, target.Max);
    }

    /// <summary>A knob the editor can drive, or null if it is one of the shapes that has to pass
    /// through verbatim: more than one <c>sets</c> entry, a composite default, a <c>:{token}</c>
    /// substitution, a <c>=bind:</c> write, a target that is not one of the two simple paths, or a
    /// property this editor cannot write a literal to. Anything that comes back null is kept as it
    /// was written, so duplicating a shipped widget never loses a knob.</summary>
    public static AdjustableTarget? FromKnob(Knob knob, IReadOnlyList<ComponentDef> components)
    {
        ArgumentNullException.ThrowIfNull(knob);
        ArgumentNullException.ThrowIfNull(components);
        if (knob.Type == KnobType.Drive) return FromDriveKnob(knob, components);
        if (knob.Sets.Count != 1) return null;
        if (knob.Default.Contains("||", StringComparison.Ordinal)) return null;
        var path = knob.Sets[0];
        if (path.Contains(":{", StringComparison.Ordinal)) return null;
        if (path.Contains("=bind:", StringComparison.Ordinal)) return null;

        var segments = path.Split('.');
        if (segments.Length == 3 && segments[0] == "components")
        {
            if (components.FirstOrDefault(c => c.Id == segments[1]) is not { } def) return null;
            // The schema's own casing, not the path's: the panel shows this name beside the row.
            var prop = PropertySchema.For(def).FirstOrDefault(p => string.Equals(p.Name, segments[2], StringComparison.OrdinalIgnoreCase));
            if (prop is null || !CanAdjust(def, prop)) return null;
            return WithRange(AdjustableTarget.ForComponent(def.Id, prop.Name, knob.Label, knob.Id), knob);
        }
        if (segments.Length == 4 && segments[0] == "sources" && segments[2] == "settings")
            return WithRange(AdjustableTarget.ForSource(segments[1], segments[3], knob.Label, knob.Id), knob);
        if (segments.Length == 3 && segments[0] == "sources" && segments[2] == EveryKey)
            return WithRange(AdjustableTarget.ForSource(segments[1], EveryKey, knob.Label, knob.Id), knob);
        return null;
    }

    /// <summary>A Drive knob the editor can drive again: every <c>sets</c> entry is a
    /// <c>:{drive}</c> substitution into a component property that is bound to a drive-keyed path.
    /// One entry that is not makes the whole knob pass through, because half a drive knob would
    /// repoint half the widget.</summary>
    private static AdjustableTarget? FromDriveKnob(Knob knob, IReadOnlyList<ComponentDef> components)
    {
        const string prefix = "components.";
        var suffix = ":{" + DriveToken + "}";
        if (knob.Sets.Count == 0) return null;

        AdjustableTarget? target = null;
        foreach (var path in knob.Sets)
        {
            if (!path.StartsWith(prefix, StringComparison.Ordinal) || !path.EndsWith(suffix, StringComparison.Ordinal)) return null;
            var segments = path[..^suffix.Length].Split('.');
            if (segments.Length != 3) return null;
            var found = Find(components, segments[1], segments[2]);
            if (found.Def is not { } def || found.Prop is not { } prop || !CanAdjustAsDrive(def, prop)) return null;
            if (target is null) target = AdjustableTarget.ForDrive(def.Id, prop.Name, knob.Label, knob.Id);
            else target.AddDriveTarget(def.Id, prop.Name);
        }
        return target;
    }

    private static AdjustableTarget WithRange(AdjustableTarget target, Knob knob)
    {
        target.Min = knob.Min;
        target.Max = knob.Max;
        return target;
    }

    /// <summary>A choice knob's default has to be one of its own choices, and a component stores
    /// the same word in the casing the renderer parses ("right"). Map back to the choice list's
    /// spelling so the knobs panel preselects a row instead of falling back to the first one.</summary>
    private static string Canonical(string value, IReadOnlyList<string>? choices)
        => choices?.FirstOrDefault(c => string.Equals(c, value, StringComparison.OrdinalIgnoreCase)) ?? value;
}
