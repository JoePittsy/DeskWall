using System.IO;
using System.Linq;
using DeskWall.Core.Layout;
using DeskWall.Core.Sources;
using DeskWall.Designer.Model;
using Xunit;

file sealed class FixedClock(DateTimeOffset now) : IClock { public DateTimeOffset Now => now; }

public class LiveSourcesTests
{
    private static Secrets NoSecrets() => new(Path.Combine(Path.GetTempPath(), "deskwall-tests", "home-designer", "no-such-secrets.json"));

    [Fact]
    public async Task Tree_Merges_Snapshots_From_Multiple_Sources()
    {
        var defs = new[]
        {
            new SourceDef { Name = "time", Type = "time" },
            new SourceDef { Name = "disks", Type = "disks" },
        };
        using var live = new LiveSources(defs, NoSecrets(), new FixedClock(DateTimeOffset.UtcNow));
        await live.RefreshNowAsync("time");
        await live.RefreshNowAsync("disks");

        var tree = live.Tree();
        Assert.NotNull(tree.Get("time"));
        Assert.NotNull(tree.Get("disks"));
    }

    [Fact]
    public async Task Updated_Fires_After_RefreshNowAsync()
    {
        var defs = new[] { new SourceDef { Name = "time", Type = "time" } };
        using var live = new LiveSources(defs, NoSecrets(), new FixedClock(DateTimeOffset.UtcNow));
        var n = 0;
        live.Updated += () => Interlocked.Increment(ref n);

        await live.RefreshNowAsync("time");

        Assert.True(n >= 1);
    }

    [Fact]
    public async Task Failing_Source_Keeps_Last_Values_And_Exposes_LastError()
    {
        var dir = Path.Combine(Path.GetTempPath(), "deskwall-tests", "livesources-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, "t.exe");
        File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), exe);

        var def = new SourceDef
        {
            Name = "cmd",
            Type = "command",
            Settings = new Dictionary<string, string> { ["command"] = exe, ["args"] = "/c echo hello" },
        };
        using var live = new LiveSources([def], NoSecrets(), new FixedClock(DateTimeOffset.UtcNow));

        await live.RefreshNowAsync("cmd");
        var first = live.Snapshots.Single(s => s.Name == "cmd");
        Assert.NotNull(first.Values);
        Assert.Null(first.LastError);

        File.Delete(exe);
        await live.RefreshNowAsync("cmd");
        var second = live.Snapshots.Single(s => s.Name == "cmd");
        Assert.NotNull(second.Values);   // last good values are kept
        Assert.NotNull(second.LastError);
        Assert.Equal(1, second.ConsecutiveFailures);
    }

    [Fact]
    public void Snapshots_Reflects_A_Source_That_Fails_To_Construct()
    {
        // http requires settings.url; omitting it throws inside SourceFactory.Create, which
        // LiveSources must record as a failed snapshot rather than let the exception escape.
        var def = new SourceDef { Name = "bad-http", Type = "http" };
        using var live = new LiveSources([def], NoSecrets(), new FixedClock(DateTimeOffset.UtcNow));

        var snap = live.Snapshots.Single();
        Assert.Equal("bad-http", snap.Name);
        Assert.NotNull(snap.LastError);
    }
}
