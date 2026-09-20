using System.IO;
using System.Runtime.CompilerServices;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

internal static class TestEnvironment
{
    /// <summary>Same isolation as the Core tests: never touch the real runtime dir.</summary>
    [ModuleInitializer]
    internal static void Init()
    {
        var dir = Path.Combine(Path.GetTempPath(), "deskwall-tests", "home-designer");
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("DESKWALL_HOME", dir);
    }
}
