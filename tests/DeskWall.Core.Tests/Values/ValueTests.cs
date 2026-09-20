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
}
