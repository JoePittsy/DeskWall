using System.Globalization;
using System.Text.RegularExpressions;
using DeskWall.Core.Layout;

namespace DeskWall.Designer.Model.Widgets;

public enum SourceFieldEditor { Text, Number, Choice }

/// <summary>One row of the "+ Source" form. <see cref="Key"/> is the setting key it writes,
/// except for the two that are not settings: <c>name</c> and <c>every</c>. <see cref="Hint"/> is
/// the row's tooltip, for the two path fields where what may be typed is not obvious.</summary>
public sealed record SourceField(
    string Key,
    string Label,
    SourceFieldEditor Editor,
    string Default,
    bool Required = false,
    IReadOnlyList<string>? Choices = null,
    string? Hint = null);

/// <summary>
/// What the widget editor asks for when a source is added, as a table rather than three
/// hand-written panels: the fields per type, their defaults, and the conversion both ways between
/// a form's values and a <see cref="SourceDef"/>.
/// <para>
/// Deliberately not on the form: <c>header.&lt;Name&gt;</c> lines and <c>unixTimeFields</c>. They
/// are for a feed the owner is already reading documentation for, not for the ten-second path this
/// form exists to serve, and <see cref="ToSourceDef(string, IReadOnlyDictionary{string, string}, SourceDef?)"/>
/// carries them through untouched so a duplicated shipped widget keeps them.
/// </para>
/// </summary>
public static class SourceForms
{
    /// <summary>The pseudo-field for <see cref="SourceDef.Name"/>.</summary>
    public const string NameKey = "name";

    /// <summary>The pseudo-field for <see cref="SourceDef.EverySeconds"/>. Not a setting: every
    /// source factory reads the def's own field and ignores a settings entry of this name.</summary>
    public const string EveryKey = "every";

    /// <summary>What a choice field shows for "no opinion". The key is then not written at all, so
    /// the source's own default (content type, file extension, first character of stdout) decides.</summary>
    public const string Auto = "auto";

    /// <summary>Added with one click and no form: there is nothing to ask.</summary>
    public static IReadOnlyList<string> BuiltIn { get; } = ["time", "disks", "system", "hardware", "audio"];

    /// <summary>The user's own three, which open the form.</summary>
    public static IReadOnlyList<string> Configurable { get; } = ["http", "command", "file"];

    private static readonly SourceField[] None = [];

    /// <summary>What a path field may hold. <c>runtime:</c> keeps a widget portable: the two
    /// dogfood command widgets carried this machine's own
    /// "C:\Users\&lt;name&gt;\AppData\Local\DeskWall\scripts" because the form suggested nothing
    /// else, and a template with that in it works on exactly one PC.</summary>
    private const string PathHint =
        "runtime: is the DeskWall folder (%LOCALAPPDATA%\\DeskWall), so runtime:scripts travels with the widget. "
        + "%USERPROFILE% and other environment variables expand too, and a plain absolute path still works.";

    private static readonly SourceField[] Http =
    [
        new(NameKey, "Name", SourceFieldEditor.Text, "http", Required: true),
        new("url", "Url", SourceFieldEditor.Text, "https://api.example.com/data.json", Required: true),
        new(EveryKey, "Every (s)", SourceFieldEditor.Number, "600"),
        new("parse", "Parse", SourceFieldEditor.Choice, Auto, Choices: [Auto, "json", "text"]),
        new("timeout", "Timeout (s)", SourceFieldEditor.Number, "10"),
    ];

    private static readonly SourceField[] Command =
    [
        new(NameKey, "Name", SourceFieldEditor.Text, "command", Required: true),
        new("command", "Command", SourceFieldEditor.Text, "cmd.exe", Required: true),
        new("args", "Arguments", SourceFieldEditor.Text, "/c echo Hello from DeskWall"),
        new("workingDir", "Working folder", SourceFieldEditor.Text, "runtime:scripts", Hint: PathHint),
        new(EveryKey, "Every (s)", SourceFieldEditor.Number, "600"),
        new("timeout", "Timeout (s)", SourceFieldEditor.Number, "10"),
        new("parse", "Parse", SourceFieldEditor.Choice, Auto, Choices: [Auto, "json", "text"]),
    ];

