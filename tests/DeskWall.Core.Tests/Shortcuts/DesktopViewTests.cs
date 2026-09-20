using System.Diagnostics;
using DeskWall.Core;
using DeskWall.Core.Shortcuts;
using Xunit;

namespace DeskWall.Core.Tests.Shortcuts;

/// <summary>Talks to the live desktop. Every file it makes is named DeskWallTest-* and deleted in a
/// finally; it never touches the non-breaking-space slot files the real tool owns.</summary>
public class DesktopViewTests
{
    private const DesktopFolderFlags PlacementMask = DesktopFolderFlags.AutoArrange | DesktopFolderFlags.SnapToGrid;

    private static string DesktopDir() => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    private static string TestLnk() => Path.Combine(DesktopDir(), $"DeskWallTest-{Guid.NewGuid():N}.lnk");

    /// <summary>Task 3's ShortcutFiles is being written in another lane; this lane only needs *a* .lnk,
    /// so it borrows WScript.Shell through powershell rather than depending on that type.</summary>
    private static void WriteShortcut(string lnk, string target)
    {
        var script = $"$s = (New-Object -ComObject WScript.Shell).CreateShortcut('{lnk}'); " +
                     $"$s.TargetPath = '{target}'; $s.Description = 'DeskWall test'; $s.Save()";
        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(script);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0 && File.Exists(lnk), $"could not write {lnk}: {err}");
    }

    [Fact]
    public void View_Is_Available_And_Reports_Spacing_And_IconSize()
    {
        Assert.True(DesktopView.IsAvailable());
        var (sx, sy) = DesktopView.Spacing();
        Assert.InRange(sx, 40, 400);
        Assert.InRange(sy, 40, 400);
        Assert.Contains(DesktopView.IconSize(), new[] { 16, 32, 48, 96, 256 });
    }

    [Fact]
    public void Position_Then_GetPosition_RoundTrips_For_A_Test_Shortcut()
    {
        var lnk = TestLnk();
        try
        {
            WriteShortcut(lnk, @"C:\Windows\explorer.exe");
            DesktopFlags.EnsurePlacementAllowed();
            Thread.Sleep(1500);                      // Explorer notices the new file asynchronously
            DesktopView.Position([(lnk, 700, 300)]);
            Thread.Sleep(500);
            var got = DesktopView.GetPosition(lnk);
            Assert.NotNull(got);
            Assert.InRange(got!.Value.X, 690, 710);
            Assert.InRange(got.Value.Y, 290, 310);
        }
        finally { if (File.Exists(lnk)) File.Delete(lnk); }
    }

    [Fact]
    public void GetPosition_Of_A_Missing_Item_Is_Null_And_Position_Throws()
    {
        var lnk = Path.Combine(DesktopDir(), "DeskWallTest-missing.lnk");
        Assert.Null(DesktopView.GetPosition(lnk));
        Assert.Throws<FileNotFoundException>(() => DesktopView.Position([(lnk, 0, 0)]));
    }

    [Fact]
    public void Flags_Round_Trip_And_Restore()
    {
        var before = DesktopView.Flags();
        var file = Paths.InRuntime("desktop-flags.json");
        if (File.Exists(file)) File.Delete(file);
        try
        {
            DesktopFlags.EnsurePlacementAllowed();
            var during = DesktopView.Flags();
            Assert.False(during.HasFlag(DesktopFolderFlags.AutoArrange));
            Assert.False(during.HasFlag(DesktopFolderFlags.SnapToGrid));
            // The save file only appears when there was something to turn off. On a desktop where both
            // flags are already off (the owner's), EnsurePlacementAllowed is a no-op by design.
            Assert.Equal((before & PlacementMask) != 0, File.Exists(file));
        }
        finally
        {
            DesktopFlags.Restore();
            Assert.Equal(before & PlacementMask, DesktopView.Flags() & PlacementMask);
        }
    }

    /// <summary>Exercises the read-and-delete half of Restore without ever switching a flag ON: turning
    /// snap-to-grid or auto-arrange on would make Explorer move every real icon on the desktop.</summary>
    [Fact]
    public void Restore_Consumes_A_Saved_File_And_Is_A_NoOp_Without_One()
    {
        var file = Paths.InRuntime("desktop-flags.json");
        var before = DesktopView.Flags() & PlacementMask;
        try
        {
            File.WriteAllText(file, "{\"Original\":0}");
            DesktopFlags.Restore();
            Assert.False(File.Exists(file));
            Assert.Equal(before, DesktopView.Flags() & PlacementMask);
            DesktopFlags.Restore();   // nothing saved: no throw, no change
            Assert.Equal(before, DesktopView.Flags() & PlacementMask);
        }
        finally { if (File.Exists(file)) File.Delete(file); }
    }
}
