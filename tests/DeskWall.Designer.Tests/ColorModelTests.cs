using DeskWall.Designer.Model;
using Xunit;
using CoreColor = DeskWall.Core.Render.Color;

public class ColorModelTests
{
    [Fact]
    public void Every_Colour_RoundTrips_Through_Hsv()
    {
        // Every R and G at a stride that hits both ends, B and A likewise: 52^2 * 18 * 3 colours.
        for (var r = 0; r <= 255; r += 5)
        for (var g = 0; g <= 255; g += 5)
        for (var b = 0; b <= 255; b += 15)
        foreach (var a in new byte[] { 0, 0xA0, 255 })
        {
            var c = new CoreColor(a, (byte)r, (byte)g, (byte)b);
            Assert.Equal(c, ColorModel.FromColor(c).ToColor());
        }
    }

    [Theory]
    [InlineData("#A0FFFFFF", "#A0FFFFFF")]
    [InlineData("#46ffffff", "#46FFFFFF")]
    [InlineData("#D13438", "#FFD13438")]
    [InlineData("#F80", "#FFFF8800")]
    [InlineData("  46FFFFFF ", "#46FFFFFF")]
    public void Hex_In_Through_Core_And_Out_As_AARRGGBB(string typed, string written)
    {
        Assert.True(ColorModel.TryParseHex(typed, out var c));
        Assert.Equal(written, ColorModel.FromColor(c).Hex);
    }

    [Theory]
    [InlineData("")]
    [InlineData("#12")]
    [InlineData("#GGGGGG")]
    [InlineData("red")]
    public void Bad_Hex_Is_Refused(string typed) => Assert.False(ColorModel.TryParseHex(typed, out _));

    [Fact]
    public void Black_And_Grey_Keep_The_Previous_Hue_And_Saturation()
    {
        var orange = ColorModel.FromColor(CoreColor.Parse("#FF8000"));
        var black = ColorModel.FromColor(CoreColor.Parse("#000000"), orange);
        Assert.Equal(orange.Hue, black.Hue);
        Assert.Equal(orange.Saturation, black.Saturation);
        Assert.Equal(orange.Hue, ColorModel.FromColor(CoreColor.Parse("#808080"), orange).Hue);
    }

    [Theory]
    [InlineData(0, "#FFFF0000")]
    [InlineData(120, "#FF00FF00")]
    [InlineData(240, "#FF0000FF")]
    [InlineData(360, "#FFFF0000")]
    public void HueColor_Is_The_Pure_Hue(double hue, string hex)
        => Assert.Equal(hex, new ColorModel(hue, 0.2, 0.3, 10).HueColor.ToHex());
}
