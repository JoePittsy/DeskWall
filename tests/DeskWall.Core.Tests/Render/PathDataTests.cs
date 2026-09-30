using DeskWall.Core.Render;
using Xunit;

/// <summary>The path subset a shaped bar reads: absolute and relative M L H V C Z, repeated pairs,
/// and a clear refusal of anything else (the resolver then draws the plain box).</summary>
public class PathDataTests
{
    [Fact]
    public void Reads_Absolute_Relative_And_Implicit_Lines_With_Their_Bounds()
    {
        var p = PathData.Parse("M0,40 L20,10 30,25 l10-15 h10 v30 Z m5,5 C5,0 10,0 10,5");
        Assert.Equal(2, p.Figures.Count);
        var ridge = p.Figures[0];
        Assert.True(ridge.Closed);
        Assert.Equal(new PathData.Line(40, 10), ridge.Segments[2]);   // relative l from (30,25)
        Assert.Equal(new PathData.Line(50, 10), ridge.Segments[3]);   // h10
        Assert.Equal(new PathData.Line(50, 40), ridge.Segments[4]);   // v30
        Assert.Equal(new PathData.Cubic(5, 0, 10, 0, 10, 5), p.Figures[1].Segments[0]);
        Assert.Equal((0f, 0f, 50f, 45f), p.Bounds);   // the m5,5 after Z starts from (0,40)
        Assert.Equal(new PathData.Line(2e1f, 1.5f), PathData.Parse("M0 0L2e1 1.5").Figures[0].Segments[0]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("M0,0")]                 // nothing to draw
    [InlineData("L10,10")]               // no M
    [InlineData("M0,0 A5,5 0 0 1 10,10")] // arcs are not supported
    [InlineData("M0,0 L10")]             // half a pair
    public void Refuses_What_It_Cannot_Draw(string d) => Assert.Throws<FormatException>(() => PathData.Parse(d));
}
