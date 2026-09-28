using System.IO;
using DeskWall.Core;
using DeskWall.Designer.Model.Widgets;
using Xunit;

namespace DeskWall.Designer.Tests.Widgets;

/// <summary>Editing a widget that ships beside the exe. The shipped folder is replaced by every
/// install, so an edit is copy-on-write: it saves a file of the same key in the user's folder,
/// which the catalog already prefers, in the shipped one's place in the gallery.</summary>
public class ShippedEditingTests
{
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

    // ---- opening one for editing ---------------------------------------------------------------

    [Fact]
    public void Editing_A_Shipped_Widget_Is_Copy_On_Write()
    {
        var shipped = NewDir("shipped");
        Write(shipped, "clock.json", "Clock");
        var template = WidgetCatalog.Load(shipped)[0];

        var doc = WidgetDocument.ForEditing(template);

        // Null path: nothing may ever be written back beside the exe, and a rename must not
        // delete the shipped file as "the previous one".
        Assert.Null(doc.Path);
        Assert.Equal("clock", doc.EditingKey);
        Assert.Equal("Clock", doc.Name);
    }

    [Fact]
    public void Editing_The_Owners_Own_Widget_Edits_The_File_In_Place()
    {
        var user = UserDir();
        Write(user, "mine.json", "Mine");
        var template = WidgetCatalog.Load(user)[0];

        var doc = WidgetDocument.ForEditing(template);

        Assert.Equal(template.Path, doc.Path);
        Assert.Equal("mine", doc.EditingKey);
    }

    // ---- saving ----------------------------------------------------------------------------------

    [Fact]
    public void Saving_A_Shipped_Widget_Being_Edited_Lands_In_The_User_Folder()
    {
        var shipped = NewDir("shipped");
        Write(shipped, "clock.json", "Clock");
        var template = WidgetCatalog.Load(shipped)[0];
        var doc = WidgetDocument.ForEditing(template);
        doc.Description = "Changed.";

        var userDir = UserDir();
        var path = WidgetTemplateWriter.Save(doc, ["clock"], userDir);

        Assert.Equal(Path.Combine(userDir, "clock.json"), path);
        Assert.True(File.Exists(Path.Combine(shipped, "clock.json")));   // untouched

        var reloaded = WidgetCatalog.Load(shipped, userDir);
        Assert.Single(reloaded);
        Assert.Equal("Changed.", reloaded[0].Description);
        Assert.True(reloaded[0].OverridesShipped);
    }

    /// <summary>The refusal is about a <em>new</em> widget silently shadowing a shipped one, not
    /// about the key itself, so it still fires for that and no longer fires for a deliberate edit.</summary>
    [Fact]
    public void A_New_Widget_Named_After_A_Shipped_One_Is_Still_Refused()
    {
        var doc = WidgetDocument.New();
        doc.Name = "Clock";
        doc.Description = "d";

        var ex = Assert.Throws<InvalidOperationException>(() => WidgetTemplateWriter.Save(doc, ["clock"], UserDir()));
        Assert.Contains("shipped widget", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Renaming_While_Editing_A_Shipped_Widget_Does_Not_Touch_The_Shipped_File()
    {
        var shipped = NewDir("shipped");
        Write(shipped, "clock.json", "Clock");
        var doc = WidgetDocument.ForEditing(WidgetCatalog.Load(shipped)[0]);
        doc.Description = "d";
        doc.Name = "My clock";

        var userDir = UserDir();
        var path = WidgetTemplateWriter.Save(doc, ["clock"], userDir);

        Assert.Equal(Path.Combine(userDir, "my-clock.json"), path);
        Assert.True(File.Exists(Path.Combine(shipped, "clock.json")));
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
