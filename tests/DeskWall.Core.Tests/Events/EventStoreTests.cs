using System.Text.Json;
using DeskWall.Core.Events;
using DeskWall.Core.Values;
using Xunit;

namespace DeskWall.Core.Tests.Events;

public class EventStoreTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 21, 20, 0, 0, TimeSpan.FromHours(1));

    public EventStoreTests() => Clear();

    public void Dispose()
    {
        Clear();
        GC.SuppressFinalize(this);
    }

    private static void Clear()
    {
        // DESKWALL_HOME points at a temp folder for the whole assembly, so this never touches the
        // owner's real runtime dir.
        if (File.Exists(EventStore.Path)) File.Delete(EventStore.Path);
        foreach (var tmp in Directory.EnumerateFiles(Path.GetDirectoryName(EventStore.Path)!, "events.json.*.tmp"))
            File.Delete(tmp);
    }

    private static ProviderRecord Record(string name, string eventJson, DateTimeOffset at)
    {
        var (e, why) = EventEnvelopeParser.Parse(eventJson);
        Assert.Null(why);
        return ProviderRecord.Empty(name, at).Apply(e!, at);
    }

    [Fact]
    public void A_Round_Trip_Keeps_Data_And_Metadata()
    {
        var r = Record("build", """
            {"source":"build","data":{"status":"green","failures":0,"ok":true,"inner":{"x":1},"list":[{"id":"a"},{"id":"b"}]},
             "type":"build.done","subject":"main","id":"7","time":"2026-09-21T19:59:00+01:00"}
            """, T0);

        EventStore.Save([r]);
        var back = Assert.Single(EventStore.Load());

        Assert.Equal("build", back.Name);
        Assert.Equal("build.done", back.Type);
        Assert.Equal("main", back.Subject);
        Assert.Equal("7", back.Id);
        Assert.Equal(r.SentAt, back.SentAt);
        Assert.Equal(T0, back.ReceivedAt);
        Assert.Equal("green", ((TextValue)back.Data.Get("status")!).Text);
        Assert.Equal(0, ((NumberValue)back.Data.Get("failures")!).Number);
        Assert.True(((BoolValue)back.Data.Get("ok")!).Flag);
        Assert.Equal(1, ((NumberValue)((RecordValue)back.Data.Get("inner")!).Get("x")!).Number);
        var list = Assert.IsType<ListValue>(back.Data.Get("list"));
        Assert.Equal(2, list.Items.Count);
        Assert.Equal("id", list.KeyField);
        Assert.NotNull(list.ByKey("b"));
    }

    [Fact]
    public void Absent_Metadata_Stays_Absent_Across_A_Round_Trip()
    {
        EventStore.Save([Record("a", """{"source":"a","data":{"n":1}}""", T0)]);
        var back = Assert.Single(EventStore.Load());

        Assert.Null(back.Type);
        Assert.Null(back.Subject);
        Assert.Null(back.Id);
        Assert.Null(back.SentAt);
    }

    [Fact]
    public void Every_Provider_Is_Remembered_Not_Just_The_Last()
    {
        EventStore.Save([
            Record("a", """{"source":"a","data":{"n":1}}""", T0),
            Record("b", """{"source":"b","data":{"n":2}}""", T0),
        ]);

        var back = EventStore.Load();
        Assert.Equal(2, back.Count);
        Assert.Contains(back, r => r.Name == "a");
        Assert.Contains(back, r => r.Name == "b");
    }

    [Fact]
    public void A_Missing_File_Loads_As_Empty()
    {
        Assert.False(File.Exists(EventStore.Path));
        Assert.Empty(EventStore.Load());
    }

    [Fact]
    public void A_Corrupt_File_Loads_As_Empty_Rather_Than_Throwing()
    {
        File.WriteAllText(EventStore.Path, "{ this is not json");
        Assert.Empty(EventStore.Load());
    }

    [Fact]
    public void A_Provider_Entry_That_Makes_No_Sense_Is_Skipped_And_The_Rest_Survive()
    {
        File.WriteAllText(EventStore.Path, """
            {"version":1,"providers":[
              {"data":{"n":1},"receivedAt":"2026-09-21T20:00:00.0000000+01:00"},
              {"name":"good","data":{"n":2},"receivedAt":"2026-09-21T20:00:00.0000000+01:00"}
            ]}
            """);

        var back = Assert.Single(EventStore.Load());
        Assert.Equal("good", back.Name);
    }

    [Fact]
    public void Save_Writes_Atomically_So_A_Crash_Cannot_Leave_A_Half_File()
    {
        EventStore.Save([Record("a", """{"source":"a","data":{"n":1}}""", T0)]);

        var dir = Path.GetDirectoryName(EventStore.Path)!;
        Assert.Empty(Directory.EnumerateFiles(dir, "events.json.*.tmp"));
        using var doc = JsonDocument.Parse(File.ReadAllText(EventStore.Path));
        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("providers").ValueKind);
    }

    [Fact]
    public void Saving_Nothing_Leaves_A_Readable_Empty_File()
    {
        EventStore.Save([]);
        Assert.Empty(EventStore.Load());
    }

    [Fact]
    public void Path_Is_In_The_Runtime_Dir()
        => Assert.Equal(DeskWall.Core.Paths.InRuntime("events.json"), EventStore.Path);
}
