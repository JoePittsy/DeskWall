using DeskWall.Core.Events;
using DeskWall.Core.Values;
using Xunit;

namespace DeskWall.Core.Tests.Events;

public class ProviderRecordTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 21, 20, 0, 0, TimeSpan.Zero);

    private static EventEnvelope E(string json)
    {
        var (e, why) = EventEnvelopeParser.Parse(json);
        Assert.Null(why);
        return e!;
    }

    private static double Num(RecordValue r, string k) => ((NumberValue)r.Get(k)!).Number;

    [Fact]
    public void Data_Merges_So_A_Partial_Event_Does_Not_Blank_A_Field()
    {
        var r = ProviderRecord.Empty("volume", T0)
            .Apply(E("""{"source":"volume","data":{"level":0.4,"device":"Speakers"}}"""), T0)
            .Apply(E("""{"source":"volume","data":{"level":0.2}}"""), T0.AddSeconds(1));

        Assert.Equal(0.2, Num(r.Data, "level"));
        Assert.Equal("Speakers", ((TextValue)r.Data.Get("device")!).Text);
    }

    [Fact]
    public void Replace_Drops_The_Fields_The_New_Event_Does_Not_Carry()
    {
        var r = ProviderRecord.Empty("volume", T0)
            .Apply(E("""{"source":"volume","data":{"level":0.4,"device":"Speakers"}}"""), T0)
            .Apply(E("""{"source":"volume","data":{"level":0.2},"replace":true}"""), T0.AddSeconds(1));

        Assert.Equal(0.2, Num(r.Data, "level"));
        Assert.Null(r.Data.Get("device"));
    }

    [Fact]
    public void ToValues_Publishes_Data_Metadata_And_An_Age()
    {
        var r = ProviderRecord.Empty("build", T0)
            .Apply(E("""{"source":"build","data":{"status":"green"},"type":"build.done","subject":"main","id":"7","time":"2026-09-21T19:59:00+00:00"}"""), T0);

        var v = r.ToValues(T0.AddSeconds(90));

        var data = Assert.IsType<RecordValue>(v.Get("data"));
        Assert.Equal("green", ((TextValue)data.Get("status")!).Text);
        Assert.Equal("build.done", ((TextValue)v.Get("type")!).Text);
        Assert.Equal("main", ((TextValue)v.Get("subject")!).Text);
        Assert.Equal("7", ((TextValue)v.Get("id")!).Text);
        Assert.Equal(new DateTimeOffset(2026, 9, 21, 19, 59, 0, TimeSpan.Zero), ((TimeValue)v.Get("sentAt")!).Time);
        Assert.Equal(T0, ((TimeValue)v.Get("receivedAt")!).Time);
        Assert.Equal(90, Num(v, "ageSeconds"));
    }

    [Fact]
    public void Absent_Metadata_Is_Absent_Not_Empty()
    {
        var r = ProviderRecord.Empty("a", T0).Apply(E("""{"source":"a","data":{"x":1}}"""), T0);
        var v = r.ToValues(T0);

        Assert.Null(v.Get("type"));
        Assert.Null(v.Get("subject"));
        Assert.Null(v.Get("id"));
        Assert.Null(v.Get("sentAt"));
        Assert.NotNull(v.Get("receivedAt"));
    }

    /// <summary>Spec section 5: type/subject/id come from the last event, so an event that does
    /// not carry one clears it rather than leaving the previous event's value to be read as this
    /// one's.</summary>
    [Fact]
    public void Metadata_Comes_From_The_Last_Event_Not_Accumulated()
    {
        var r = ProviderRecord.Empty("a", T0)
            .Apply(E("""{"source":"a","data":{},"type":"one","id":"1"}"""), T0)
            .Apply(E("""{"source":"a","data":{}}"""), T0.AddSeconds(1));

        Assert.Null(r.Type);
        Assert.Null(r.Id);
    }

    [Fact]
    public void Nested_Objects_In_The_Payload_Merge_Only_At_The_Top_Level()
    {
        var r = ProviderRecord.Empty("a", T0)
            .Apply(E("""{"source":"a","data":{"a":{"x":1}}}"""), T0)
            .Apply(E("""{"source":"a","data":{"a":{"y":2}}}"""), T0.AddSeconds(1));

        var inner = Assert.IsType<RecordValue>(r.Data.Get("a"));
        Assert.Null(inner.Get("x"));            // not a deep merge: no rule for lists, nobody asked
        Assert.Equal(2, ((NumberValue)inner.Get("y")!).Number);
    }

    [Fact]
    public void An_Empty_Record_Publishes_No_Data_Fields_And_A_Zero_Age()
    {
        var v = ProviderRecord.Empty("a", T0).ToValues(T0);
        Assert.Empty(Assert.IsType<RecordValue>(v.Get("data")).Fields);
        Assert.Equal(0, Num(v, "ageSeconds"));
    }

    /// <summary>A clock that has gone backwards (an NTP correction between receipt and refresh)
    /// must not publish a negative age, which would format as "-3 s ago".</summary>
    [Fact]
    public void An_Age_Is_Never_Negative()
    {
        var r = ProviderRecord.Empty("a", T0).Apply(E("""{"source":"a","data":{}}"""), T0);
        Assert.Equal(0, Num(r.ToValues(T0.AddSeconds(-30)), "ageSeconds"));
    }

    [Fact]
    public void Apply_Takes_The_Received_Time_Given_To_It()
    {
        var r = ProviderRecord.Empty("a", T0).Apply(E("""{"source":"a","data":{}}"""), T0.AddMinutes(5));
        Assert.Equal(T0.AddMinutes(5), r.ReceivedAt);
    }
}
