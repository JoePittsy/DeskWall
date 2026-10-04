using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DeskWall.Core.Values;

namespace DeskWall.Core.Sources;

/// <summary>secrets.json in the runtime dir: { "steamKey": "...", ... }. Values are substituted for
/// {secret:name} placeholders at request time and never logged.</summary>
public sealed partial class Secrets(string path)
{
    private Dictionary<string, string>? _map;

    public static Secrets Default() => new(Paths.InRuntime("secrets.json"));

    [GeneratedRegex(@"\{secret:([A-Za-z0-9_\-]+)\}")]
    private static partial Regex Placeholder();

    public static bool ContainsPlaceholder(string text) => Placeholder().IsMatch(text);

    public string? Get(string name)
    {
        _map ??= Load();
        return _map.TryGetValue(name, out var v) ? v : null;
    }

    /// <summary>Replace every {secret:name}. Unknown names throw KeyNotFoundException naming the secret, never its value.</summary>
    public string Substitute(string text)
        => Placeholder().Replace(text, m => Get(m.Groups[1].Value) ?? throw new KeyNotFoundException($"secret '{m.Groups[1].Value}' is not defined in secrets.json"));

    /// <summary>Substitute run backwards: every value <paramref name="template"/> would have
    /// substituted is put back as its {secret:name} placeholder in <paramref name="text"/>. For what
    /// comes back from something a secret was handed to (a command's output echoing its own
    /// arguments), so it reaches a value as the template, the way HttpSource's errors carry the url
    /// template. Only the template's own secrets: a value is not a secret to this caller unless it
    /// was given out. Longest first, so a secret containing another is not half-replaced; an empty
    /// or undefined secret is skipped rather than matching everywhere or throwing.</summary>
    public string Redact(string text, string? template)
    {
        if (template is null || text.Length == 0) return text;
        var given = Placeholder().Matches(template)
            .Select(m => (Name: m.Groups[1].Value, Value: Get(m.Groups[1].Value)))
            .Where(s => !string.IsNullOrEmpty(s.Value))
            .DistinctBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(s => s.Value!.Length);
        foreach (var (name, value) in given) text = text.Replace(value!, $"{{secret:{name}}}", StringComparison.Ordinal);
        return text;
    }

    /// <summary><see cref="Redact(string, string?)"/> over every string in a value tree. Numbers,
    /// times and flags are left alone: they are parsed, not echoed.</summary>
    public Value Redact(Value value, string? template) => template is null || !ContainsPlaceholder(template) ? value : value switch
    {
        TextValue t => new TextValue(Redact(t.Text, template)),
        RecordValue r => Redact(r, template),
        ListValue l => new ListValue([.. l.Items.Select(i => Redact(i, template))], l.KeyField),
        _ => value,
    };

    public RecordValue Redact(RecordValue record, string? template)
        => template is null || !ContainsPlaceholder(template) ? record
            : new RecordValue(record.Fields.ToDictionary(kv => kv.Key, kv => Redact(kv.Value, template), StringComparer.OrdinalIgnoreCase));

    private Dictionary<string, string> Load()
    {
        if (!File.Exists(path)) return new(StringComparer.OrdinalIgnoreCase);
        var d = JsonSerializer.Deserialize(File.ReadAllText(path), SecretsJsonContext.Default.DictionaryStringString) ?? new();
        return new(d, StringComparer.OrdinalIgnoreCase);
    }
}

[JsonSerializable(typeof(Dictionary<string, string>))]
internal partial class SecretsJsonContext : JsonSerializerContext;
