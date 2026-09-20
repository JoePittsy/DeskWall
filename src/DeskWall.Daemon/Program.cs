using DeskWall.Core;

namespace DeskWall.Daemon;

internal static class Program
{
    private static int Main(string[] argv)
    {
        var cmd = argv.Length == 0 ? "run" : argv[0];
        switch (cmd)
        {
            case "paths":
                Console.WriteLine(Paths.RuntimeDir);
                return 0;
            default:
                Console.Error.WriteLine($"deskwall: unknown or not yet implemented command '{cmd}'");
                return 2;
        }
    }
}
