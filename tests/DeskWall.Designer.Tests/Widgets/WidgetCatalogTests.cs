using System.IO;
using DeskWall.Core;
using DeskWall.Designer.Model.Widgets;
using Xunit;

namespace DeskWall.Designer.Tests.Widgets;

public class WidgetCatalogTests
{
    private static string NewDir(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "deskwall-tests", "widget-catalog", name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Write(string dir, string fileName, string name)
        => File.WriteAllText(Path.Combine(dir, fileName), $$"""
        { "version": 1, "name": "{{name}}", "description": "d", "size": [10, 10], "sources": [], "components": [], "knobs": [] }
        """);

    [Fact]
    public void Loads_Every_Json_File_In_A_Directory()
    {
        var dir = NewDir("shipped");
        Write(dir, "clock.json", "Clock");
        Write(dir, "weather.json", "Weather");

        var catalog = WidgetCatalog.Load(dir);

        Assert.Equal(2, catalog.Count);
        Assert.Contains(catalog, t => t.Key == "clock" && t.Name == "Clock");
        Assert.Contains(catalog, t => t.Key == "weather" && t.Name == "Weather");
    }

    [Fact]
    public void A_Later_Directory_Overrides_A_Key_Loaded_From_An_Earlier_One()
    {
        var shipped = NewDir("shipped");
        var user = NewDir("user");
        Write(shipped, "clock.json", "Clock");
        Write(user, "clock.json", "My Clock");

        var catalog = WidgetCatalog.Load(shipped, user);

        Assert.Single(catalog);
        Assert.Equal("My Clock", catalog[0].Name);
    }

    [Fact]
    public void A_Missing_Directory_Is_Skipped_Not_An_Error()
    {
        var missing = Path.Combine(Path.GetTempPath(), "deskwall-tests", "widget-catalog", "does-not-exist-" + Guid.NewGuid().ToString("N")[..8]);
        var catalog = WidgetCatalog.Load(missing);
        Assert.Empty(catalog);
    }

    [Fact]
    public void Shipped_And_User_Dirs_Are_Where_The_Spec_Says()
    {
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "widgets"), WidgetCatalog.ShippedDir);
        Assert.Equal(Paths.InRuntime("widgets"), WidgetCatalog.UserDir);   // runtime dir is DESKWALL_HOME under test
    }
}
