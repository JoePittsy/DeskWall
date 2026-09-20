using DeskWall.Core.Values;

namespace DeskWall.Core.Bindings;

public static class BindingResolver
{
    /// <summary>Walk the path. Null if any step is missing or of the wrong shape.</summary>
    public static Value? Resolve(Binding b, RecordValue root)
    {
        Value? cur = root;
        foreach (var seg in b.Path)
        {
            cur = (seg, cur) switch
            {
                (NameSegment n, RecordValue r) => r.Get(n.Name),
                (IndexSegment ix, ListValue l) => ix.Index < l.Items.Count ? l.Items[ix.Index] : null,
                (KeySegment k, ListValue l) => l.ByKey(k.Key),
                _ => null,
            };
            if (cur is null) return null;
        }
        return cur;
    }

    public static string? ResolveText(Binding b, RecordValue root) => Resolve(b, root)?.ToText(b.Format);
}
