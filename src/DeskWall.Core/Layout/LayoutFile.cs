using System.Collections;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace DeskWall.Core.Layout;

public sealed class LayoutFile
{
    public int Version { get; set; } = 1;
    /// <summary>A literal path or a binding (e.g. <c>time.phase</c> through a map), resolved each
    /// tick with %ENV% and <c>runtime:</c> expanded: <see cref="Resolve.LayoutResolver.BaseImagePath"/>.</summary>
    public required PropertyValue BaseImage { get; set; }
    public Fit BaseFit { get; set; } = Fit.Cover;
    /// <summary>"jpeg" or "png".</summary>
    public string Encode { get; set; } = "jpeg";
    public int JpegQuality { get; set; } = 92;
    public List<SourceDef> Sources { get; set; } = new();
    public List<ComponentDef> Components { get; set; } = new();
    /// <summary>Widget instance id to the record the designer's widget picker needs to re-edit or
    /// re-create it. Null (not an empty dictionary) when the layout was never touched by the
    /// widget picker; the daemon ignores this field entirely (spec 3, `docs/layout-format.md`
    /// "Widgets"). Component-level ownership is <see cref="ComponentDef.Widget"/>.</summary>
    /// <remarks>v1 only: read for the migrator, never written once null.</remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, WidgetRecord>? Widgets { get; set; }
    /// <summary>v2: linked widget copies, expanded into components before resolve
    /// (<c>Widgets.WidgetExpander</c>, <c>docs/layout-format.md</c> "Copies"). Null in a v1 file.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<WidgetCopy>? Copies { get; set; }

    public static LayoutFile Parse(string json)
        => JsonSerializer.Deserialize(json, LayoutJsonContext.Default.LayoutFile) ?? throw new JsonException("empty layout");

    public static LayoutFile Load(string path) => Parse(File.ReadAllText(path));

    /// <summary>The file as the owner would write it: indented, no <c>"</c>/<c>°</c>
    /// escapes, enums in lowercase, and no property that still holds its default
    /// (<see cref="LayoutJsonWrite"/>). Parsing it back gives an identical layout.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, LayoutJsonWrite.Layout);

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

/// <summary>What <see cref="LayoutFile.ToJson"/> writes with. Reading stays on
/// <see cref="LayoutJsonContext"/>, which also still writes every property for anything that
/// compares whole components. Everything here sits on the source-generated metadata (a type-info
/// modifier and the generic enum converter), so it is as native-AOT safe as the context itself.</summary>
internal static class LayoutJsonWrite
{
    public static readonly JsonTypeInfo<LayoutFile> Layout = (JsonTypeInfo<LayoutFile>)Options().GetTypeInfo(typeof(LayoutFile));

    private static JsonSerializerOptions Options()
    {
        var o = new JsonSerializerOptions(LayoutJsonContext.Default.Options)
        {
            // Still escapes what JSON requires (a quote becomes \"), but leaves °, &, + and the
            // like as written. The file is read by DeskWall and by people, never embedded in HTML.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            TypeInfoResolver = LayoutJsonContext.Default.WithAddedModifier(SkipDefaults),
        };
        // Every hand-written layout spells these in lowercase; reading is case-insensitive either way.
        o.Converters.Add(new JsonStringEnumConverter<Fit>(JsonNamingPolicy.CamelCase));
        o.Converters.Add(new JsonStringEnumConverter<Axis>(JsonNamingPolicy.CamelCase));
        return o;
    }

    /// <summary>Leaves out a property whose value equals the one a fresh instance starts with, so
    /// reading the file back assigns exactly that value again. Required properties are always
    /// written (the reader demands them), and so is <c>version</c>: a file must not change
    /// meaning if the default format ever moves on.</summary>
    private static void SkipDefaults(JsonTypeInfo info)
    {
        if (info.Kind != JsonTypeInfoKind.Object || Prototype(info.Type) is not { } prototype) return;
        foreach (var p in info.Properties)
        {
            if (p.IsRequired || p.Get is null || (info.Type == typeof(LayoutFile) && p.Name == "version")) continue;
            var initial = p.Get(prototype);
            var inner = p.ShouldSerialize;
            p.ShouldSerialize = (owner, value) => !IsInitial(value, initial) && (inner is null || inner(owner, value));
        }
    }

    private static bool IsInitial(object? value, object? initial) => (value, initial) switch
    {
        (PropertyValue v, PropertyValue i) => !v.IsBound && !i.IsBound && v.LiteralText == i.LiteralText,
        (ICollection { Count: 0 }, ICollection { Count: 0 }) => true,
        _ => Equals(value, initial),
    };

    /// <summary>A fresh instance of each object type the layout format writes, required members
    /// filled with placeholders (they are always written, so their values never matter). A type
    /// missing here is written in full, never wrongly; <c>LayoutFileTests</c> checks the list is
    /// complete.</summary>
    internal static object? Prototype(Type t)
    {
        const string s = "";
        var r = default(Rect);
        if (t == typeof(LayoutFile)) return new LayoutFile { BaseImage = s };
        if (t == typeof(SourceDef)) return new SourceDef { Name = s, Type = s };
        if (t == typeof(WidgetCopy)) return new WidgetCopy { Id = s, Widget = s };
        if (t == typeof(WidgetRecord)) return new WidgetRecord { Template = s };
        if (t == typeof(TextDef)) return new TextDef { Id = s, Rect = r, Text = s };
        if (t == typeof(ImageDef)) return new ImageDef { Id = s, Rect = r, Source = s };
        if (t == typeof(BarDef)) return new BarDef { Id = s, Rect = r, Fraction = s };
        if (t == typeof(LineDef)) return new LineDef { Id = s, Rect = r, Values = s };
        if (t == typeof(DialDef)) return new DialDef { Id = s, Rect = r, Fraction = s };
        if (t == typeof(ShortcutDef)) return new ShortcutDef { Id = s, Rect = r, Target = s };
        if (t == typeof(RepeaterDef)) return new RepeaterDef { Id = s, Rect = r, Items = s, Template = [] };
        return null;
    }
}
