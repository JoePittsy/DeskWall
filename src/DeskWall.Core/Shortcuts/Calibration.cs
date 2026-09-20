using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeskWall.Core.Shortcuts;

/// <summary>Measured arrow-overlay rects per (icon size, display scale), in runtime/calibration.json.
/// Seeded with the POC's measured value for 48 px icons at 100 percent.</summary>
public sealed class Calibration
{
    private readonly Dictionary<string, int[]> _map;

    private Calibration(Dictionary<string, int[]> map) { _map = map; }

    private static string FilePath => Paths.InRuntime("calibration.json");

    public static string Key(int iconSize, int scalePercent) => $"{iconSize}@{scalePercent}";

    public static Calibration Seed() => new(new Dictionary<string, int[]> { [Key(48, 100)] = [0, 40, 13] });

    public static Calibration Load()
    {
        var seed = Seed();
        if (!File.Exists(FilePath)) return seed;
        try
        {
            var stored = JsonSerializer.Deserialize(File.ReadAllText(FilePath), CalibrationJsonContext.Default.DictionaryStringInt32Array) ?? new();
            foreach (var (k, v) in stored) if (v.Length == 3) seed._map[k] = v;
            return seed;
        }
        catch (JsonException) { return seed; }
    }

    public ArrowRect? Get(int iconSize, int scalePercent)
        => _map.TryGetValue(Key(iconSize, scalePercent), out var v) ? new ArrowRect(v[0], v[1], v[2]) : null;

    public void Set(int iconSize, int scalePercent, ArrowRect arrow) => _map[Key(iconSize, scalePercent)] = [arrow.Dx, arrow.Dy, arrow.Size];

    public void Save()
    {
        var tmp = FilePath + ".tmp";
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(_map, CalibrationJsonContext.Default.DictionaryStringInt32Array));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch { if (File.Exists(tmp)) File.Delete(tmp); throw; }
    }
}

[JsonSerializable(typeof(Dictionary<string, int[]>))]
internal partial class CalibrationJsonContext : JsonSerializerContext;
