namespace DeskWall.Core;

public static class Paths
{
    private static string? _runtimeDir;

    /// <summary>%LOCALAPPDATA%\DeskWall, created on first access. The DESKWALL_HOME environment
    /// variable overrides it (tests point it at a temp folder so they never touch the real state).</summary>
    public static string RuntimeDir
    {
        get
        {
            if (_runtimeDir is null)
            {
                var dir = Environment.GetEnvironmentVariable("DESKWALL_HOME");
                if (string.IsNullOrWhiteSpace(dir))
                    dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskWall");
                Directory.CreateDirectory(dir);
                _runtimeDir = dir;
            }
            return _runtimeDir;
        }
    }

    public static string InRuntime(params string[] parts) => Path.Combine([RuntimeDir, .. parts]);

    /// <summary>"runtime:assets/weather/61.png" -> %LOCALAPPDATA%\DeskWall\assets\weather\61.png.
    /// Lets a committed layout, widget or script path name a per-user file without a per-user
    /// absolute path. Braces are not used for the token because a composite format string would
    /// swallow them. Anything without the prefix comes back untouched; RuntimeDir (and the mkdir
    /// behind it) is only reached when the prefix is actually there.</summary>
    public static string ExpandRuntime(string source)
    {
        const string prefix = "runtime:";
        if (!source.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return source;
        var rest = source[prefix.Length..].Replace('/', '\\').TrimStart('\\');
        return InRuntime(rest.Split('\\', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>A path a user wrote in a layout: %ENV% variables and then the runtime: prefix.
    /// One place, so a command's program, a command's working folder, a file source's path and an
    /// image's source cannot drift apart on what they accept.</summary>
    public static string ExpandPath(string path) => ExpandRuntime(Environment.ExpandEnvironmentVariables(path));
}
