using DeskWall.Core;
using DeskWall.Core.Render;
using DeskWall.Core.Resolve;
using Xunit;

/// <summary>Finding 14: an image replaced in place at the same path (fit/radius/opacity unchanged)
/// used to keep the same content key forever, so the old cover never redrew.</summary>
public class ResolvedImageContentKeyTests
{
    [Fact]
    public void Touching_The_File_Changes_The_Key()
    {
        var dir = Path.Combine(Path.GetTempPath(), "deskwall-tests", "img-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "cover.png");
        File.WriteAllBytes(path, [1, 2, 3]);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-10));
        var img = new ResolvedImage("i", new Rect(0, 0, 10, 10), 0, path, Fit.Cover, 0, 1);
        var before = ContentKey.Of(img);

        File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        var after = ContentKey.Of(img);

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void Missing_File_Does_Not_Throw_And_Still_Keys()
    {
        var missing = Path.Combine(Path.GetTempPath(), "deskwall-tests", "no-such-" + Guid.NewGuid().ToString("N") + ".png");
        var img = new ResolvedImage("i", new Rect(0, 0, 10, 10), 0, missing, Fit.Cover, 0, 1);
        Assert.Equal(16, ContentKey.Of(img).Length);
    }
}
