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

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(LayoutFile))]
public partial class LayoutJsonContext : JsonSerializerContext;
