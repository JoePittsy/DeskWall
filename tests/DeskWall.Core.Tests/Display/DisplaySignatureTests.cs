using DeskWall.Core.Display;
using Xunit;

public class DisplaySignatureTests
{
    [Fact]
    public void Key_RoundTrips()
    {
        var s = new DisplaySignature(@"\\?\DISPLAY#DELA1F2#5&abc#0#UID4353", 3440, 1440, 100);
        Assert.Equal(@"\\?\DISPLAY#DELA1F2#5&abc#0#UID4353 @ 3440x1440 @ 100%", s.Key);
        Assert.Equal(s, DisplaySignature.Parse(s.Key));
    }

    [Fact]
    public void Similarity_Prefers_Same_Device_Then_Aspect()
    {
        var mine = new DisplaySignature("A", 3440, 1440, 100);
        Assert.Equal(5, mine.Similarity(mine));
        Assert.Equal(3, mine.Similarity(new DisplaySignature("A", 1920, 1080, 100)));
        Assert.Equal(1, mine.Similarity(new DisplaySignature("B", 2560, 1080, 100)));   // 21:9 vs 21.5:9 within 1 percent? no: 2.37 vs 2.39 -> yes within 1%
        Assert.Equal(0, mine.Similarity(new DisplaySignature("B", 1920, 1080, 100)));
    }

    [Fact]
    public void Enumerate_Returns_At_Least_Primary()
    {
        var mons = Monitors.Enumerate();
        Assert.Contains(mons, m => m.IsPrimary);
        var p = mons.First(m => m.IsPrimary);
        Assert.True(p.Bounds.W > 0 && p.Bounds.H > 0);
        Assert.False(string.IsNullOrEmpty(p.WallpaperMonitorId));
        Assert.Equal(p.Bounds.W, p.Signature.Width);
    }
}
