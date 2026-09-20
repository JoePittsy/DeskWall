using System.Globalization;

namespace DeskWall.Core.Display;

public sealed record DisplaySignature(string DevicePath, int Width, int Height, int ScalePercent)
{
    public string Key => $"{DevicePath} @ {Width}x{Height} @ {ScalePercent}%";
    public double Aspect => (double)Width / Height;

    public static DisplaySignature Parse(string key)
    {
        var parts = key.Split(" @ ");
        if (parts.Length != 3) throw new FormatException($"bad signature '{key}'");
        var wh = parts[1].Split('x');
        return new(parts[0], int.Parse(wh[0], CultureInfo.InvariantCulture), int.Parse(wh[1], CultureInfo.InvariantCulture),
            int.Parse(parts[2].TrimEnd('%'), CultureInfo.InvariantCulture));
    }

    /// <summary>Higher is closer. 3 same device, +1 same aspect within 1 percent, +1 same resolution.</summary>
    public int Similarity(DisplaySignature o)
    {
        var s = 0;
        if (string.Equals(DevicePath, o.DevicePath, StringComparison.OrdinalIgnoreCase)) s += 3;
        if (Math.Abs(Aspect - o.Aspect) / Aspect <= 0.01) s += 1;
        if (Width == o.Width && Height == o.Height) s += 1;
        return s;
    }
}

public sealed record MonitorInfo(DisplaySignature Signature, Rect Bounds, bool IsPrimary, string WallpaperMonitorId);
