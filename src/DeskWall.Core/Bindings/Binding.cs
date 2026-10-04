namespace DeskWall.Core.Bindings;

public abstract record PathSegment;
public sealed record NameSegment(string Name) : PathSegment { public override string ToString() => Name; }
public sealed record IndexSegment(int Index) : PathSegment { public override string ToString() => $"[{Index}]"; }
public sealed record KeySegment(string Key) : PathSegment { public override string ToString() => $"[{Key}]"; }

/// <summary><c>[..4]</c>, <c>[2..]</c>, <c>[1..3]</c>: the items from <see cref="Start"/>
/// (inclusive, default 0) to <see cref="End"/> (exclusive, default the end), as C# ranges count.
/// Applied to a list it yields a shorter list with the same key field; bounds past the end
/// clamp rather than fail, so <c>[..8]</c> of three items is those three.</summary>
public sealed record SliceSegment(int? Start, int? End) : PathSegment
{
    public override string ToString() => $"[{Start}..{End}]";
}

/// <summary>A path into the value tree plus an optional format. See spec 4.2.</summary>
public sealed record Binding(IReadOnlyList<PathSegment> Path, string? Format)
{
    public static Binding Parse(string text) => BindingParser.Parse(text);

    public override string ToString()
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < Path.Count; i++)
        {
            if (i > 0 && Path[i] is NameSegment) sb.Append('.');
            sb.Append(Path[i]);
        }
        if (Format is not null) sb.Append(" | \"").Append(Format).Append('"');
        return sb.ToString();
    }
}
