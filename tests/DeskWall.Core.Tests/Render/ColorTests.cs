using DeskWall.Core.Render;
using Xunit;

public class ColorTests
{
    [Theory]
    [InlineData("#FFFFFF", 255, 255, 255, 255)]
    [InlineData("#80FF0000", 128, 255, 0, 0)]
    [InlineData("#abc", 255, 170, 187, 204)]
    public void Parse_Hex(string s, byte a, byte r, byte g, byte b)
    {
        var c = Color.Parse(s);
        Assert.Equal((a, r, g, b), (c.A, c.R, c.G, c.B));
    }

    [Fact]
    public void Parse_Invalid_Throws() => Assert.Throws<FormatException>(() => Color.Parse("red"));
}
