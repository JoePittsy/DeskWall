using System.Globalization;
using System.Text.RegularExpressions;
using DeskWall.Core.Bindings;
using DeskWall.Core.Layout;

namespace DeskWall.Core.Widgets;

/// <summary>The knob <c>sets</c> path grammar (<c>docs/layout-format.md</c> "Knobs and the
/// <c>sets</c> grammar" and "Copies"), executed against a widget's parts: a <see cref="LayoutFile"/>
/// holding a clone of the template's sources and components with their template-local ids and
/// rects. One executor for knobs and overrides alike. Ported from the designer's
/// <c>WidgetInstance.ApplySet</c> / <c>ApplyTokenGroup</c> / <c>Substitute</c>, plus the
/// four-segment repeater-child form and the <c>rect</c>/<c>z</c>/<c>hidden</c> pseudo-properties.
/// <list type="bullet">
/// <item><c>components.&lt;partId&gt;.&lt;property&gt;</c></item>
/// <item><c>components.&lt;repeaterId&gt;.&lt;childId&gt;.&lt;property&gt;</c></item>
/// <item><c>sources.&lt;name&gt;.settings.&lt;key&gt;</c>, <c>sources.&lt;name&gt;.every</c></item>
/// </list></summary>
public static partial class KnobSets
{
    private const string PartSeparator = "||";
    private const string BindMarker = "=bind:";

    [GeneratedRegex(@":\{(\w+)\}$")]
    private static partial Regex TokenSuffix();

