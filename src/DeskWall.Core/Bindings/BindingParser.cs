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
                segs.Add(int.TryParse(body, out var n) && n >= 0 ? new IndexSegment(n) : (PathSegment?)TrySlice(body) ?? new KeySegment(body));
                i = close + 1;
            }
            else throw new FormatException($"unexpected '{s[i]}' at {i}");
        }
        return segs;
    }

    /// <summary><c>a..b</c> with each side empty or a plain non-negative integer is a slice; any
    /// other text with <c>..</c> in it (<c>..{count}</c>, a knob placeholder) stays a key, exactly as
    /// <c>[-1]</c> does. <c>..</c> rather than Python's <c>:</c> because a key may be a time.</summary>
    private static SliceSegment? TrySlice(string body)
    {
        var dots = body.IndexOf("..", StringComparison.Ordinal);
        if (dots < 0) return null;
        if (!TryBound(body[..dots], out var start) || !TryBound(body[(dots + 2)..], out var end)) return null;
        if (start is null && end is null) throw new FormatException("[..] needs a start or an end");
        if (start > end) throw new FormatException($"slice [{body}] ends before it starts");
        return new SliceSegment(start, end);
    }

    private static bool TryBound(string s, out int? bound)
    {
        bound = null;
        if (s.Length == 0) return true;
        // NumberStyles.None: digits only, so "-1" and " 2" are not bounds.
        if (!int.TryParse(s, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n)) return false;
        bound = n;
        return true;
    }

    private static string ReadName(string s, ref int i)
    {
        var start = i;
        if (i >= s.Length || !IsNameStart(s[i])) throw new FormatException($"expected name at {i}");
        while (i < s.Length && IsNameChar(s[i])) i++;
        return s[start..i];
    }

    private static bool IsNameStart(char c) => char.IsAsciiLetter(c) || c == '_';

    private static bool IsNameChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '_' or '-';

    /// <summary>Whether the whole string is one path segment, so it can be the first segment of a
    /// binding. An event's source name has to pass this or nothing could ever bind to it, and the
    /// rule lives here rather than being copied beside the character class it has to agree with.</summary>
    public static bool IsName(string? s)
    {
        if (string.IsNullOrEmpty(s) || !IsNameStart(s[0])) return false;
        for (var i = 1; i < s.Length; i++) if (!IsNameChar(s[i])) return false;
        return true;
    }
}
