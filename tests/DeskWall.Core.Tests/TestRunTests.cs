using System.Diagnostics;
using Xunit;

/// <summary>#85/#59: each test run owns one folder under %TEMP%\deskwall-tests, so concurrent runs
/// cannot delete each other's files, and what a dead run left behind is swept by the next.</summary>
public class TestRunTests
{
    [Fact]
    public void The_Run_Home_Is_Inside_A_Folder_Named_For_This_Process()
    {
        Assert.Equal(TestRun.Home, Environment.GetEnvironmentVariable("DESKWALL_HOME"));
        Assert.Equal(TestRun.Root, Path.GetDirectoryName(TestRun.Home));
        Assert.Equal(TestRun.Parent, Path.GetDirectoryName(TestRun.Root));
        Assert.Matches($@"^core-{Environment.ProcessId}-[0-9a-f]{{8}}$", Path.GetFileName(TestRun.Root));
        Assert.True(Directory.Exists(TestRun.Home));
    }

    [Fact]
    public void Sweep_Removes_Dead_Runs_And_Old_Leftovers_But_Not_Live_Runs_Or_Recent_Files()
    {
        var parent = Path.Combine(TestRun.Root, "sweep-" + Guid.NewGuid().ToString("N")[..8]);
        int deadPid;
        using (var p = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit") { CreateNoWindow = true, UseShellExecute = false })!)
        {
            p.WaitForExit();
            deadPid = p.Id;
        }
        string Dir(string name, DateTime? written = null)
        {
            var d = Path.Combine(parent, name);
            Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(d, "f.txt"), "x");
            if (written is { } w) Directory.SetLastWriteTimeUtc(d, w);
            return d;
        }
        var now = DateTime.UtcNow;
        var dead = Dir($"core-{deadPid}-0123abcd");
        var live = Dir($"designer-{Environment.ProcessId}-0123abcd", now.AddDays(-30));   // live even when old
        var oldLegacy = Dir("home", now.AddDays(-2));
        var newLegacy = Dir("home-designer", now.AddHours(-1));
        var oldFile = Path.Combine(parent, "scaled-shape-base.png");
        File.WriteAllText(oldFile, "x");
        File.SetLastWriteTimeUtc(oldFile, now.AddDays(-2));

        TestRun.Sweep(parent, now);

        Assert.False(Directory.Exists(dead));
        Assert.True(Directory.Exists(live));
        Assert.False(Directory.Exists(oldLegacy));
        Assert.True(Directory.Exists(newLegacy));   // could be a run of a branch from before #85
        Assert.False(File.Exists(oldFile));
    }

    [Fact]
    public void Sweep_Of_A_Missing_Folder_Does_Nothing()
        => TestRun.Sweep(Path.Combine(TestRun.Root, "no-such-" + Guid.NewGuid().ToString("N")), DateTime.UtcNow);
}
