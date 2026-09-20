using System.IO;
using DeskWall.Core;
using DeskWall.Core.Diagnostics;
using Xunit;

namespace DeskWall.Core.Tests.Diagnostics;

/// <summary>The daemon's half of the settings.json contract. The designer writes the file with a
/// camelCase naming policy and grows it freely; the daemon must read exactly one value out of it and
/// must never refuse to show a tray icon because of a file it could not understand.</summary>
public class DaemonSettingsTests
{
    private static string FilePath => Paths.InRuntime("settings.json");

    private static void Write(string json) => File.WriteAllText(FilePath, json);

    private static void Clean()
    {
        if (File.Exists(FilePath)) File.Delete(FilePath);
    }

    [Fact]
    public void True_When_The_File_Is_Missing()
    {
        Clean();
        Assert.True(DaemonSettings.TrayIconEnabled());
    }

    [Fact]
    public void Reads_False_From_The_Designers_Camel_Case_File()
    {
        try
        {
            // Exactly what DeskWall.Designer.Model.Settings.Save writes, extra members and all.
            Write("""
                {
                  "trayIcon": false,
                  "lastLayoutPath": "C:/x/a.json",
                  "showLayers": true
                }
                """);
            Assert.False(DaemonSettings.TrayIconEnabled());
        }
        finally { Clean(); }
    }

    [Fact]
    public void Reads_True_And_Ignores_Unknown_Members()
    {
        try
        {
            Write("""{"trayIcon": true, "windowLeft": 12.5, "nothingTheDaemonKnows": {"a": 1}}""");
            Assert.True(DaemonSettings.TrayIconEnabled());
        }
        finally { Clean(); }
    }

    [Fact]
    public void True_When_The_File_Is_Corrupt_Or_Says_Nothing()
    {
        try
        {
            Write("""{ this is not json""");
            Assert.True(DaemonSettings.TrayIconEnabled());
            Write("""{}""");
            Assert.True(DaemonSettings.TrayIconEnabled());
        }
        finally { Clean(); }
    }
}
