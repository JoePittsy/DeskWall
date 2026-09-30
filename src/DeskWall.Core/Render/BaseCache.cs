using System.Security.Cryptography;
using System.Text;

namespace DeskWall.Core.Render;

/// <summary>The base image scaled to the canvas, stored as a raw PBGRA dump so loading it is a
/// copy, not a decode. Keyed by the resolved path, mtime, size and fit.
/// <para>A bound <c>baseImage</c> (one photo per <c>time.phase</c>) swaps between a few files, so the
/// cache keeps the <see cref="Capacity"/> most recently used entries per canvas size rather than
/// just the current one: after each photo has been shown once, a swap is a raw read, never a
/// decode. Recency is the file's mtime, touched on every hit. Sizes are grouped separately
/// because the designer shares the runtime dir and renders its preview at its own size; its
/// entries must not evict the daemon's.</para></summary>
public static class BaseCache
{
    /// <summary>Entries kept per canvas size: one per phase of the day.</summary>
    public const int Capacity = 4;

    /// <summary>The key <see cref="Ensure"/> would use, without decoding or writing anything.
    /// Only stats the file, so the tick's skip gate can detect a replaced base image (finding 12)
    /// before paying for a full render - the spec requires the skip gate to run before any drawing.</summary>
    public static string KeyFor(string imagePath, int w, int h, Fit fit)
    {
        var mtime = File.GetLastWriteTimeUtc(imagePath).Ticks;
        var keySrc = $"{imagePath}|{mtime}|{w}x{h}|{fit}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(keySrc)))[..16];
    }

    /// <summary>Where the cached raw for this image lives; exists only once <see cref="Ensure"/> has run.</summary>
    public static string PathFor(string imagePath, int w, int h, Fit fit)
        => Path.Combine(Paths.InRuntime("base"), $"{w}x{h}-{KeyFor(imagePath, w, h, fit)}.raw");

    public static string Ensure(string imagePath, int w, int h, Fit fit)
    {
        var path = PathFor(imagePath, w, h, fit);
        var dir = Path.GetDirectoryName(path)!;
        if (File.Exists(path))
        {
            // Recency for the eviction below. Best-effort: a hit that cannot be touched is still a hit.
            try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return path;
        }
        Directory.CreateDirectory(dir);
        using (var src = Surface.Load(imagePath))
        using (var dst = Surface.Create(w, h))
        {
            dst.Clear(new Color(255, 0, 0, 0));
            // Resample.Fast: the base is a mild downscale of a very large image, and the
            // high-quality filter measured +163 ms here against a 500 ms cold-start budget for no
            // visible gain at that ratio. See Resample.
            dst.DrawSurface(src, new Rect(0, 0, w, h), fit, resample: Resample.Fast);
            dst.SaveRaw(path);
        }
        Evict(dir, path, $"{w}x{h}-");
        return path;
    }

    /// <summary>Keeps the <see cref="Capacity"/> newest entries of this size (including
    /// <paramref name="keep"/>) and drops the rest; drops any other file (another size, or the
    /// pre-LRU unprefixed names) once it is a day old. Best-effort - a file locked by a concurrent
    /// tick or the designer must not fail a tick that already has the entry it needed (finding 20).</summary>
    private static void Evict(string dir, string keep, string sizePrefix)
    {
        var mine = new List<(string Path, DateTime Used)>();
        foreach (var f in Directory.EnumerateFiles(dir, "*.raw"))
        {
            if (f == keep) continue;
            var used = File.GetLastWriteTimeUtc(f);
            if (Path.GetFileName(f).StartsWith(sizePrefix, StringComparison.Ordinal)) mine.Add((f, used));
            else if (used < DateTime.UtcNow.AddDays(-1)) TryDelete(f);
        }
        foreach (var (f, _) in mine.OrderByDescending(e => e.Used).Skip(Capacity - 1)) TryDelete(f);
    }

    private static void TryDelete(string f)
    {
        try { File.Delete(f); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}