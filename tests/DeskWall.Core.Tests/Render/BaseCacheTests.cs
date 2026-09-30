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

    /// <summary>A bound base swaps between one photo per phase: four of them must stay decoded so a
    /// swap is a raw read, the least recently used goes first, and another canvas size (the designer's
    /// preview shares the runtime dir) is not counted against this one.</summary>
    [Fact]
    public void Ensure_Keeps_The_Four_Most_Recently_Used_Bases_Per_Canvas_Size()
    {
        var dir = Path.Combine(TempDir(), "basecache-lru-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        string Photo(string name, byte shade)
        {
            var p = Path.Combine(dir, name + ".png");
            using var b = Surface.Create(13, 7);
            b.Clear(new Color(255, shade, shade, shade));
            b.SavePng(p);
            return p;
        }
        var photos = new[] { Photo("a", 10), Photo("b", 20), Photo("c", 30), Photo("d", 40), Photo("e", 50) };

        var other = BaseCache.Ensure(photos[0], 12, 7, Fit.Cover);   // another size: never evicted by this one's churn
        var raws = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            raws.Add(BaseCache.Ensure(photos[i], 13, 7, Fit.Cover));
            File.SetLastWriteTimeUtc(raws[i], DateTime.UtcNow.AddSeconds(-10 + i));   // a, b, c, d: oldest first
        }
        Assert.All(raws, r => Assert.True(File.Exists(r)));

        Assert.Equal(raws[0], BaseCache.Ensure(photos[0], 13, 7, Fit.Cover));   // a hit makes a the newest
        var e = BaseCache.Ensure(photos[4], 13, 7, Fit.Cover);                  // the fifth evicts b, now the oldest

        Assert.True(File.Exists(e));
        Assert.True(File.Exists(raws[0]));
        Assert.False(File.Exists(raws[1]));
        Assert.True(File.Exists(raws[2]));
        Assert.True(File.Exists(raws[3]));
        Assert.True(File.Exists(other));
        Assert.Equal(BaseCache.Capacity, Directory.EnumerateFiles(Path.GetDirectoryName(e)!, "13x7-*.raw").Count());
    }
}
