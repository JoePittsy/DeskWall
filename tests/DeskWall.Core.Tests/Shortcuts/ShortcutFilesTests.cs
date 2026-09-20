using DeskWall.Core;
using DeskWall.Core.Resolve;
using DeskWall.Core.Shortcuts;
using Xunit;

public class ShortcutFilesTests
{
    private static string TempLnk() { var d = Path.Combine(Path.GetTempPath(), "deskwall-tests", "lnk-" + Guid.NewGuid().ToString("N")[..8]); Directory.CreateDirectory(d); return Path.Combine(d, "\u00A0.lnk"); }

    [Theory]
    [InlineData("steam://rungameid/620", "steam://rungameid/620", "", "")]
    [InlineData("\"C:\\Program Files\\X\\x.exe\" --start abc", "C:\\Program Files\\X\\x.exe", "--start abc", "C:\\Program Files\\X")]
    [InlineData("explorer.exe D:\\", "explorer.exe", "D:\\", "")]
    public void SpecFor_Splits_Program_And_Arguments(string target, string prog, string args, string wd)
    {
        var spec = ShortcutFiles.SpecFor(new ResolvedShortcut("s", new Rect(0, 0, 1, 1), 0, target, "Play it", 0), @"C:\blank.ico");
        Assert.Equal(prog, spec.Target);
        Assert.Equal(args, spec.Arguments);
        Assert.Equal(wd, spec.WorkingDirectory);
        Assert.Equal("Play it", spec.Description);
        Assert.Equal(@"C:\blank.ico", spec.IconPath);
    }

    [Fact]
    public void Write_Read_Delete_RoundTrip()
    {
        var lnk = TempLnk();
        var ico = BlankIcon.Ensure();
        var spec = new ShortcutSpec(@"C:\Windows\explorer.exe", @"D:\", @"C:\Windows", "Open D", ico);
        ShortcutFiles.Write(lnk, spec);
        Assert.True(File.Exists(lnk));
        var back = ShortcutFiles.Read(lnk)!;
        Assert.Equal(spec.Target, back.Target, ignoreCase: true);
        Assert.Equal(spec.Arguments, back.Arguments);
        Assert.Equal(spec.Description, back.Description);
        Assert.Equal(ico, back.IconPath, ignoreCase: true);
        ShortcutFiles.Write(lnk, spec with { Description = "Changed" });
        Assert.Equal("Changed", ShortcutFiles.Read(lnk)!.Description);
        ShortcutFiles.Delete(lnk);
        Assert.False(File.Exists(lnk));
        Assert.Null(ShortcutFiles.Read(lnk));
    }
}
