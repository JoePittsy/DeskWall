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
