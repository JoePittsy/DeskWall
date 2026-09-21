using DeskWall.Core.Values;
using Xunit;

public class ValueTests
{
    [Fact]
    public void ListValue_LookupByKey_FindsRecord()
    {
        var c = new RecordValue(new Dictionary<string, Value> { ["letter"] = new TextValue("C"), ["free"] = new NumberValue(120e9) });
        var d = new RecordValue(new Dictionary<string, Value> { ["letter"] = new TextValue("D"), ["free"] = new NumberValue(9e9) });
        var list = new ListValue([c, d], KeyField: "letter");
        Assert.Same(d, list.ByKey("D"));
        Assert.Null(list.ByKey("E"));
    }

    [Fact]
    public void Value_ToText_UsesInvariantCulture()
    {
        Assert.Equal("1234.5", new NumberValue(1234.5).ToText(null));
        Assert.Equal("1,235", new NumberValue(1234.6).ToText("N0"));   // .NET rounds 1234.5 half-to-even, so use an unambiguous value
        Assert.Equal("09:05", new TimeValue(new DateTimeOffset(2026, 9, 20, 9, 5, 0, TimeSpan.Zero)).ToText("HH:mm"));
        Assert.Equal("True", new BoolValue(true).ToText(null));
        Assert.Equal("https://x/10/cover.jpg", new NumberValue(10).ToText("https://x/{0}/cover.jpg"));
    }

    /// <summary>Finding 5: a malformed format string must fall back to the unformatted text, not
    /// throw a FormatException out of resolve and kill the tick.</summary>
    [Fact]
    public void Value_ToText_Falls_Back_When_The_Format_Is_Malformed()
    {
        // {1} has no argument: the composite format the review report used
        Assert.Equal("120000000000", new NumberValue(120e9).ToText("{0:N0} GB, {1} total"));
        // unbalanced brace
        Assert.Equal("120000000000", new NumberValue(120e9).ToText("{0:N0} GB {free"));
        // invalid type-specific format, which goes down the ToString(format) path instead
        Assert.Equal("1", new NumberValue(1).ToText("Q9"));
        Assert.Equal("2026-09-20T09:05:00.0000000+00:00",
            new TimeValue(new DateTimeOffset(2026, 9, 20, 9, 5, 0, TimeSpan.Zero)).ToText("{0} of {1}"));
    }

    /// <summary>A volume script published `muted` and nothing on screen could react to it. A format
    /// beginning with '?' maps the value's own text onto a string, so one feature makes text, colour
    /// and image path all react to a bool.</summary>
    [Fact]
    public void Map_Format_Picks_A_String_By_The_Values_Own_Text()
    {
        Assert.Equal("#D13438", new BoolValue(true).ToText("?true=#D13438,false=#EBFFFFFF"));
        Assert.Equal("#EBFFFFFF", new BoolValue(false).ToText("?true=#D13438,false=#EBFFFFFF"));
        // Matching is case-insensitive, so `true` matches the BoolValue's "True".
        Assert.Equal("muted", new BoolValue(true).ToText("?TRUE=muted"));
    }

    /// <summary>Showing nothing is the point of the muted case: no match and no star entry is the
    /// empty string, not the unformatted text.</summary>
    [Fact]
    public void Map_Format_With_No_Match_And_No_Star_Is_Empty()
    {
        Assert.Equal("", new BoolValue(false).ToText("?true=muted"));
        Assert.Equal("", new NumberValue(7).ToText("?1=day,0=night"));
    }

    [Fact]
    public void Map_Format_Falls_Back_To_The_Star_Entry()
    {
        Assert.Equal("day", new NumberValue(1).ToText("?1=day,0=night,*=?"));
        Assert.Equal("night", new NumberValue(0).ToText("?1=day,0=night,*=?"));
        Assert.Equal("?", new NumberValue(2).ToText("?1=day,0=night,*=?"));
        Assert.Equal("anything", new TextValue("zzz").ToText("?*=anything"));
    }

    [Fact]
    public void Map_Format_Works_On_Text_And_Time_Values()
    {
        Assert.Equal("up", new TextValue("Running").ToText("?running=up,stopped=down"));
        var t = new TimeValue(new DateTimeOffset(2026, 9, 20, 9, 5, 0, TimeSpan.Zero));
        Assert.Equal("matched", t.ToText("?2026-09-20T09:05:00.0000000+00:00=matched,*=no"));
    }

    /// <summary>A leading '?' with no '=' anywhere is not a map. It goes down the ordinary format
    /// path untouched, so a format that happened to start with '?' before maps existed still does
    /// exactly what it did: fall back for a value that cannot apply it, never throw.</summary>
    [Fact]
    public void Map_Format_That_Is_Malformed_Is_Not_Treated_As_A_Map()
    {
        Assert.Equal("True", new BoolValue(true).ToText("?"));
        Assert.Equal("True", new BoolValue(true).ToText("?true"));
        // A number applies it as a custom numeric format, junk in, junk out, as it always has.
        Assert.Equal("?nonsensemore nonsense", new NumberValue(42).ToText("?nonsense,more nonsense"));
        Assert.Equal("zzz", new TextValue("zzz").ToText("?no equals here"));
    }

    /// <summary>A key may be written with a space after the comma; the picked text is taken
    /// verbatim so a value can be blank or carry spaces of its own.</summary>
    [Fact]
    public void Map_Format_Trims_The_Key_But_Not_The_Picked_Text()
    {
        Assert.Equal(" down ", new TextValue("stopped").ToText("?running=up, stopped= down "));
        Assert.Equal("", new TextValue("stopped").ToText("?stopped=,*=x"));
    }
}
