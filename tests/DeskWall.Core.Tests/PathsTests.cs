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
}
