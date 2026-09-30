using System.Globalization;

namespace DeskWall.Designer.Model;

public enum RuleMode { None, Step, Blend }

/// <param name="At">Step: the value is below this. Blend: the value is at this.</param>
public sealed record Rule(double At, string To);

/// <summary>The bind menu's rules as rows, and back: a step is the map format with "&lt;" keys
/// ("?&lt;0.3=a,&lt;0.7=b,*=c"), a blend the "~" format ("~0=12,1=48"); docs/layout-format.md.
/// Only what the editor writes is read back; any other format is not rules (null).</summary>
public static class Rules
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static (RuleMode Mode, List<Rule> Rows, string Otherwise)? Parse(string? format)
    {
        if (format is null || format.Length < 2 || !format.Contains('=')) return null;
        var blend = format[0] == '~';
        if (!blend && format[0] != '?') return null;
        var rows = new List<Rule>();
        var otherwise = "";
        foreach (var pair in format[1..].Split(','))
        {
            var eq = pair.IndexOf('=');
            if (eq < 0) return null;
            var key = pair[..eq].Trim();
            var to = pair[(eq + 1)..];
            if (!blend && key == "*") { otherwise = to; continue; }
            if (!blend && (key.Length < 2 || key[0] != '<' || key[1] == '=')) return null;
            if (!double.TryParse(blend ? key : key[1..], NumberStyles.Float, Inv, out var at)) return null;
            rows.Add(new Rule(at, to));
        }
        return (blend ? RuleMode.Blend : RuleMode.Step, rows.OrderBy(r => r.At).ToList(), otherwise);
    }

    /// <summary>Null for <see cref="RuleMode.None"/>. Rows are sorted, so bands never overlap.</summary>
    public static string? Write(RuleMode mode, IEnumerable<Rule> rows, string otherwise)
    {
        if (mode == RuleMode.None) return null;
        var sorted = rows.OrderBy(r => r.At).Select(r => (mode == RuleMode.Step ? "<" : "") + r.At.ToString("R", Inv) + "=" + r.To);
        return (mode == RuleMode.Step ? "?" : "~") + string.Join(",", mode == RuleMode.Step ? sorted.Append("*=" + otherwise) : sorted);
    }
}
