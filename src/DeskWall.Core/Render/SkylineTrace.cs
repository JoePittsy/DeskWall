using System.Globalization;
using DeskWall.Core.Layout;

namespace DeskWall.Core.Render;

/// <summary>One-shot luminance walk, shared by the designer and CLI. No timers or resident state.</summary>
public static class SkylineTrace
{
    public static BarDef Trace(Surface image, Rect band, int step = 4)
    {
        var x0 = Math.Clamp(band.X, 0, image.Width - 1);
        var y0 = Math.Clamp(band.Y, 0, image.Height - 1);
        var x1 = Math.Clamp(band.Right, x0 + 1, image.Width);
        var y1 = Math.Clamp(band.Bottom, y0 + 1, image.Height);
        if (x1 - x0 < 3 || y1 - y0 < 5) throw new ArgumentException("Drag a wider, taller band around the skyline.");
        step = Math.Clamp(step, 1, 32);
        var width = x1 - x0; var height = y1 - y0;
        var bytes = new byte[checked(width * height * 4)];
        image.ReadRegion(new Rect(x0, y0, width, height), bytes);
        double L(int x, int y)
        {
            var i = (Math.Clamp(y, 0, height - 1) * width + Math.Clamp(x, 0, width - 1)) * 4;
            return (bytes[i] * 0.0722 + bytes[i + 1] * 0.7152 + bytes[i + 2] * 0.2126) / 255;
        }
        var columns = (width - 1 + step - 1) / step + 1;
        var previous = new double[height];
        var next = new double[height];
        var back = new int[checked(columns * height)];
        var reach = Math.Max(8, step * 4);
        for (var n = 0; n < columns; n++)
        {
            var x = Math.Min(width - 1, n * step);
            for (var y = 0; y < height; y++)
            {
                // Prefer bright sky above darker rock, penalising steep changes between samples.
                var contrast = (L(x, y - 4) + L(x, y - 2) - L(x, y + 2) - L(x, y + 4)) / 2;
                var best = 0.0; var at = y;
                if (n > 0)
                {
                    best = double.PositiveInfinity;
                    for (var q = Math.Max(0, y - reach); q <= Math.Min(height - 1, y + reach); q++)
                    {
                        var score = previous[q] + Math.Abs(q - y) * 0.008;
                        if (score < best) { best = score; at = q; }
                    }
                }
                next[y] = best - contrast;
                back[n * height + y] = at;
            }
            (previous, next) = (next, previous);
        }
        var row = Array.IndexOf(previous, previous.Min());
        var points = new (double X, double Y)[columns];
        for (var n = columns - 1; n >= 0; n--)
        {
            points[n] = (x0 + Math.Min(width - 1, n * step), y0 + row);
            row = back[n * height + row];
        }
        return FromPoints(points, false);
    }

    public static BarDef FromPoints(IReadOnlyList<(double X, double Y)> points, bool closed)
    {
        if (points.Count < (closed ? 3 : 2)) throw new ArgumentException("An outline needs two points; a filled shape needs three.");
        var x0 = (int)Math.Floor(points.Min(p => p.X)); var y0 = (int)Math.Floor(points.Min(p => p.Y));
        var x1 = (int)Math.Ceiling(points.Max(p => p.X)); var y1 = (int)Math.Ceiling(points.Max(p => p.Y));
        var shape = string.Join(" ", points.Select((p, n) => string.Create(CultureInfo.InvariantCulture,
            $"{(n == 0 ? "M" : "L")}{p.X - x0:0.###},{p.Y - y0:0.###}"))) + (closed ? " Z" : "");
        var pad = closed ? 0 : 1;
        return new BarDef
        {
            Id = "outline", Rect = new Rect(x0 - pad, y0 - pad, Math.Max(1, x1 - x0) + 2 * pad, Math.Max(1, y1 - y0) + 2 * pad),
            Fraction = PropertyValue.Literal(1), Threshold = PropertyValue.Literal(2),
            Track = PropertyValue.Literal("#00000000"), Fill = PropertyValue.Literal("#99FFFFFF"),
            Shape = PropertyValue.Literal(shape), Thickness = PropertyValue.Literal(closed ? 0 : 2),
        };
    }
}
