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
    /// <summary>What the shortcut manager last reconciled the desktop to (slots, rects, targets,
    /// tooltips, arrow). Empty when shortcuts have never been placed, or the last attempt failed.</summary>
    public string ShortcutsFingerprint { get; set; } = "";
    public string FramePath { get; set; } = "";
    /// <summary>BaseCache.KeyFor of the base image at the last render. Finding 12: a mismatch here
    /// means the base image was replaced in place, so the skip gate must not skip.</summary>
    public string BaseKey { get; set; } = "";

    public static FrameState Load(string path)
    {
        if (!File.Exists(path)) return new();
        try { return JsonSerializer.Deserialize(File.ReadAllText(path), FrameStateJsonContext.Default.FrameState) ?? new(); }
        catch (JsonException) { return new(); }
    }

    public void Save(string path)
    {
        var tmp = path + ".tmp";
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, FrameStateJsonContext.Default.FrameState));
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // Finding 10: leave no <path>.tmp behind on a failing save.
            try { File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }
}

[JsonSerializable(typeof(FrameState))]
internal partial class FrameStateJsonContext : JsonSerializerContext;
