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
                (SliceSegment sl, ListValue l) => Slice(l, sl),
                _ => null,
            };
            if (cur is null) return null;
        }
        return cur;
    }

    private static ListValue Slice(ListValue l, SliceSegment s)
    {
        var count = l.Items.Count;
        var start = Math.Min(s.Start ?? 0, count);
        var end = Math.Min(s.End ?? count, count);
        if (start == 0 && end == count) return l;
        var items = new RecordValue[end - start];
        for (var i = 0; i < items.Length; i++) items[i] = l.Items[start + i];
        return new ListValue(items, l.KeyField);
    }

    public static string? ResolveText(Binding b, RecordValue root) => Resolve(b, root)?.ToText(b.Format);
}
