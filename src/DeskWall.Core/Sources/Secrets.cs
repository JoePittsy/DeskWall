using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

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

    private Dictionary<string, string> Load()
    {
        if (!File.Exists(path)) return new(StringComparer.OrdinalIgnoreCase);
        var d = JsonSerializer.Deserialize(File.ReadAllText(path), SecretsJsonContext.Default.DictionaryStringString) ?? new();
        return new(d, StringComparer.OrdinalIgnoreCase);
    }
}

[JsonSerializable(typeof(Dictionary<string, string>))]
internal partial class SecretsJsonContext : JsonSerializerContext;
