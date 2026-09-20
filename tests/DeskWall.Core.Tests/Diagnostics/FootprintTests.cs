using DeskWall.Core.Diagnostics;
using Xunit;

public class FootprintTests
{
    [Fact]
    public void Current_Reports_Plausible_Numbers()
    {
        var f = Footprint.Current();
        Assert.InRange(f.WorkingSetBytes, 1_000_000, 2_000_000_000);
        Assert.InRange(f.PrivateBytes, 1_000_000, 2_000_000_000);
        Assert.InRange(f.Handles, 10, 100_000);
        Assert.InRange(f.Threads, 1, 1000);
        Assert.Contains("MB", f.Short());
    }

    [Fact]
    public void Trim_Does_Not_Throw_And_Working_Set_Does_Not_Grow()
    {
        var before = Footprint.Current().WorkingSetBytes;
        Footprint.Trim();
        var after = Footprint.Current().WorkingSetBytes;
        Assert.True(after <= before + 512 * 1024, $"working set grew from {before} to {after}");
    }
}
