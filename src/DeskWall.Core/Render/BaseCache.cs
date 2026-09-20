using System.Security.Cryptography;
using System.Text;

namespace DeskWall.Core.Render;

/// <summary>The base image scaled to the canvas, stored as a raw PBGRA dump so loading it is a
/// copy, not a decode. Keyed by path, mtime, size and fit.</summary>
public static class BaseCache
{
    public static string Ensure(string imagePath, int w, int h, Fit fit)
    {
        var mtime = File.GetLastWriteTimeUtc(imagePath).Ticks;
        var keySrc = $"{imagePath}|{mtime}|{w}x{h}|{fit}";
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(keySrc)))[..16];
        var dir = Paths.InRuntime("base");
        var path = Path.Combine(dir, $"{key}.raw");
        if (File.Exists(path)) return path;
        Directory.CreateDirectory(dir);
        using var src = Surface.Load(imagePath);
        using var dst = Surface.Create(w, h);
        dst.Clear(new Color(255, 0, 0, 0));
        dst.DrawSurface(src, new Rect(0, 0, w, h), fit);
        dst.SaveRaw(path);
        // keep the cache dir tidy: drop other .raw files older than a day
        foreach (var f in Directory.EnumerateFiles(dir, "*.raw"))
            if (f != path && File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-1)) File.Delete(f);
        return path;
    }
}
