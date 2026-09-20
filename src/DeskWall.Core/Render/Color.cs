using System.Globalization;

namespace DeskWall.Core.Render;

public readonly record struct Color(byte A, byte R, byte G, byte B)
{
    public static Color White => new(255, 255, 255, 255);
    public static Color Transparent => new(0, 0, 0, 0);

    /// <summary>#RGB, #RRGGBB or #AARRGGBB.</summary>
    public static Color Parse(string s)
    {
        if (s.Length is not (4 or 7 or 9) || s[0] != '#') throw new FormatException($"bad colour '{s}'");
        if (s.Length == 4) s = $"#{s[1]}{s[1]}{s[2]}{s[2]}{s[3]}{s[3]}";
        if (!uint.TryParse(s.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
            throw new FormatException($"bad colour '{s}'");
        if (s.Length == 7) v |= 0xFF000000;
        return new((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    public string ToHex() => $"#{A:X2}{R:X2}{G:X2}{B:X2}";
}
