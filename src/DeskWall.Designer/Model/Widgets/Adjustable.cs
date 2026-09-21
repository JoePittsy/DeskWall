using DeskWall.Core.Layout;

namespace DeskWall.Designer.Model.Widgets;

/// <summary>One property or one source setting the widget's author has exposed as a knob. A knob
/// built from this writes a literal to exactly one place: no composites, no <c>{token}</c>
/// splicing. Those still exist in shipped widgets and pass through the editor untouched
/// (<see cref="WidgetDocument.PassThroughKnobs"/>).
/// <para>Exactly one of the two pairs is set: (<see cref="ComponentId"/>, <see cref="Property"/>)
/// or (<see cref="SourceName"/>, <see cref="SettingKey"/>).</para></summary>
public sealed class AdjustableTarget
{
    private AdjustableTarget(string? componentId, string? property, string? sourceName, string? settingKey, string id, string label)
    {
        ComponentId = componentId;
        Property = property;
        SourceName = sourceName;
        SettingKey = settingKey;
        Id = id;
        Label = label;
    }

    public string? ComponentId { get; }
    /// <summary>The <see cref="PropertySchema.Prop.Name"/>, in its schema casing ("EffectRadius").</summary>
    public string? Property { get; }
    public string? SourceName { get; }
    public string? SettingKey { get; }

    /// <summary>The knob id written to the file. Derived from the label when the target is first
    /// exposed and then kept, so re-saving a shipped widget does not renumber ids that a label
    /// never matched exactly ("Arguments" -> <c>args</c>, "Warn at" -> <c>warnAt</c>).</summary>
    public string Id { get; set; }

    public string Label { get; set; }
    public double? Min { get; set; }
    public double? Max { get; set; }

    public bool IsComponent => ComponentId is not null;

    public static AdjustableTarget ForComponent(string componentId, string property, string label, string? id = null)
        => new(componentId, property, null, null, id ?? WidgetDocument.Slug(label), label);

    public static AdjustableTarget ForSource(string sourceName, string settingKey, string label, string? id = null)
        => new(null, null, sourceName, settingKey, id ?? WidgetDocument.Slug(label), label);

    /// <summary>What the knob's <c>sets</c> entry says (docs/layout-format.md, "Widgets").</summary>
    public string SetsPath => IsComponent
        ? $"components.{ComponentId}.{Property!.ToLowerInvariant()}"
        : string.Equals(SettingKey, Adjustable.EveryKey, StringComparison.Ordinal)
            ? $"sources.{SourceName}.every"
            : $"sources.{SourceName}.settings.{SettingKey}";

    /// <summary>True when this is the same place in the document as the arguments name.</summary>
    public bool Targets(string componentId, string property)
        => IsComponent && ComponentId == componentId && string.Equals(Property, property, StringComparison.OrdinalIgnoreCase);

    public bool TargetsSetting(string sourceName, string settingKey)
        => !IsComponent && SourceName == sourceName && string.Equals(SettingKey, settingKey, StringComparison.Ordinal);
}

/// <summary>Turning a target into the knob a template file carries, and deciding which targets may
/// become one at all.</summary>
public static class Adjustable
{
    /// <summary>The one source field that is not a setting: <c>SourceDef.EverySeconds</c>. Its
    /// <c>sets</c> path is <c>sources.&lt;name&gt;.every</c>, not <c>...settings.every</c>, because
    /// a value in <c>settings</c> would be silently ignored by every source factory.</summary>
    public const string EveryKey = "every";

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

    /// <summary>The knob for this target as the document stands <em>now</em>. Null when the target
    /// has gone (the part was deleted, the source removed) or has become a binding: the default is
    /// read here and nowhere else, so there is no stored copy to keep in step with the value.</summary>
    public static Knob? ToKnob(WidgetDocument document, AdjustableTarget target)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(target);
        var layout = document.Model.Layout;

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
