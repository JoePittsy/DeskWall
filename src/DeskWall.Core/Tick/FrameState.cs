using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeskWall.Core.Tick;

/// <summary>What the last rendered frame contained, so the next tick knows what changed.</summary>
public sealed class FrameState
{
    public Dictionary<string, string> KeysById { get; set; } = new();
    /// <summary>[x, y, w, h] per component id, for repainting the base where a component vanished.</summary>
    public Dictionary<string, int[]> RectsById { get; set; } = new();
    public string SignatureKey { get; set; } = "";
    public string FramePath { get; set; } = "";

    public static FrameState Load(string path)
    {
        if (!File.Exists(path)) return new();
        try { return JsonSerializer.Deserialize(File.ReadAllText(path), FrameStateJsonContext.Default.FrameState) ?? new(); }
        catch (JsonException) { return new(); }
    }

    public void Save(string path)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, FrameStateJsonContext.Default.FrameState));
        File.Move(tmp, path, overwrite: true);
    }
}

[JsonSerializable(typeof(FrameState))]
internal partial class FrameStateJsonContext : JsonSerializerContext;
