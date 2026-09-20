using System.IO;
using DeskWall.Core;
using DeskWall.Core.Display;
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
}
