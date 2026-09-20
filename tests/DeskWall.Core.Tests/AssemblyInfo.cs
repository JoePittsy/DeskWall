using System.Runtime.CompilerServices;
using Xunit;

// Surface holds process-wide COM factories and the tests touch the real desktop; run classes serially.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

internal static class TestEnvironment
{
    /// <summary>Point the runtime dir at a temp folder before any test touches Paths, so tests never
    /// write into the user's real %LOCALAPPDATA%\DeskWall.</summary>
    [ModuleInitializer]
    internal static void Init()
    {
        var dir = Path.Combine(Path.GetTempPath(), "deskwall-tests", "home");
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("DESKWALL_HOME", dir);
    }
}
