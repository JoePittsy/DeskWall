using DeskWall.Core.Sources;
using DeskWall.Core.Values;
using Xunit;

public class DisksSourceTests
{
    [Fact]
    [Trait("Category", "Desktop")]
    public async Task Publishes_Fixed_Ready_Drives_Keyed_By_Letter()
    {
        var real = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).ToList();
        if (real.Count == 0) return;   // no ready fixed drive (e.g. a CI box): skip, do not fail
        var src = new DisksSource("disks", TimeSpan.FromMinutes(5), () => real);
        var v = await src.RefreshAsync(default);
        var list = (ListValue)v.Get("drives")!;
        Assert.Equal("letter", list.KeyField);
        Assert.Equal(real.Count, list.Items.Count);
        var c = list.ByKey("C")!;
        var frac = ((NumberValue)c.Get("usedFraction")!).Number;
        Assert.InRange(frac, 0, 1);
        Assert.True(((NumberValue)c.Get("freeGB")!).Number >= 0);
    }
}
