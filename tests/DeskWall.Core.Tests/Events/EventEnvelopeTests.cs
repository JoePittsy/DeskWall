using DeskWall.Core.Events;
using DeskWall.Core.Values;
using Xunit;

namespace DeskWall.Core.Tests.Events;

public class EventEnvelopeTests
{
    private static (EventEnvelope? e, string? why) P(string s) => EventEnvelopeParser.Parse(s);

    [Fact]
    public void A_Minimal_Event_Parses()
    {
        var (e, why) = P("""{"source":"build","data":{"status":"green","failures":0}}""");
        Assert.Null(why);
        Assert.Equal("build", e!.Source);
        Assert.Equal("green", ((TextValue)e.Data.Get("status")!).Text);
        Assert.Equal(0, ((NumberValue)e.Data.Get("failures")!).Number);
        Assert.True(e.Wake);            // waking is the default
        Assert.False(e.Replace);        // merging is the default
    }

    [Fact]
    public void Unknown_Attributes_Are_Ignored_Not_Rejected()
    {
        var (e, why) = P("""{"specversion":"1.0","datacontenttype":"application/json","source":"a","data":{}}""");
        Assert.Null(why);
        Assert.NotNull(e);
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("not json", "json")]
    [InlineData("""{"data":{}}""", "source")]
    [InlineData("""{"source":"a"}""", "data")]
    [InlineData("""{"source":"a","data":5}""", "data")]
    [InlineData("""{"source":"","data":{}}""", "source")]
    public void A_Bad_Envelope_Is_Rejected_With_A_Reason_Naming_The_Problem(string line, string word)
    {
        var (e, why) = P(line);
        Assert.Null(e);
        Assert.Contains(word, why!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Optional_Attributes_Are_Carried()
    {
        var (e, _) = P("""
            {"source":"a","data":{},"type":"build.done","subject":"main","id":"7",
             "time":"2026-09-21T20:00:00+01:00","replace":true,"wake":false}
            """);
        Assert.Equal("build.done", e!.Type);
        Assert.Equal("main", e.Subject);
        Assert.Equal("7", e.Id);
        Assert.Equal(new DateTimeOffset(2026, 9, 21, 20, 0, 0, TimeSpan.FromHours(1)), e.SentAt);
        Assert.True(e.Replace);
        Assert.False(e.Wake);
    }

    /// <summary>A source name reaches the value tree as a binding path segment, so it has to be
    /// one: BindingParser's names are a letter or underscore then letters, digits, _ or -.</summary>
    [Theory]
    [InlineData("my build")]
    [InlineData("9build")]
    [InlineData("build.sub")]
    public void A_Source_Name_That_Is_Not_A_Binding_Name_Is_Rejected(string name)
    {
        // Three '$': the payload's own "{}" is two literal braces, so an interpolation hole needs
        // more than two to be told apart from them.
        var (e, why) = P($$$"""{"source":"{{{name}}}","data":{}}""");
        Assert.Null(e);
        Assert.Contains("name", why!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_Unparseable_Time_Is_Dropped_Rather_Than_Failing_The_Event()
    {
        var (e, why) = P("""{"source":"a","data":{},"time":"yesterday"}""");
        Assert.Null(why);
        Assert.Null(e!.SentAt);
    }

    /// <summary>The payload goes through JsonValues like any other source's, so a nested object is
    /// a RecordValue and a list is a ListValue. One mapping, not two.</summary>
    [Fact]
    public void A_Nested_Payload_Maps_Through_JsonValues()
    {
        var (e, why) = P("""{"source":"a","data":{"inner":{"n":2},"flag":true}}""");
        Assert.Null(why);
        Assert.Equal(2, ((NumberValue)((RecordValue)e!.Data.Get("inner")!).Get("n")!).Number);
        Assert.True(((BoolValue)e.Data.Get("flag")!).Flag);
    }

    /// <summary>A JSON array is a valid JSON document but not a patch to a record.</summary>
    [Fact]
    public void A_Data_Array_Is_Rejected_Because_A_Patch_Is_A_Record()
    {
        var (e, why) = P("""{"source":"a","data":[1,2]}""");
        Assert.Null(e);
        Assert.Contains("data", why!, StringComparison.OrdinalIgnoreCase);
    }
}
