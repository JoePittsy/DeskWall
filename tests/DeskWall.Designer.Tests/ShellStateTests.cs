using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DeskWall.Core;
using DeskWall.Core.Display;
using DeskWall.Core.Layout;
using DeskWall.Designer.Model;
using Xunit;

namespace DeskWall.Designer.Tests;

/// <summary>The shell's decisions that are not WPF. MainWindow itself needs a message pump; these
/// are the parts that do not.</summary>
public class ShellStateTests
{
    private const string Ultrawide = @"\?\DISPLAY#DELA1C9#5&2a1b8d3&0&UID4353#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7} @ 3440x1440 @ 100%";

    [Fact]
    public void Layout_Path_Is_Under_Runtime_Layouts_And_Is_A_Legal_File_Name()
    {
        var sig = DisplaySignature.Parse(Ultrawide);
        var path = ShellState.LayoutPathFor(sig);

        Assert.Equal(Paths.InRuntime("layouts"), Path.GetDirectoryName(path));
        Assert.Equal(-1, Path.GetFileName(path).IndexOfAny(Path.GetInvalidFileNameChars()));
        Assert.EndsWith(".json", path, StringComparison.Ordinal);
        // Same display, same file: applying twice must not make a second layout.
        Assert.Equal(path, ShellState.LayoutPathFor(DisplaySignature.Parse(Ultrawide)));
    }

