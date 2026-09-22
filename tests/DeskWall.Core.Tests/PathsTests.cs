using DeskWall.Core;
using Xunit;

public class PathsTests
{
    [Fact]
    public void RuntimeDir_HonoursDeskwallHome_AndExists()
    {
        // AssemblyInfo's module initializer points DESKWALL_HOME at a temp folder for the whole test run.
        var dir = Paths.RuntimeDir;
        Assert.Equal(Environment.GetEnvironmentVariable("DESKWALL_HOME"), dir);
        Assert.True(Directory.Exists(dir));
        Assert.DoesNotContain(@"\AppData\Local\DeskWall", dir, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A committed layout or widget must never carry
    /// C:\Users\JosephPitts\AppData\Local\DeskWall inside it. `runtime:` is the portable way to
    /// name a per-user file; image paths already understood it, and now a command and a file do too.
    /// Paths caches RuntimeDir, so a test cannot set its own DESKWALL_HOME: the assembly-wide one
    /// from AssemblyInfo is what these assert against.</summary>
    [Fact]
    public void ExpandRuntime_Maps_The_Prefix_Into_The_Runtime_Dir()
    {
        Assert.Equal(Paths.InRuntime("scripts", "progress.ps1"), Paths.ExpandRuntime(@"runtime:scripts\progress.ps1"));
        Assert.Equal(Paths.InRuntime("assets", "weather", "61.png"), Paths.ExpandRuntime("runtime:assets/weather/61.png"));
        Assert.Equal(Paths.InRuntime("scripts"), Paths.ExpandRuntime(@"RUNTIME:\scripts"));
        Assert.Equal(Paths.RuntimeDir, Paths.ExpandRuntime("runtime:"));
    }

    [Fact]
    public void ExpandRuntime_Leaves_Every_Other_Path_Alone()
    {
        Assert.Equal(@"C:\tools\x.ps1", Paths.ExpandRuntime(@"C:\tools\x.ps1"));
        Assert.Equal("pwsh.exe", Paths.ExpandRuntime("pwsh.exe"));
        Assert.Equal("%APPDATA%/x", Paths.ExpandRuntime("%APPDATA%/x"));   // env expansion is ExpandPath's job
        Assert.Equal("", Paths.ExpandRuntime(""));
    }

    [Fact]
    public void ExpandPath_Does_Both_Env_Variables_And_The_Runtime_Prefix()
    {
        Assert.Equal(Paths.InRuntime("scripts", "progress.ps1"), Paths.ExpandPath(@"runtime:scripts\progress.ps1"));
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.Windows), Paths.ExpandPath("%SystemRoot%"));
        Assert.Equal("pwsh.exe", Paths.ExpandPath("pwsh.exe"));
    }
}
