using DeskWall.Core;
using DeskWall.Core.Events;
using Xunit;

namespace DeskWall.Core.Tests.Events;

/// <summary>Mirrors WidgetCatalogTests: the shipped/runtime two-directory load is the same shape,
/// and a provider manifest is loaded the same way a widget template is.</summary>
public class ProviderManifestTests
{
    private static string NewDir(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "deskwall-tests", "provider-catalog", name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Write(string dir, string fileName, string name, string description)
        => File.WriteAllText(Path.Combine(dir, fileName), $$"""
        { "version": 1, "name": "{{name}}", "description": "{{description}}", "fields": [] }
        """);

    [Fact]
    public void Loads_Every_Json_File_In_A_Directory()
    {
        var dir = NewDir("shipped");
        Write(dir, "build.json", "build", "CI");
        Write(dir, "volume.json", "volume", "Audio");

        var catalog = ProviderCatalog.Load(dir);

        Assert.Equal(2, catalog.Count);
        Assert.Contains(catalog, m => m.Name == "build" && m.Description == "CI");
        Assert.Contains(catalog, m => m.Name == "volume");
    }

    [Fact]
    public void A_Later_Directory_Overrides_A_Key_Loaded_From_An_Earlier_One_And_Keeps_Its_Position()
    {
        var shipped = NewDir("shipped");
        var user = NewDir("user");
        Write(shipped, "build.json", "build", "CI");
        Write(shipped, "volume.json", "volume", "Audio");
        Write(user, "build.json", "build", "My CI");

        var catalog = ProviderCatalog.Load(shipped, user);

        Assert.Equal(2, catalog.Count);
        Assert.Equal("build", catalog[0].Name);                     // position of first sighting
        Assert.Equal("My CI", catalog[0].Description);
    }

    [Fact]
    public void A_Missing_Directory_Is_Skipped_Not_An_Error()
    {
        var missing = Path.Combine(Path.GetTempPath(), "deskwall-tests", "provider-catalog", "gone-" + Guid.NewGuid().ToString("N")[..8]);
        Assert.Empty(ProviderCatalog.Load(missing));
    }

    [Fact]
    public void A_Malformed_File_Names_Itself_In_The_Exception()
    {
        var dir = NewDir("bad");
        var path = Path.Combine(dir, "broken.json");
        File.WriteAllText(path, "{ not json");

        var ex = Assert.Throws<FormatException>(() => ProviderCatalog.Load(dir));
        Assert.Contains("broken.json", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_File_Missing_Name_Or_Description_Names_The_Field()
    {
        var dir = NewDir("incomplete");
        File.WriteAllText(Path.Combine(dir, "a.json"), """{ "version": 1, "description": "d" }""");
        Assert.Contains("name", Assert.Throws<FormatException>(() => ProviderCatalog.Load(dir)).Message, StringComparison.OrdinalIgnoreCase);

        var dir2 = NewDir("incomplete2");
        File.WriteAllText(Path.Combine(dir2, "a.json"), """{ "version": 1, "name": "a" }""");
        Assert.Contains("description", Assert.Throws<FormatException>(() => ProviderCatalog.Load(dir2)).Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A manifest names a provider, and a provider name is the first segment of every
    /// binding into it, so a name no binding could spell describes nothing reachable.</summary>
    [Fact]
    public void A_Name_That_Is_Not_A_Binding_Name_Is_Rejected()
    {
        var dir = NewDir("badname");
        File.WriteAllText(Path.Combine(dir, "a.json"), """{ "version": 1, "name": "my build", "description": "d" }""");
        Assert.Contains("name", Assert.Throws<FormatException>(() => ProviderCatalog.Load(dir)).Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Fields_And_ExpectEvery_Are_Carried()
    {
        var dir = NewDir("full");
        File.WriteAllText(Path.Combine(dir, "build.json"), """
            {
              "version": 1,
              "name": "build",
              "description": "CI build status",
              "expectEverySeconds": 900,
              "fields": [
                { "path": "data.status", "type": "text", "example": "green", "description": "green, red or running" },
                { "path": "data.failures", "type": "number" }
              ]
            }
            """);

        var m = Assert.Single(ProviderCatalog.Load(dir));

        Assert.Equal(900, m.ExpectEverySeconds);
        Assert.Equal(2, m.Fields.Count);
        Assert.Equal("data.status", m.Fields[0].Path);
        Assert.Equal("text", m.Fields[0].Type);
        Assert.Equal("green", m.Fields[0].Example);
        Assert.Equal("green, red or running", m.Fields[0].Description);
        Assert.Null(m.Fields[1].Example);
        Assert.Null(m.Fields[1].Description);
        Assert.Equal(Path.Combine(dir, "build.json"), m.Path);
    }

    [Fact]
    public void A_Field_Without_A_Path_Names_The_File()
    {
        var dir = NewDir("badfield");
        File.WriteAllText(Path.Combine(dir, "build.json"), """
            { "version": 1, "name": "build", "description": "d", "fields": [ { "type": "text" } ] }
            """);
        var ex = Assert.Throws<FormatException>(() => ProviderCatalog.Load(dir));
        Assert.Contains("build.json", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>ToJson is how the designer's "Describe this provider" seeds a manifest from the
    /// fields it has observed, so what it writes has to load again.</summary>
    [Fact]
    public void ToJson_Round_Trips_Through_Load()
    {
        var dir = NewDir("roundtrip");
        var path = Path.Combine(dir, "build.json");
        var m = new ProviderManifest
        {
            Name = "build",
            Description = "CI build status",
            ExpectEverySeconds = 900,
            Fields = [new ProviderField("data.status", "text", "green", "what the last build did")],
        };

        File.WriteAllText(path, ProviderManifest.ToJson(m));
        var back = ProviderManifest.Load(path);

        Assert.Equal("build", back.Name);
        Assert.Equal("CI build status", back.Description);
        Assert.Equal(900, back.ExpectEverySeconds);
        var f = Assert.Single(back.Fields);
        Assert.Equal("data.status", f.Path);
        Assert.Equal("text", f.Type);
        Assert.Equal("green", f.Example);
        Assert.Equal("what the last build did", f.Description);
    }

    [Fact]
    public void Shipped_And_User_Dirs_Are_Where_The_Spec_Says()
    {
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "providers"), ProviderCatalog.ShippedDir);
        Assert.Equal(Paths.InRuntime("providers"), ProviderCatalog.UserDir);   // runtime dir is DESKWALL_HOME under test
    }
}
