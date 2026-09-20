using System.IO;
using DeskWall.Core;
using DeskWall.Designer.Model;
using Xunit;

namespace DeskWall.Designer.Tests;

public class SettingsTests
{
    private static string FilePath => Paths.InRuntime("settings.json");

    private static void Clean()
    {
        if (File.Exists(FilePath)) File.Delete(FilePath);
        if (Directory.Exists(FilePath)) Directory.Delete(FilePath, recursive: true);
        if (File.Exists(FilePath + ".tmp")) File.Delete(FilePath + ".tmp");
    }

    [Fact]
    public void Load_Default_When_Missing()
    {
        Clean();
        var s = Settings.Load();
        Assert.True(s.TrayIcon);
        Assert.Null(s.LastLayoutPath);
        Assert.Null(s.LastSignatureKey);
        // The shell opens with every panel showing and lets Windows place the window.
        Assert.True(s.ShowSources);
        Assert.True(s.ShowProperties);
        Assert.True(s.ShowLayers);
        Assert.Null(s.WindowLeft);
        Assert.Null(s.WindowWidth);
        Assert.False(s.WindowMaximized);
    }

    /// <summary>Task 8: the shell's own layout lives in the same file the daemon reads, so it has to
    /// round trip through the same serializer without disturbing what was already there.</summary>
    [Fact]
    public void Shell_Layout_Round_Trips_Beside_The_Daemons_Setting()
    {
        Clean();
        try
        {
            new Settings
            {
                TrayIcon = false,
                ShowSources = false,
                ShowProperties = true,
                ShowLayers = false,
                WindowLeft = -8,
                WindowTop = 12.5,
                WindowWidth = 1500,
                WindowHeight = 950,
                WindowMaximized = true,
            }.Save();

            var loaded = Settings.Load();
            Assert.False(loaded.TrayIcon);
            Assert.False(loaded.ShowSources);
            Assert.True(loaded.ShowProperties);
            Assert.False(loaded.ShowLayers);
            Assert.Equal(-8, loaded.WindowLeft);
            Assert.Equal(12.5, loaded.WindowTop);
            Assert.Equal(1500, loaded.WindowWidth);
            Assert.Equal(950, loaded.WindowHeight);
            Assert.True(loaded.WindowMaximized);
        }
        finally { Clean(); }
    }

    /// <summary>The daemon's reader (DeskWall.Core.Diagnostics.DaemonSettings) matches on camelCase
    /// property names. If this file ever stopped writing them, the daemon would silently fall back to
    /// "tray on" and the settings page would look like it does nothing.</summary>
    [Fact]
    public void Save_Writes_The_Camel_Case_Name_The_Daemon_Reads()
    {
        Clean();
        try
        {
            new Settings { TrayIcon = false }.Save();
            Assert.Contains("\"trayIcon\": false", File.ReadAllText(FilePath), StringComparison.Ordinal);
        }
        finally { Clean(); }
    }

    [Fact]
    public void Save_Load_Round_Trips()
    {
        Clean();
        try
        {
            var s = new Settings { TrayIcon = false, LastLayoutPath = @"C:\x\layout.json", LastSignatureKey = "DISPLAY1 @ 3440x1440 @ 100%" };
            s.Save();

            var loaded = Settings.Load();
            Assert.False(loaded.TrayIcon);
            Assert.Equal(@"C:\x\layout.json", loaded.LastLayoutPath);
            Assert.Equal("DISPLAY1 @ 3440x1440 @ 100%", loaded.LastSignatureKey);
        }
        finally { Clean(); }
    }

    /// <summary>Finding 10 discipline (LayoutFile.Save, LayoutStore.Save) applies here too: a failing
    /// Save must not leave settings.json.tmp behind. Forcing the destination to already be a
    /// directory makes File.Move fail after the tmp file has already been written.</summary>
    [Fact]
    public void Save_Failure_Leaves_No_Tmp_File()
    {
        Clean();
        Directory.CreateDirectory(FilePath);
        try
        {
            var s = new Settings();
            Assert.ThrowsAny<Exception>(s.Save);
            Assert.False(File.Exists(FilePath + ".tmp"));
        }
        finally { Clean(); }
    }
}
