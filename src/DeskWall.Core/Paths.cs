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
}
