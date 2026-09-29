using System.IO;
using DeskWall.Core;
using DeskWall.Designer.Model.Widgets;
using Xunit;
using DeskWall.Core.Widgets;

namespace DeskWall.Designer.Tests.Widgets;

/// <summary>The catalog's side of editing a widget that ships beside the exe. The shipped folder is
/// replaced by every install, so an edit is copy-on-write: Apply saves a file of the same key in the
/// user's folder (<c>DesignerModelDepthTests</c>), which the catalog prefers, in the shipped one's
/// place in the Insert panel.
/// <para>These write part-less widgets (a "clock" among them) into the shared test home's real
/// <c>widgets\</c> folder, which every other test's <c>DesignerModel.Finder()</c> reads first.
/// xUnit disposes the class after each test, and that empties the folder again, so no later test
/// expands a copy of "clock" against a clock with no parts.</para></summary>
public sealed class ShippedEditingTests : IDisposable
{
    public void Dispose()
    {
        if (Directory.Exists(WidgetCatalog.UserDir)) Directory.Delete(WidgetCatalog.UserDir, recursive: true);
    }

    private static string NewDir(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "deskwall-tests", "shipped-editing", name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Write(string dir, string fileName, string name)
        => File.WriteAllText(Path.Combine(dir, fileName), $$"""
        { "version": 1, "name": "{{name}}", "description": "d", "size": [10, 10], "sources": [], "components": [], "knobs": [] }
        """);

    /// <summary>A template in the real user dir, so <see cref="WidgetCatalog.IsUserTemplate"/>
    /// (which compares against <see cref="WidgetCatalog.UserDir"/>, DESKWALL_HOME under test)
    /// agrees it is the owner's own.</summary>
    private static string UserDir()
    {
        var dir = WidgetCatalog.UserDir;
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ---- what the catalog records ------------------------------------------------------------

    [Fact]
    public void A_User_File_On_A_Shipped_Key_Is_Flagged_As_Both()
    {
        var shipped = NewDir("shipped");
        var user = UserDir();
        Write(shipped, "clock.json", "Clock");
        Write(shipped, "date.json", "Date");
        Write(user, "clock.json", "My Clock");
        Write(user, "mine.json", "Mine");

        var catalog = WidgetCatalog.Load(shipped, user);

        var clock = catalog.Single(t => t.Key == "clock");
        Assert.True(clock.OverridesShipped);
        Assert.True(WidgetCatalog.IsUserTemplate(clock));

        var date = catalog.Single(t => t.Key == "date");
        Assert.False(date.OverridesShipped);
        Assert.False(WidgetCatalog.IsUserTemplate(date));

        var mine = catalog.Single(t => t.Key == "mine");
        Assert.False(mine.OverridesShipped);
        Assert.True(WidgetCatalog.IsUserTemplate(mine));
    }

    [Fact]
    public void A_Shipped_Key_With_No_Override_Is_Not_Flagged()
    {
        var shipped = NewDir("shipped");
        Write(shipped, "clock.json", "Clock");
        Assert.False(WidgetCatalog.Load(shipped, NewDir("empty-user"))[0].OverridesShipped);
    }

    // ---- the way back -----------------------------------------------------------------------------

    [Fact]
    public void Resetting_Deletes_Only_The_Override_And_The_Shipped_One_Comes_Back()
    {
        var shipped = NewDir("shipped");
        var user = UserDir();
        Write(shipped, "clock.json", "Clock");
        Write(user, "clock.json", "My Clock");

        var before = WidgetCatalog.Load(shipped, user);
        Assert.Equal("My Clock", before[0].Name);

        File.Delete(before[0].Path!);

        var after = WidgetCatalog.Load(shipped, user);
        Assert.Equal("Clock", Assert.Single(after).Name);
        Assert.True(File.Exists(Path.Combine(shipped, "clock.json")));
    }

    /// <summary>The override keeps the shipped key's place in the gallery, so resetting one does
    /// not make the list jump about.</summary>
    [Fact]
    public void An_Override_Keeps_The_Shipped_Keys_Place_In_The_Gallery()
    {
        var shipped = NewDir("shipped");
        var user = UserDir();
        Write(shipped, "aaa.json", "A");
        Write(shipped, "bbb.json", "B");
        Write(shipped, "ccc.json", "C");
        Write(user, "aaa.json", "A edited");

        Assert.Equal(["A edited", "B", "C"], WidgetCatalog.Load(shipped, user).Select(t => t.Name));
    }
}
