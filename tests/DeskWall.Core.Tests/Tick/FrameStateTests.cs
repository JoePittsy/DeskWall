using DeskWall.Core.Tick;
using Xunit;

/// <summary>Finding 10: a failing Save used to leave &lt;path&gt;.tmp behind forever.</summary>
public class FrameStateTests
{
    [Fact]
    public void Save_Failure_Leaves_No_Tmp_File()
    {
        var dir = Path.Combine(Path.GetTempPath(), "deskwall-tests", "framestate-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "frame-state.json");
        Directory.CreateDirectory(path);   // destination is a directory: File.Move fails after the tmp write

        var state = new FrameState { SignatureKey = "x" };
        Assert.ThrowsAny<Exception>(() => state.Save(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Save_Then_Load_RoundTrips_BaseKey()
    {
        var dir = Path.Combine(Path.GetTempPath(), "deskwall-tests", "framestate-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "frame-state.json");
        var state = new FrameState { SignatureKey = "sig", BaseKey = "abc123" };
        state.Save(path);
        var loaded = FrameState.Load(path);
        Assert.Equal("abc123", loaded.BaseKey);
    }
}
