using DeskWall.Core;
using DeskWall.Core.Render;
using Xunit;

/// <summary>Finding 20: the cache tidy-up loop used to let File.Delete's IOException escape,
/// failing a tick that had already produced the cache entry it needed.</summary>
public class BaseCacheTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "deskwall-tests");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Ensure_Ignores_A_Locked_Stale_Cache_File_During_Tidy_Up()
    {
        var dir = TempDir();
        var baseDir = Paths.InRuntime("base");
        Directory.CreateDirectory(baseDir);

        // A stale .raw file that looks old enough to be tidied, held open exclusively so
        // File.Delete throws IOException when the tidy-up loop reaches it.
        var stale = Path.Combine(baseDir, "stale-" + Guid.NewGuid().ToString("N") + ".raw");
        File.WriteAllBytes(stale, [0]);
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-2));
        using var lockHandle = new FileStream(stale, FileMode.Open, FileAccess.Read, FileShare.None);

        var png = Path.Combine(dir, "basecache-tidy-" + Guid.NewGuid().ToString("N") + ".png");
        using (var b = Surface.Create(10, 10)) { b.Clear(new Color(255, 1, 2, 3)); b.SavePng(png); }

        var path = BaseCache.Ensure(png, 10, 10, Fit.Cover);   // must not throw despite the locked stale file

        Assert.True(File.Exists(path));
        Assert.True(File.Exists(stale));   // still there: the delete failed and was swallowed
    }
}
