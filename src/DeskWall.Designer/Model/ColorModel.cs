using CoreColor = DeskWall.Core.Render.Color;

namespace DeskWall.Designer.Model;

/// <summary>The colour picker's state: hue in degrees [0, 360), saturation and value in [0, 1], and
/// alpha as the byte the file stores. Held as HSV rather than recomputed from the colour so that
/// dragging brightness to black, or saturation to grey, and back does not lose the hue.
/// Hex goes in through Core's <see cref="CoreColor.Parse"/> (#RGB, #RRGGBB, #AARRGGBB) and out as
/// <see cref="CoreColor.ToHex"/>'s #AARRGGBB, the form every layout file uses.</summary>
public readonly record struct ColorModel(double Hue, double Saturation, double Value, byte Alpha)
{
    /// <summary><paramref name="previous"/> supplies the hue of a grey and the saturation of black,
    /// which the colour alone does not carry.</summary>
    public static ColorModel FromColor(CoreColor c, ColorModel? previous = null)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), d = max - Math.Min(r, Math.Min(g, b));
        var hue = d == 0 ? previous?.Hue ?? 0
            : max == r ? 60 * ((g - b) / d)
            : max == g ? 60 * ((b - r) / d + 2)
            : 60 * ((r - g) / d + 4);
        if (hue < 0) hue += 360;
        var saturation = max == 0 ? previous?.Saturation ?? 0 : d / max;
        return new(hue, saturation, max, c.A);
    }

    public CoreColor ToColor()
    {
        var h = (Hue % 360 + 360) % 360 / 60;
        double v = Math.Clamp(Value, 0, 1), c = v * Math.Clamp(Saturation, 0, 1), x = c * (1 - Math.Abs(h % 2 - 1)), m = v - c;
        var (r, g, b) = (int)h switch
        {
            0 => (c, x, 0.0),
            1 => (x, c, 0.0),
            2 => (0.0, c, x),
            3 => (0.0, x, c),
            4 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return new(Alpha, Byte(r + m), Byte(g + m), Byte(b + m));
    }

    /// <summary>The fully saturated, fully bright colour at this hue: the backdrop of the
    /// saturation/value area.</summary>
    public CoreColor HueColor => (this with { Saturation = 1, Value = 1, Alpha = 255 }).ToColor();

    public string Hex => ToColor().ToHex();

    /// <summary>What was typed, with or without the leading '#', through Core's parser.</summary>
    public static bool TryParseHex(string? text, out CoreColor color)
    {
        var s = (text ?? "").Trim();
        if (s.Length > 0 && s[0] != '#') s = "#" + s;
        try { color = CoreColor.Parse(s); return true; }
        catch (FormatException) { color = default; return false; }
    }

    private static byte Byte(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
}
