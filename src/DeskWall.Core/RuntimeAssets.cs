namespace DeskWall.Core;

/// <summary>The assets a layout reaches through <c>runtime:assets/...</c> (the weather icons) ship
/// in <c>assets\</c> beside the exe and are used from the runtime dir. The daemon copies what is
/// missing at start and the designer when it opens, so an upgrade that adds files (the night icons)
/// reaches the wallpaper without the designer ever being opened. A file already in the runtime dir
/// is never overwritten: it is either the same file or one the owner replaced on purpose.</summary>
public static class RuntimeAssets
{
    /// <summary>What ships beside the running exe.</summary>
    public static string ShippedDir => Path.Combine(AppContext.BaseDirectory, "assets");

    public sealed record Result(int Copied, IReadOnlyList<string> Failed);

    /// <summary>Copies every file under <paramref name="shippedDir"/> (recursively) that is missing
    /// from <paramref name="runtimeDir"/> (default: the runtime dir's <c>assets</c>), keeping its
    /// relative path. A missing <paramref name="shippedDir"/> is a no-op. A file that cannot be
    /// copied is reported in <see cref="Result.Failed"/> and the rest still are.</summary>
    public static Result CopyMissing(string shippedDir, string? runtimeDir = null)
    {
        if (!Directory.Exists(shippedDir)) return new(0, []);
        runtimeDir ??= Paths.InRuntime("assets");
        var copied = 0;
        var failed = new List<string>();
        foreach (var src in Directory.EnumerateFiles(shippedDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(shippedDir, src);
            var target = Path.Combine(runtimeDir, relative);
            if (File.Exists(target)) continue;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(src, target, overwrite: false);
                copied++;
            }
            // Another process (the designer starting at the same moment) won the race: the file is
            // there, which is all that was wanted.
            catch (IOException) when (File.Exists(target)) { }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed.Add($"{relative}: {ex.Message}"); }
        }
        return new(copied, failed);
    }
}
