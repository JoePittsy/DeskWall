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

/// <summary>A dial's key must be as insensitive to sub-pixel wobble as a bar's: the hardware
/// source's 5-minute average moves in the fourth decimal between reads and would otherwise
/// repaint the arc every tick.</summary>
public class ResolvedDialTests
{
    [Fact]
    public void Dial_Key_Quantises_Fraction_To_Three_Decimals()
    {
        var a = new ResolvedDial("d", new Rect(0, 0, 80, 80), 0, 0.5001, Color.Parse("#46FFFFFF"), Color.Parse("#EBFFFFFF"), 6, 135, 270);
        var b = a with { Fraction = 0.5004 };
        var c = a with { Fraction = 0.501 };
        Assert.Equal(string.Join("|", a.KeyParts()), string.Join("|", b.KeyParts()));
        Assert.NotEqual(string.Join("|", a.KeyParts()), string.Join("|", c.KeyParts()));
    }

    [Fact]
    public void Dial_Paint_Bounds_Is_Its_Rect()
    {
        var d = new ResolvedDial("d", new Rect(5, 6, 80, 80), 0, 0.5, Color.Parse("#46FFFFFF"), Color.Parse("#EBFFFFFF"), 6, 135, 270);
        Assert.Equal(d.Rect, d.PaintBounds);
    }
}
