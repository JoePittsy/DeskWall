using DeskWall.Core.Display;
using DeskWall.Core.Wallpaper;
using Xunit;

public class WallpaperSetterTests
{
    [Fact]
    [Trait("Category", "Desktop")]
    public void Get_ReturnsCurrentPath_ForPrimary()
    {
        var mons = Monitors.Enumerate();
        if (mons.Count == 0 || !mons.Any(m => m.IsPrimary)) return;   // no desktop session (e.g. a CI box): skip, do not fail
        var p = mons.First(m => m.IsPrimary);
        var current = WallpaperSetter.Get(p.WallpaperMonitorId);
        // A machine with no wallpaper set answers "" - that is the COM call working, not failing, so
        // assert what is actually guaranteed rather than something a fresh profile or a CI agent fails.
        Assert.NotNull(current);
        if (current.Length > 0) Assert.True(Path.IsPathRooted(current), current);
    }

    /// <summary>
    /// Finding 17: the previous version of this test never actually exercised "once" - on every
    /// run after the first, restore.json already existed from a prior run (DESKWALL_HOME is a
    /// fixed temp folder that is never cleaned between runs), so the only content assertion was
    /// skipped and the test reduced to Assert.True(File.Exists(path)), which would pass even if
    /// RecordRestorePoint were an empty method. Delete the file first, call twice with the content
    /// modified in between, and assert the second call left the modification untouched.
    /// </summary>
    [Fact]
    public void RecordRestorePoint_WritesOnce()
    {
        var path = DeskWall.Core.Paths.InRuntime("restore.json");
        File.Delete(path);

        WallpaperSetter.RecordRestorePoint();
        Assert.True(File.Exists(path));

        // RecordRestorePoint's only precondition is File.Exists(RestoreFile); overwrite with an
        // arbitrary marker and prove the second call leaves it alone.
        var marker = "test-marker-" + Guid.NewGuid();
        File.WriteAllText(path, marker);

        WallpaperSetter.RecordRestorePoint();   // file already exists: must not overwrite

        Assert.Equal(marker, File.ReadAllText(path));
    }
}