    /// <summary>Writes <paramref name="value"/> at <paramref name="path"/>. False when the path
    /// names nothing the parts have (an orphan), or a binding where only a literal can go.
    /// <c>hidden</c> = <c>true</c> removes the part.</summary>
    public static bool Apply(LayoutFile parts, string path, PropertyValue value)
    {
        switch (Resolve(parts, path))
        {
            case PartProp t:
                t.Prop.Set(t.Part, value);
                return true;
            case PartHidden t:
                if (!value.IsBound && string.Equals(value.LiteralText, "true", StringComparison.OrdinalIgnoreCase)) t.Owner.Remove(t.Part);
                return true;
            case Setting t when !value.IsBound:
                t.Source.Settings[t.Key] = value.LiteralText ?? "";
                return true;
            case Every t when !value.IsBound:
                // The refresh interval is SourceDef's own field, not a setting. A value that is not a
                // positive whole number leaves it alone rather than reverting it to the type's default.
                if (int.TryParse(value.LiteralText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
                    t.Source.EverySeconds = seconds;
                return true;
            default:
                return false;
        }
    }

    /// <summary>The current value at <paramref name="path"/>, or null when it names nothing. An
    /// absent setting reads as "", a part's <c>hidden</c> as "false".</summary>
    public static PropertyValue? Get(LayoutFile parts, string path) => Resolve(parts, path) switch
    {
        PartProp t => t.Prop.Get(t.Part),
        PartHidden => PropertyValue.Literal("false"),
        Setting t => PropertyValue.Literal(t.Source.Settings.TryGetValue(t.Key, out var v) ? v : ""),
        Every t => PropertyValue.Literal(t.Source.EverySeconds?.ToString(CultureInfo.InvariantCulture) ?? ""),
        _ => null,
    };

    /// <summary>Whether one <c>sets</c> entry names something <paramref name="parts"/> has: its
    /// path, without a <c>=bind:</c> or <c>:{token}</c> suffix, resolves. A knob entry that does
    /// not is what the expander reports as <see cref="ExpandProblemKind.OrphanKnob"/>.</summary>
    public static bool Names(LayoutFile parts, string set)
    {
        ArgumentNullException.ThrowIfNull(set);
        var bindAt = set.IndexOf(BindMarker, StringComparison.Ordinal);
        var path = bindAt >= 0 ? set[..bindAt] : TokenSuffix().Match(set) is { Success: true } token ? set[..token.Index] : set;
        return Resolve(parts, path) is not null;
    }

    /// <summary>Applies one knob's <c>sets</c> for <paramref name="value"/> (plain, or a composite
    /// joined by <c>||</c>: the i-th entry takes part i + 1, else part 0). A <c>:{token}</c> entry
    /// substitutes into <paramref name="template"/>'s own, still-placeholder-bearing value, and every
    /// token entry aimed at the same target is applied together in one pass, so Town's "{lat}" and
    /// "{lon}" both land in the one URL. A bound target stays bound (the Drive knob). Returns the
    /// <c>sets</c> entries that found no target; empty when the knob applied in full.</summary>
    public static IReadOnlyList<string> ApplyKnob(LayoutFile parts, WidgetTemplate template, Knob knob, string value)
    {
        var failed = new List<string>();
        var values = value.Split(PartSeparator);
        var tokenGroups = new List<(string Target, List<(string Token, string Part)> Tokens)>();
        for (var i = 0; i < knob.Sets.Count; i++)
        {
            var part = values.Length > i + 1 ? values[i + 1] : values[0];
            var setPath = knob.Sets[i];
            var token = TokenSuffix().Match(setPath);
            if (token.Success)
            {
                var target = setPath[..token.Index];
                var group = tokenGroups.FindIndex(g => g.Target == target);
                if (group < 0) tokenGroups.Add((target, [(token.Groups[1].Value, part)]));
                else tokenGroups[group].Tokens.Add((token.Groups[1].Value, part));
                continue;
            }
            // "=bind:" writes a binding parsed from the knob value; the text after the marker only
            // documents the default's shape for a human and is never parsed.
            var bindAt = setPath.IndexOf(BindMarker, StringComparison.Ordinal);
            var path = bindAt >= 0 ? setPath[..bindAt] : setPath;
            var v = bindAt >= 0 ? TryBind(part) : PropertyValue.Literal(part);
            if (v is null || !Apply(parts, path, v)) failed.Add(setPath);
        }

        if (tokenGroups.Count == 0) return failed;
        var original = new LayoutFile { BaseImage = "", Sources = [.. template.Sources], Components = [.. template.Components] };
        foreach (var (target, tokens) in tokenGroups)
        {
            var authored = Get(original, target);
            var v = authored is null ? null
                : authored.IsBound ? TryBind(Substitute(authored.Binding!.ToString(), tokens))
                : PropertyValue.Literal(Substitute(authored.LiteralText ?? "", tokens));
            if (v is null || !Apply(parts, target, v)) failed.Add(target);
        }
        return failed;
    }

    private static PropertyValue? TryBind(string text)
    {
        try { return PropertyValue.Bound(Binding.Parse(text)); }
        catch (FormatException) { return null; }
    }

    private static string Substitute(string text, List<(string Token, string Part)> tokens)
    {
        foreach (var (token, part) in tokens) text = text.Replace("{" + token + "}", part, StringComparison.Ordinal);
        return text;
    }

    // ---- path resolution ----------------------------------------------------------------------

    private abstract record Target;
    private sealed record PartProp(ComponentDef Part, ComponentProperties.Prop Prop) : Target;
    private sealed record PartHidden(List<ComponentDef> Owner, ComponentDef Part) : Target;
    private sealed record Setting(SourceDef Source, string Key) : Target;
    private sealed record Every(SourceDef Source) : Target;

    private static Target? Resolve(LayoutFile parts, string path)
    {
        var s = path.Split('.');
        if (s[0] == "components" && s.Length is 3 or 4)
        {
            var owner = parts.Components;
            var part = owner.Find(c => c.Id == s[1]);
            if (s.Length == 4)
            {
                if (part is not RepeaterDef repeater) return null;
                owner = repeater.Template;
                part = owner.Find(c => c.Id == s[2]);
            }
            if (part is null) return null;
            if (string.Equals(s[^1], ComponentProperties.Hidden, StringComparison.OrdinalIgnoreCase)) return new PartHidden(owner, part);
            var prop = ComponentProperties.Find(part, s[^1]);
            return prop is null ? null : new PartProp(part, prop);
        }
        if (s[0] == "sources" && s.Length >= 3 && parts.Sources.Find(x => x.Name == s[1]) is { } source)
        {
            if (s.Length == 3 && s[2] == "every") return new Every(source);
            if (s.Length >= 4 && s[2] == "settings") return new Setting(source, string.Join('.', s[3..]));
        }
        return null;
    }
}
