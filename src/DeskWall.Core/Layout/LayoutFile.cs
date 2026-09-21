using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeskWall.Core.Layout;

public sealed class LayoutFile
{
    public int Version { get; set; } = 1;
    public required string BaseImage { get; set; }
    public Fit BaseFit { get; set; } = Fit.Cover;
    /// <summary>"jpeg" or "png".</summary>
    public string Encode { get; set; } = "jpeg";
    public int JpegQuality { get; set; } = 92;
    public List<SourceDef> Sources { get; set; } = new();
    public List<ComponentDef> Components { get; set; } = new();

    /// <summary>Instance id -> what the designer stamped it from. The daemon never reads it; it is
    /// here so a widget's knobs can be shown back and re-edited, and so the instance can be built
    /// again after its template changes. Absent in a layout written by hand.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, WidgetRecord>? Widgets { get; set; }

    public static LayoutFile Parse(string json)
        => JsonSerializer.Deserialize(json, LayoutJsonContext.Default.LayoutFile) ?? throw new JsonException("empty layout");

    public static LayoutFile Load(string path) => Parse(File.ReadAllText(path));

    public string ToJson() => JsonSerializer.Serialize(this, LayoutJsonContext.Default.LayoutFile);

    public void Save(string path)
    {
        var tmp = path + ".tmp";
        try
        {
            File.WriteAllText(tmp, ToJson());
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

/// <summary>One widget instance in a layout: which template stamped it, the knob values the owner
/// chose, and whether the arranger is allowed to move it. Lives in Core only so the layout's
/// source-generated JSON context can carry it; nothing in Core reads it.</summary>
public sealed class WidgetRecord
{
    public required string Template { get; set; }
    public Dictionary<string, string> Knobs { get; set; } = new();
    public bool Unlocked { get; set; }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(LayoutFile))]
public partial class LayoutJsonContext : JsonSerializerContext;
