namespace DeskWall.Core.Bindings;

public abstract record PathSegment;
public sealed record NameSegment(string Name) : PathSegment { public override string ToString() => Name; }
public sealed record IndexSegment(int Index) : PathSegment { public override string ToString() => $"[{Index}]"; }
public sealed record KeySegment(string Key) : PathSegment { public override string ToString() => $"[{Key}]"; }

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
