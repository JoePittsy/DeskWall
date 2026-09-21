using DeskWall.Core.Layout;
using DeskWall.Core.Sources;
using DeskWall.Designer.Model.Widgets;
using Xunit;

namespace DeskWall.Designer.Tests.Widgets;

/// <summary>The "+ Source" form: one table of fields per type rather than three hand-built
/// panels, and what those fields turn into.</summary>
public class SourceFormsTests
{
    [Fact]
    public void The_Built_Ins_Need_No_Form()
    {
        foreach (var type in SourceForms.BuiltIn) Assert.Empty(SourceForms.For(type));
        Assert.Equal(["time", "disks", "system", "hardware"], SourceForms.BuiltIn);
    }

    [Theory]
    [InlineData("http", "name", "url", "every", "parse", "timeout")]
    [InlineData("file", "name", "path", "every", "parse")]
    public void Each_Type_Has_Its_Own_Fields(string type, params string[] keys)
        => Assert.Equal(keys, SourceForms.For(type).Select(f => f.Key));

    [Fact]
    public void Command_Has_Its_Seven_Fields()
        => Assert.Equal(["name", "command", "args", "workingDir", "every", "timeout", "parse"],
            SourceForms.For("command").Select(f => f.Key));

    [Fact]
    public void Parse_Offers_Auto_And_File_Also_Offers_Rss()
    {
        Assert.Equal(["auto", "json", "text"], SourceForms.For("http").Single(f => f.Key == "parse").Choices);
        Assert.Equal(["auto", "json", "text", "rss"], SourceForms.For("file").Single(f => f.Key == "parse").Choices);
    }

    [Fact]
    public void The_Name_Defaults_To_The_Type()
    {
        foreach (var type in SourceForms.Configurable)
            Assert.Equal(type, SourceForms.For(type).Single(f => f.Key == SourceForms.NameKey).Default);
    }

    [Fact]
    public void Auto_Means_The_Key_Is_Not_Written_At_All()
    {
        var values = SourceForms.Defaults("http");
        Assert.Equal(SourceForms.Auto, values["parse"]);
        Assert.DoesNotContain("parse", SourceForms.ToSourceDef("http", values).Settings.Keys);

        values["parse"] = "json";
        Assert.Equal("json", SourceForms.ToSourceDef("http", values).Settings["parse"]);
    }

    [Fact]
    public void Every_Is_The_Sources_Own_Field_Not_A_Setting()
    {
        var def = SourceForms.ToSourceDef("file", SourceForms.Defaults("file"));
        Assert.Equal(30, def.EverySeconds);
        Assert.DoesNotContain("every", def.Settings.Keys);
    }

    [Fact]
    public void A_Blank_Optional_Field_Is_Left_Out()
    {
        var values = SourceForms.Defaults("command");
        values["workingDir"] = "";
        Assert.DoesNotContain("workingDir", SourceForms.ToSourceDef("command", values).Settings.Keys);
    }

    /// <summary>The whole point of the defaults: a source added from the palette must be one the
    /// Core factory will actually construct, so the live tree shows a result (or its own error)
    /// rather than the designer throwing on the way in.</summary>
    [Theory]
    [InlineData("time")]
    [InlineData("disks")]
    [InlineData("system")]
    [InlineData("http")]
    [InlineData("command")]
    [InlineData("file")]
    public void Defaults_Make_A_Source_The_Core_Factory_Accepts(string type)
    {
        var def = SourceForms.ToSourceDef(type, SourceForms.Defaults(type));
        var source = SourceFactory.Create(def, SystemClock.Instance, Secrets.Default());
        Assert.Equal(def.Name, source.Name);
        SourceFactory.DisposeAll([source]);
    }

    [Fact]
    public void A_Source_Reads_Back_Into_Its_Own_Form()
    {
        var def = new SourceDef
        {
            Name = "feed",
            Type = "http",
            EverySeconds = 900,
            Settings = { ["url"] = "https://example.com/x", ["parse"] = "json", ["timeout"] = "20" },
        };
        var values = SourceForms.FromSourceDef(def);
        Assert.Equal("feed", values[SourceForms.NameKey]);
        Assert.Equal("900", values["every"]);
        Assert.Equal("https://example.com/x", values["url"]);
        Assert.Equal("json", values["parse"]);
        Assert.Equal("20", values["timeout"]);

        var again = SourceForms.ToSourceDef("http", values);
        Assert.Equal(def.Name, again.Name);
        Assert.Equal(def.EverySeconds, again.EverySeconds);
        Assert.Equal(def.Settings, again.Settings);
    }

    [Fact]
    public void A_Missing_Parse_Reads_Back_As_Auto()
    {
        var def = new SourceDef { Name = "feed", Type = "file", Settings = { ["path"] = "C:\\x.json" } };
        Assert.Equal(SourceForms.Auto, SourceForms.FromSourceDef(def)["parse"]);
    }

    /// <summary>Header lines and unixTimeFields are not on the form (spec 3.3), but a duplicated
    /// shipped widget may have them and must not lose them.</summary>
    [Fact]
    public void Settings_The_Form_Does_Not_Show_Survive_An_Edit()
    {
        var def = new SourceDef
        {
            Name = "feed",
            Type = "http",
            Settings = { ["url"] = "https://example.com/x", ["header.Accept"] = "application/json", ["unixTimeFields"] = "ts" },
        };
        var values = SourceForms.FromSourceDef(def);
        values["url"] = "https://example.com/y";
        var again = SourceForms.ToSourceDef("http", values, def);

        Assert.Equal("https://example.com/y", again.Settings["url"]);
        Assert.Equal("application/json", again.Settings["header.Accept"]);
        Assert.Equal("ts", again.Settings["unixTimeFields"]);
    }

    // ---- validation -------------------------------------------------------------------------

    [Theory]
    [InlineData("feed", null)]
    [InlineData("", "A source needs a name.")]
    [InlineData("Feed", "A source name is lower-case letters and digits, starting with a letter.")]
    [InlineData("2feed", "A source name is lower-case letters and digits, starting with a letter.")]
    [InlineData("time", "There is already a source called 'time'.")]
    public void The_Name_Is_Checked(string name, string? expected)
    {
        var values = SourceForms.Defaults("http");
        values[SourceForms.NameKey] = name;
        Assert.Equal(expected, SourceForms.Validate("http", values, ["time"]));
    }

    [Fact]
    public void A_Required_Field_Cannot_Be_Left_Blank()
    {
        var values = SourceForms.Defaults("http");
        values["url"] = "  ";
        Assert.Equal("Url is needed.", SourceForms.Validate("http", values, []));
    }
}
