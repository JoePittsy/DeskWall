using DeskWall.Core;
using Xunit;

public class PathsTests
{
    [Fact]
    public void RuntimeDir_IsUnderLocalAppData_AndExists()
    {
        var dir = Paths.RuntimeDir;
        Assert.EndsWith(@"\DeskWall", dir);
        Assert.True(Directory.Exists(dir));
    }
}
