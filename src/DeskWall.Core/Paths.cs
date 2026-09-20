namespace DeskWall.Core;

public static class Paths
{
    private static string? _runtimeDir;

    /// <summary>%LOCALAPPDATA%\DeskWall, created on first access.</summary>
    public static string RuntimeDir
    {
        get
        {
            if (_runtimeDir is null)
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskWall");
                Directory.CreateDirectory(dir);
                _runtimeDir = dir;
            }
            return _runtimeDir;
        }
    }

    public static string InRuntime(params string[] parts) => Path.Combine([RuntimeDir, .. parts]);
}
