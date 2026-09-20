using DeskWall.Core.Display;
using DeskWall.Core.Wallpaper;
using Xunit;

public class WallpaperSetterTests
{
    [Fact]
    public void Get_ReturnsCurrentPath_ForPrimary()
    {
        var p = Monitors.Enumerate().First(m => m.IsPrimary);
        var current = WallpaperSetter.Get(p.WallpaperMonitorId);
        Assert.False(string.IsNullOrEmpty(current));
    }

    [Fact]
    public void RecordRestorePoint_WritesOnce()
    {
        var path = DeskWall.Core.Paths.InRuntime("restore.json");
        var existed = File.Exists(path);
        WallpaperSetter.RecordRestorePoint();
        Assert.True(File.Exists(path));
        if (!existed) Assert.Contains("\"", File.ReadAllText(path));
    }
}
