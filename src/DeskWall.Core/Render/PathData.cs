using System.Globalization;

namespace DeskWall.Core.Render;

/// <summary>SVG path data, the subset a traced outline or a pasted icon uses: M L H V C Z, upper
/// case absolute, lower case relative, repeated coordinates after a command. No arcs, no S/Q/T:
/// add them when a real path needs them. Coordinates are the path's own; <see cref="Surface"/>
/// stretches its bounds to the box it is drawn in.</summary>
public sealed class PathData
{
    public abstract record Segment;
    public sealed record Line(float X, float Y) : Segment;
    public sealed record Cubic(float X1, float Y1, float X2, float Y2, float X, float Y) : Segment;
    public sealed record Figure(float X, float Y, IReadOnlyList<Segment> Segments, bool Closed);

    public IReadOnlyList<Figure> Figures { get; }
    public (float MinX, float MinY, float MaxX, float MaxY) Bounds { get; }

    private PathData(List<Figure> figures)
    {
        Figures = figures;
        float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
        void Add(float x, float y) { x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y); }
        foreach (var f in figures)
        {
            Add(f.X, f.Y);
            foreach (var s in f.Segments)
                if (s is Line l) Add(l.X, l.Y);
                else if (s is Cubic c) { Add(c.X1, c.Y1); Add(c.X2, c.Y2); Add(c.X, c.Y); }
        }
        Bounds = (x0, y0, x1, y1);
    }

    /// <summary>Throws <see cref="FormatException"/> on anything it cannot read, or on a path
    /// with nothing to draw; the caller falls back to a plain box.</summary>
    public static PathData Parse(string d)
    {
        var tokens = Tokens(d);
        var figures = new List<Figure>();
        List<Segment>? segs = null;
        float sx = 0, sy = 0, x = 0, y = 0;
        char cmd = '\0';
        var i = 0;
        float Num()
        {
            if (i < tokens.Count && tokens[i].Num is { } n) { i++; return n; }
            throw new FormatException($"number expected in path at token {i}");
        }
        bool MoreNumbers() => i < tokens.Count && tokens[i].Num is not null;
        void Close(bool closed) { if (segs is not null) figures.Add(new Figure(sx, sy, segs, closed)); segs = null; }

        while (i < tokens.Count)
        {
            if (tokens[i].Cmd is { } c) { cmd = c; i++; }
            else if (cmd == '\0') throw new FormatException("path must start with a command");
            var rel = char.IsLower(cmd);
            switch (char.ToUpperInvariant(cmd))
            {
                case 'M':
                    Close(false);
                    x = Num() + (rel ? x : 0); y = Num() + (rel ? y : 0);
                    sx = x; sy = y; segs = [];
                    cmd = rel ? 'l' : 'L';   // further pairs after an M are lines
                    break;
                case 'L': x = Num() + (rel ? x : 0); y = Num() + (rel ? y : 0); Seg(new Line(x, y)); break;
                case 'H': x = Num() + (rel ? x : 0); Seg(new Line(x, y)); break;
                case 'V': y = Num() + (rel ? y : 0); Seg(new Line(x, y)); break;
                case 'C':
                    var ox = rel ? x : 0; var oy = rel ? y : 0;
                    var c1x = Num() + ox; var c1y = Num() + oy; var c2x = Num() + ox; var c2y = Num() + oy;
                    x = Num() + ox; y = Num() + oy;
                    Seg(new Cubic(c1x, c1y, c2x, c2y, x, y));
                    break;
                case 'Z':
                    Close(true); x = sx; y = sy;
                    if (MoreNumbers()) throw new FormatException("numbers after Z");
                    cmd = '\0';
                    break;
                default: throw new FormatException($"unsupported path command '{cmd}'");
            }
        }
        Close(false);
        if (figures.Count == 0 || figures.All(f => f.Segments.Count == 0)) throw new FormatException("path draws nothing");
        return new PathData(figures);

        void Seg(Segment s) => (segs ?? throw new FormatException("path must start with M")).Add(s);
    }

    private static List<(char? Cmd, float? Num)> Tokens(string d)
    {
        var list = new List<(char?, float?)>();
        var i = 0;
        while (i < d.Length)
        {
            var ch = d[i];
            if (char.IsWhiteSpace(ch) || ch == ',') { i++; continue; }
            if (char.IsLetter(ch) && ch is not ('e' or 'E')) { list.Add((ch, null)); i++; continue; }
            var start = i;
            if (d[i] is '-' or '+') i++;
            var dot = false;
            while (i < d.Length && (char.IsDigit(d[i]) || (d[i] == '.' && !dot)))
            {
                if (d[i] == '.') dot = true;
                i++;
            }
            if (i < d.Length && d[i] is 'e' or 'E') { i++; if (i < d.Length && d[i] is '-' or '+') i++; while (i < d.Length && char.IsDigit(d[i])) i++; }
            if (i == start || !float.TryParse(d.AsSpan(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
                throw new FormatException($"bad path data at {start}");
            list.Add((null, n));
        }
        return list;
    }
}