    private static readonly SourceField[] File =
    [
        new(NameKey, "Name", SourceFieldEditor.Text, "file", Required: true),
        new("path", "File", SourceFieldEditor.Text, "runtime:data.json", Required: true, Hint: PathHint),
        new(EveryKey, "Every (s)", SourceFieldEditor.Number, "30"),
        new("parse", "Parse", SourceFieldEditor.Choice, Auto, Choices: [Auto, "json", "text", "rss"]),
    ];

    public static IReadOnlyList<SourceField> For(string type) => (type ?? "").ToLowerInvariant() switch
    {
        "http" => Http,
        "command" => Command,
        "file" => File,
        _ => None,
    };

    public static Dictionary<string, string> Defaults(string type)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in For(type)) values[f.Key] = f.Default;
        if (!values.ContainsKey(NameKey)) values[NameKey] = type;
        return values;
    }

    /// <summary>The form's values as a source. <paramref name="existing"/>, when given, supplies
    /// the settings the form does not show, so editing a duplicated shipped widget's source does
    /// not quietly drop its headers.</summary>
    public static SourceDef ToSourceDef(string type, IReadOnlyDictionary<string, string> values, SourceDef? existing = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        var fields = For(type);
        var settings = existing is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(existing.Settings, StringComparer.Ordinal);

        foreach (var f in fields)
        {
            if (f.Key is NameKey or EveryKey) continue;
            var text = (values.GetValueOrDefault(f.Key) ?? "").Trim();
            var omit = text.Length == 0
                || (f.Editor == SourceFieldEditor.Choice && string.Equals(text, Auto, StringComparison.OrdinalIgnoreCase));
            if (omit) settings.Remove(f.Key); else settings[f.Key] = text;
        }

        var name = (values.GetValueOrDefault(NameKey) ?? "").Trim();
        return new SourceDef
        {
            Name = name.Length == 0 ? type : name,
            Type = type,
            EverySeconds = ParseEvery(values.GetValueOrDefault(EveryKey)),
            Settings = settings,
        };
    }

    public static Dictionary<string, string> FromSourceDef(SourceDef def)
    {
        ArgumentNullException.ThrowIfNull(def);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in For(def.Type))
        {
            values[f.Key] = f.Key switch
            {
                NameKey => def.Name,
                EveryKey => def.EverySeconds?.ToString(CultureInfo.InvariantCulture) ?? "",
                _ => def.Settings.TryGetValue(f.Key, out var v) ? v
                    : f.Editor == SourceFieldEditor.Choice ? Auto : "",
            };
        }
        if (!values.ContainsKey(NameKey)) values[NameKey] = def.Name;
        return values;
    }

    /// <summary>The first thing wrong with the form, in the owner's words, or null when it is
    /// ready to add. <paramref name="otherNames"/> is every source name already in the document
    /// except the one being edited.</summary>
    public static string? Validate(string type, IReadOnlyDictionary<string, string> values, IEnumerable<string> otherNames)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(otherNames);
        var name = (values.GetValueOrDefault(NameKey) ?? "").Trim();
        if (name.Length == 0) return "A source needs a name.";
        if (!NamePattern.IsMatch(name)) return "A source name is lower-case letters and digits, starting with a letter.";
        if (otherNames.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
            return $"There is already a source called '{name}'.";

        foreach (var f in For(type))
        {
            if (!f.Required || f.Key == NameKey) continue;
            if ((values.GetValueOrDefault(f.Key) ?? "").Trim().Length == 0) return $"{f.Label} is needed.";
        }
        return null;
    }

    private static readonly Regex NamePattern = new("^[a-z][a-z0-9]*$", RegexOptions.CultureInvariant);

    private static int? ParseEvery(string? text)
        => int.TryParse((text ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : null;
}
