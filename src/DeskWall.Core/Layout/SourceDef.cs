using System.Text.Json.Serialization;

namespace DeskWall.Core.Layout;

public sealed class SourceDef
{
    public required string Name { get; set; }
    public required string Type { get; set; }
    [JsonPropertyName("every")] public int? EverySeconds { get; set; }
    public Dictionary<string, string> Settings { get; set; } = new();
}
