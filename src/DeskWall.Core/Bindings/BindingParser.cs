namespace DeskWall.Core.Bindings;

public static class BindingParser
{
    public static Binding Parse(string text)
    {
        var bar = text.IndexOf('|');
        var pathText = (bar < 0 ? text : text[..bar]).Trim();
        string? format = null;
        if (bar >= 0)
        {
            format = text[(bar + 1)..].Trim();
            if (format.Length >= 2 && format[0] == '"' && format[^1] == '"') format = format[1..^1];
            if (format.Length == 0) throw new FormatException("empty format");
        }
        return new Binding(ParsePath(pathText), format);
    }

    private static List<PathSegment> ParsePath(string s)
    {
        if (s.Length == 0) throw new FormatException("empty path");
        var segs = new List<PathSegment>();
        var i = 0;
        segs.Add(new NameSegment(ReadName(s, ref i)));
        while (i < s.Length)
        {
            if (s[i] == '.')
            {
                i++;
                segs.Add(new NameSegment(ReadName(s, ref i)));
            }
            else if (s[i] == '[')
            {
                var close = s.IndexOf(']', i);
                if (close < 0) throw new FormatException("unclosed [");
                var body = s[(i + 1)..close];
                if (body.Length == 0) throw new FormatException("empty []");
                segs.Add(int.TryParse(body, out var n) && n >= 0 ? new IndexSegment(n) : new KeySegment(body));
                i = close + 1;
            }
            else throw new FormatException($"unexpected '{s[i]}' at {i}");
        }
        return segs;
    }

    private static string ReadName(string s, ref int i)
    {
        var start = i;
        if (i >= s.Length || !(char.IsAsciiLetter(s[i]) || s[i] == '_')) throw new FormatException($"expected name at {i}");
        while (i < s.Length && (char.IsAsciiLetterOrDigit(s[i]) || s[i] is '_' or '-')) i++;
        return s[start..i];
    }
}
