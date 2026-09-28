using System.Collections.Generic;
using System.IO;
using System.Linq;
using DeskWall.Core.Events;
using DeskWall.Core.Values;
using DeskWall.Designer.Model;
using Xunit;

/// <summary>The providers panel's model: what the designer knows about pushed providers when the
/// producer is another process and the daemon owns the pipe. Model only, no UI automation, per the
/// designer's testing rule.</summary>
public class ProviderViewTests
{
    private static RecordValue Rec(params (string Key, Value Value)[] fields)
    {
        var d = new Dictionary<string, Value>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in fields) d[k] = v;
        return new RecordValue(d);
    }

    private static ProviderRecord Record(string name, params (string Key, Value Value)[] data)
        => new(name, Rec(data), null, null, null, null, new System.DateTimeOffset(2026, 9, 21, 20, 0, 0, System.TimeSpan.Zero));

    private static ProviderManifest Manifest(string name, params ProviderField[] fields)
        => new() { Name = name, Description = "described", Fields = fields };

    private sealed class Harness : System.IDisposable
    {
        public readonly string Dir = Path.Combine(Path.GetTempPath(), "deskwall-tests", "providers-" + System.Guid.NewGuid().ToString("N"));
        public List<ProviderRecord> Records = [];
        public List<ProviderManifest> Manifests = [];
        public List<ProviderRecord>? Saved;

        public Harness() => Directory.CreateDirectory(Dir);

        public ProvidersModel Model() => new(
            () => Records,
            records => { Saved = records.ToList(); Records = Saved; },
            () => Manifests,
            Dir);

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Providers_Come_From_Manifests_And_Remembered_Records_Together()
    {
        using var h = new Harness();
        h.Records.Add(Record("build", ("status", new TextValue("green"))));
        h.Manifests.Add(Manifest("hearth", new ProviderField("data.game", "text", "Hollow Knight", "what is running")));

        var views = h.Model().Load();

        Assert.Equal(["build", "hearth"], views.Select(v => v.Name).OrderBy(n => n, System.StringComparer.Ordinal));
        var build = views.Single(v => v.Name == "build");
        var hearth = views.Single(v => v.Name == "hearth");
        // A provider that has run is bindable with no manifest; one with a manifest is bindable
        // before it has ever run here. That is the whole point of having both (spec 5).
        Assert.True(build.Remembered);
        Assert.False(build.Described);
        Assert.False(hearth.Remembered);
        Assert.True(hearth.Described);
        Assert.Contains(hearth.Fields, f => f.Path == "data.game");
    }

    [Fact]
    public void A_Remembered_Field_Wins_For_Value_And_The_Manifest_Wins_For_Description()
    {
        using var h = new Harness();
        h.Records.Add(Record("build", ("status", new TextValue("green"))));
        h.Manifests.Add(Manifest("build",
            new ProviderField("data.status", "text", "red", "green, amber or red")));

        var field = h.Model().Load().Single().Fields.Single(f => f.Path == "data.status");

        Assert.Equal("green", field.Value);                      // observed
        Assert.Equal("green, amber or red", field.Description);  // described
        Assert.Equal("red", field.Example);
    }

    [Fact]
    public void Describe_Writes_A_Manifest_Seeded_From_The_Observed_Fields()
    {
        using var h = new Harness();
        h.Records.Add(Record("build", ("status", new TextValue("green")), ("failures", new NumberValue(0))));

        var path = h.Model().Describe("build");

        Assert.Equal(Path.Combine(h.Dir, "build.json"), path);
        var written = ProviderManifest.Load(path);
        Assert.Equal("build", written.Name);
        Assert.Contains(written.Fields, f => f.Path == "data.status" && f.Type == "text" && f.Example == "green");
        Assert.Contains(written.Fields, f => f.Path == "data.failures" && f.Type == "number");
        // The author is meant to open the file and fill these in, so they must be there to fill.
        Assert.All(written.Fields, f => Assert.NotNull(f.Description));
    }

    [Fact]
    public void Forget_Removes_The_Remembered_Record_And_Leaves_A_Manifest_Alone()
    {
        using var h = new Harness();
        h.Records.Add(Record("build", ("status", new TextValue("green"))));
        h.Records.Add(Record("hearth", ("game", new TextValue("Hollow Knight"))));
        h.Manifests.Add(Manifest("build", new ProviderField("data.status", "text", null, "the build")));
        var model = h.Model();
        model.Load();

        model.Forget("build");

        Assert.NotNull(h.Saved);
        Assert.Equal(["hearth"], h.Saved!.Select(r => r.Name));
        var views = model.Load();
        // Still listed, because the manifest still describes it; just no longer remembered.
        var build = views.Single(v => v.Name == "build");
        Assert.False(build.Remembered);
        Assert.True(build.Described);
        Assert.Null(build.Fields.Single(f => f.Path == "data.status").Value);
    }

    [Fact]
    public void Forget_Is_Silent_About_A_Provider_It_Has_Never_Heard_Of()
    {
        using var h = new Harness();
        var model = h.Model();
        model.Load();
        model.Forget("nobody");     // a Forget racing the daemon's next save must not throw
        Assert.Empty(model.Load());
    }

    [Fact]
    public void A_Records_Metadata_Is_Offered_As_Bindable_Fields()
    {
        using var h = new Harness();
        h.Records.Add(new ProviderRecord("build", Rec(("status", new TextValue("green"))),
            "build.done", "main", "7", null, new System.DateTimeOffset(2026, 9, 21, 20, 0, 0, System.TimeSpan.Zero)));

        var fields = h.Model().Load().Single().Fields.Select(f => f.Path).ToList();

        // Spec 5: these are published beside the payload and a layout may bind any of them.
        Assert.Contains("data.status", fields);
        Assert.Contains("receivedAt", fields);
        Assert.Contains("ageSeconds", fields);
        Assert.Contains("type", fields);
        Assert.Contains("subject", fields);
        Assert.Contains("id", fields);
        // Absent metadata is absent, not an empty row.
        Assert.DoesNotContain("sentAt", fields);
    }
}