    [Fact]
    public void Two_Displays_Get_Two_Files()
    {
        var a = ShellState.LayoutPathFor(new DisplaySignature("DISPLAY1", 3440, 1440, 100));
        var b = ShellState.LayoutPathFor(new DisplaySignature("DISPLAY1", 2560, 1440, 100));
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Display_Choices_List_Monitors_First_And_Never_Twice()
    {
        var attached = new[] { "DISPLAY1 @ 3440x1440 @ 100%", "DISPLAY2 @ 1920x1080 @ 100%" };
        var store = new[] { "DISPLAY2 @ 1920x1080 @ 100%", "RDP @ 2560x1080 @ 100%" };

        var choices = ShellState.DisplayChoices(attached, store);

        Assert.Equal(
            new[] { "DISPLAY1 @ 3440x1440 @ 100%", "DISPLAY2 @ 1920x1080 @ 100%", "RDP @ 2560x1080 @ 100%" },
            choices.Select(c => c.Key));
    }

    [Fact]
    public void Display_Label_Keeps_The_Geometry_And_The_Model_But_Not_The_Guid()
    {
        var label = ShellState.DisplayLabel(Ultrawide);
        Assert.Contains("3440x1440 @ 100%", label, StringComparison.Ordinal);
        Assert.Contains("DELA1C9", label, StringComparison.Ordinal);
        Assert.DoesNotContain("e6f07b5f", label, StringComparison.Ordinal);
    }

    [Fact]
    public void Display_Label_Falls_Back_To_The_Raw_Key_When_It_Is_Not_A_Signature()
    {
        Assert.Equal("not a signature", ShellState.DisplayLabel("not a signature"));
    }

    [Theory]
    [InlineData(null, false, "(not saved for this display)")]
    [InlineData(null, true, "(not saved for this display) *")]
    [InlineData(@"C:\x\layouts\clock-disks.json", false, "clock-disks.json")]
    [InlineData(@"C:\x\layouts\clock-disks.json", true, "clock-disks.json *")]
    public void File_Label_Names_The_File_And_Marks_Unsaved_Edits(string? path, bool dirty, string expected)
        => Assert.Equal(expected, ShellState.FileLabel(path, dirty));

    // ---- what the window opens ------------------------------------------------------------------
    //
    // Owner feedback 2026-09-21: "it's not letting me edit the existing ones". The designer resolved
    // the layout for the current display by exact signature and found nothing over RDP, where this
    // display is 1692x1031 and the store's entries are keyed at 3440x1440, so it opened "New layout"
    // over the top of a desktop the daemon was happily painting from a file on disk.

    private static readonly DisplaySignature Rdp = new("Default_Monitor", 1692, 1031, 100);
    private static readonly DisplaySignature Console = DisplaySignature.Parse(Ultrawide);
    private const string DefaultBase = @"C:\Windows\base.jpg";

    private static LayoutFile Authored() => LayoutFile.Parse("""
        { "version": 1, "baseImage": "C:\\x.jpg", "sources": [],
          "components": [ { "type": "text", "id": "clock", "rect": [3220, 40, 172, 60], "text": "12:34" } ] }
        """);

    [Fact]
    public void Nothing_Registered_Anywhere_Opens_A_New_Layout_For_This_Display()
    {
        var target = ShellState.OpenFrom(null, Rdp, _ => throw new InvalidOperationException("must not read a file"), DefaultBase);

        Assert.Null(target.Path);
        Assert.Equal(Rdp, target.Signature);
        Assert.Equal(DefaultBase, target.Layout.BaseImage);
        Assert.Empty(target.Layout.Components);
    }

    [Fact]
    public void An_Exact_Match_Opens_Its_Own_File_Unchanged()
    {
        var layout = Authored();
        var resolution = new LayoutResolution(layout, @"C:\x\layouts\ultrawide.json", Console, Scaled: false);

        var target = ShellState.OpenFrom(resolution, Console, _ => throw new InvalidOperationException("already loaded"), DefaultBase);

        Assert.Same(layout, target.Layout);
        Assert.Equal(@"C:\x\layouts\ultrawide.json", target.Path);
        Assert.Equal(Console, target.Signature);
    }

    /// <summary>The RDP case, and the whole point: the store matched a layout authored at 3440x1440
    /// and handed back a copy scaled to this session. Editing that copy would let the owner drag
    /// shrunken rects around and then write them over the authored file. Open the file instead, on
    /// the canvas it was authored on, at the path both signatures already resolve to.</summary>
    [Fact]
    public void A_Closest_Match_Opens_The_Authored_File_On_The_Authored_Canvas()
    {
        var scaled = LayoutScaler.Scale(Authored(), Console, Rdp);
        var resolution = new LayoutResolution(scaled, @"C:\x\layouts\ultrawide.json", Console, Scaled: true);
        var reads = new List<string>();

        var target = ShellState.OpenFrom(resolution, Rdp, p => { reads.Add(p); return Authored(); }, DefaultBase);

        Assert.Equal([@"C:\x\layouts\ultrawide.json"], reads);
        Assert.Equal(@"C:\x\layouts\ultrawide.json", target.Path);
        Assert.Equal(Console, target.Signature);                       // 3440x1440, not this session's
        Assert.Equal(new Rect(3220, 40, 172, 60), target.Layout.Components.Single().Rect);
        Assert.NotEqual(scaled.Components.Single().Rect, target.Layout.Components.Single().Rect);
    }

    /// <summary>The authored file going unreadable between the store reading it and the window
    /// opening it is a race that should cost nothing but the path: opening the scaled copy is fine,
    /// saving it over the authored file is not.</summary>
    [Fact]
    public void An_Unreadable_Authored_File_Never_Becomes_A_Path_To_Save_Over()
    {
        var scaled = LayoutScaler.Scale(Authored(), Console, Rdp);
        var resolution = new LayoutResolution(scaled, @"C:\x\layouts\ultrawide.json", Console, Scaled: true);

        var target = ShellState.OpenFrom(resolution, Rdp, _ => null, DefaultBase);

        Assert.Null(target.Path);
        Assert.Equal(Rdp, target.Signature);
        Assert.Same(scaled, target.Layout);
    }

    /// <summary>End to end through a real store: one entry, keyed for the ultrawide, opened from a
    /// display that has no entry of its own. This is the shape of the runtime dir on JOES-PC.</summary>
    [Fact]
    public void A_Store_Keyed_For_Another_Display_Still_Opens_That_Displays_File()
    {
        var dir = Path.Combine(Path.GetTempPath(), "deskwall-tests", "openfrom-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "column-system.json");
            Authored().Save(file);
            var store = new LayoutStore(Path.Combine(dir, "layouts.json"));
            store.Set(Console, file);

            var resolution = store.Resolve(Rdp);
            Assert.NotNull(resolution);
            Assert.True(resolution!.Scaled);                           // closest match, not exact

            var target = ShellState.OpenFrom(resolution, Rdp, p => LayoutFile.Load(p), DefaultBase);

            Assert.Equal(file, target.Path);
            Assert.Equal(Console, target.Signature);
            Assert.Equal(new Rect(3220, 40, 172, 60), target.Layout.Components.Single().Rect);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Banner_Names_The_Signature_It_Was_Scaled_From()
        => Assert.Equal("scaled from RDP @ 2560x1080 @ 100%", ShellState.BannerText("RDP @ 2560x1080 @ 100%"));

    [Fact]
    public void A_Remembered_Window_Is_Kept_When_It_Overlaps_A_Monitor()
    {
        var screens = new[] { new Rect(0, 0, 3440, 1440) };
        Assert.True(ShellState.OnScreen(100, 100, 1500, 950, screens));
        Assert.True(ShellState.OnScreen(-200, -50, 1500, 950, screens));   // half off the left edge is still draggable
    }

    [Fact]
    public void A_Remembered_Window_Is_Thrown_Away_When_The_Monitor_It_Was_On_Has_Gone()
    {
        // The Apollo case: the layout was saved on a second screen that is no longer there.
        var screens = new[] { new Rect(0, 0, 3440, 1440) };
        Assert.False(ShellState.OnScreen(4000, 100, 1500, 950, screens));
        Assert.False(ShellState.OnScreen(100, 100, 1500, 950, Array.Empty<Rect>()));
        Assert.False(ShellState.OnScreen(100, 100, 0, 950, screens));
    }

    [Fact]
    public void CopyAssets_Copies_Files_Into_The_Runtime_Weather_Folder()
    {
        var src = Path.Combine(Path.GetTempPath(), "deskwall-tests", "copyassets-src-" + Guid.NewGuid());
        Directory.CreateDirectory(src);
        try
        {
            File.WriteAllText(Path.Combine(src, "0.png"), "fake-png");
            File.WriteAllText(Path.Combine(src, "LICENSE"), "mit");

            ShellState.CopyAssets(src);

            var dst = Paths.InRuntime("assets", "weather");
            Assert.True(File.Exists(Path.Combine(dst, "0.png")));
            Assert.True(File.Exists(Path.Combine(dst, "LICENSE")));
        }
        finally { Directory.Delete(src, recursive: true); }
    }

    [Fact]
    public void CopyAssets_Never_Overwrites_A_File_Already_There()
    {
        var src = Path.Combine(Path.GetTempPath(), "deskwall-tests", "copyassets-src-" + Guid.NewGuid());
        Directory.CreateDirectory(src);
        try
        {
            File.WriteAllText(Path.Combine(src, "0.png"), "fake-png");
            ShellState.CopyAssets(src);

            var dst = Paths.InRuntime("assets", "weather");
            var target = Path.Combine(dst, "0.png");
            File.WriteAllText(target, "already-there");

            ShellState.CopyAssets(src);

            Assert.Equal("already-there", File.ReadAllText(target));
        }
        finally { Directory.Delete(src, recursive: true); }
    }

    [Fact]
    public void CopyAssets_Is_A_NoOp_When_The_Source_Directory_Is_Missing()
    {
        var src = Path.Combine(Path.GetTempPath(), "deskwall-tests", "copyassets-missing-" + Guid.NewGuid());
        Assert.False(Directory.Exists(src));

        ShellState.CopyAssets(src);   // must not throw
    }
    // ---- where the window opens ---------------------------------------------------------------

    private static readonly Rect[] OneMonitor = [new Rect(0, 0, 3440, 1440)];

    [Fact]
    public void Placement_Restores_A_Rectangle_That_Still_Lands_On_A_Monitor()
    {
        var placement = ShellState.Placement(200, 150, 1440, 900, OneMonitor);

        Assert.NotNull(placement);
        Assert.Equal((200d, 150d, 1440d, 900d), placement!.Value);
    }

    [Fact]
    public void Placement_Is_Null_When_The_Window_Would_Open_Off_Every_Monitor()
    {
        // The remembered position of a second monitor that is no longer attached - or of the
        // ultrawide before Apollo streaming dropped the session to 1920x1200.
        Assert.Null(ShellState.Placement(3600, 100, 1440, 900, OneMonitor));
        Assert.Null(ShellState.Placement(100, -1200, 1440, 900, OneMonitor));
    }

    [Fact]
    public void Placement_Keeps_A_Window_That_Only_Overlaps_An_Edge()
    {
        // Half off the right-hand edge is still draggable back, so it is not thrown away.
        Assert.NotNull(ShellState.Placement(3000, 100, 1440, 900, OneMonitor));
    }

    [Fact]
    public void Placement_Is_Null_When_Nothing_Was_Remembered()
    {
        Assert.Null(ShellState.Placement(null, null, null, null, OneMonitor));
        Assert.Null(ShellState.Placement(200, null, 1440, 900, OneMonitor));
    }

    [Fact]
    public void Placement_Is_Null_When_No_Monitor_Could_Be_Enumerated()
    {
        // A session still coming up: better the default, centred, than a position nobody can verify.
        Assert.Null(ShellState.Placement(200, 150, 1440, 900, []));
    }

    [Fact]
    public void Placement_Refuses_A_Degenerate_Rectangle()
        => Assert.Null(ShellState.Placement(200, 150, 0, 900, OneMonitor));

    [Fact]
    public void The_Default_Window_Is_The_Size_The_Three_Panes_Need()
    {
        Assert.Equal(1440, ShellState.DefaultWidth);
        Assert.Equal(900, ShellState.DefaultHeight);
    }
}
