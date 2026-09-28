using DeskWall.Core;
using DeskWall.Core.Display;
using DeskWall.Core.Layout;

namespace DeskWall.Designer.Model;

/// <summary>The open document. All mutation goes through <see cref="Edit"/> so undo and change
/// notification are uniform. Undo is whole-document JSON snapshots, capped at 100. Not
/// thread-affine; views marshal to the UI thread.</summary>
public sealed class DesignerModel
{
    private const int UndoCap = 100;

    /// <summary>What an undo entry holds. The canvas size is in here and not in a parallel stack
    /// because the widget editor's <c>Fit to parts</c> moves the components AND changes the size in
    /// one <see cref="Edit"/>: two stacks would let a single Ctrl+Z put back one and not the
    /// other.</summary>
    private readonly record struct Snapshot(string Json, DisplaySignature Signature);

    private readonly List<Snapshot> _undo = new();
    private readonly Stack<Snapshot> _redo = new();
    private readonly List<string> _selection = new();
    private string _savedJson;

    public DesignerModel(LayoutFile layout, DisplaySignature signature, string? path)
    {
        Layout = layout;
        Signature = signature;
        Path = path;
        _savedJson = layout.ToJson();
    }

    public LayoutFile Layout { get; private set; }

    /// <summary>The canvas this document is authored on. A layout's is the display's and never
    /// changes; a widget document's is the widget's own size, which <see cref="ResizeCanvas"/> edits.</summary>
    public DisplaySignature Signature { get; private set; }
    public string? Path { get; set; }
    public bool Dirty => Layout.ToJson() != _savedJson;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public IReadOnlyList<string> Selection => _selection;

    public event Action? Changed;
    public event Action? SelectionChanged;

    // ---- selection ------------------------------------------------------------------------

    public void Select(IEnumerable<string> ids)
    {
        _selection.Clear();
        foreach (var id in ids) if (Find(id) is not null && !_selection.Contains(id)) _selection.Add(id);
        SelectionChanged?.Invoke();
    }

    public void ClearSelection() { if (_selection.Count == 0) return; _selection.Clear(); SelectionChanged?.Invoke(); }

    // ---- editing -------------------------------------------------------------------------

    /// <summary>Snapshot, mutate the live LayoutFile, notify.</summary>
    public void Edit(string label, Action<LayoutFile> mutate)
    {
        _undo.Add(Capture());
        if (_undo.Count > UndoCap) _undo.RemoveAt(0);
        _redo.Clear();
        mutate(Layout);
        LastEditLabel = label;
        Changed?.Invoke();
    }

    public string? LastEditLabel { get; private set; }

    /// <summary>Change the canvas the document is authored on, undoably. Only the widget editor
    /// calls this: a layout's canvas is the display's, and the store scales between displays.
    /// <para>Not named Resize, for the same reason <see cref="SetRect"/> is not: the canvas's own
    /// <see cref="Model.Resize"/> maths has to stay reachable by name from in here.</para></summary>
    public void ResizeCanvas(int width, int height)
    {
        var w = Math.Max(1, width);
        var h = Math.Max(1, height);
        if (Signature.Width == w && Signature.Height == h) return;
        Edit("Resize canvas", _ => SetSignatureSize(w, h));
    }

    /// <summary>The size change on its own, with no undo entry of its own, for a caller already
    /// inside an <see cref="Edit"/> that moves the components at the same time (Fit to parts).</summary>
    internal void SetSignatureSize(int width, int height)
        => Signature = Signature with { Width = Math.Max(1, width), Height = Math.Max(1, height) };

    public void Undo()
    {
        if (!CanUndo) return;
        _redo.Push(Capture());
        Restore(_undo[^1]);
        _undo.RemoveAt(_undo.Count - 1);
        PruneSelection();
        Changed?.Invoke();
    }

    public void Redo()
    {
        if (!CanRedo) return;
        _undo.Add(Capture());
        Restore(_redo.Pop());
        PruneSelection();
        Changed?.Invoke();
    }

    private Snapshot Capture() => new(Layout.ToJson(), Signature);

    private void Restore(Snapshot snapshot)
    {
        Layout = LayoutFile.Parse(snapshot.Json);
        Signature = snapshot.Signature;
    }

    public ComponentDef? Find(string id) => Layout.Components.FirstOrDefault(c => c.Id == id);

    public void Move(IEnumerable<string> ids, int dx, int dy) => Edit("Move", l =>
    {
        foreach (var id in ids) if (Find(id) is { } c) c.Rect = c.Rect.Offset(dx, dy);
    });

    /// <summary>Put one component's rect somewhere exactly. Named SetRect and not Resize so the
    /// canvas's <see cref="Model.Resize"/> maths is reachable by name from in here.</summary>
    public void SetRect(string id, Rect newRect) => Edit("Resize", l =>
    {
        if (Find(id) is { } c) c.Rect = new Rect(newRect.X, newRect.Y, Math.Max(4, newRect.W), Math.Max(4, newRect.H));
    });

    public void SetZ(string id, int z) => Edit("Set Z", l => { if (Find(id) is { } c) c.Z = z; });

    public void BringToFront(string id) => Edit("Bring to front", l =>
    {
        if (Find(id) is not { } c) return;
        var max = l.Components.Where(o => o != c).Select(o => o.Z).DefaultIfEmpty(0).Max();
        c.Z = max + 1;
    });

    public void SendToBack(string id) => Edit("Send to back", l =>
    {
        if (Find(id) is not { } c) return;
        var min = l.Components.Where(o => o != c).Select(o => o.Z).DefaultIfEmpty(0).Min();
        c.Z = min - 1;
    });

    /// <summary>Adds the component, making its id unique with a -2, -3, ... suffix if needed.</summary>
    public void Add(ComponentDef def) => Edit("Add", l =>
    {
        var baseId = def.Id;
        var id = baseId; var n = 2;
        while (l.Components.Any(c => c.Id == id)) id = $"{baseId}-{n++}";
        def.Id = id;
        l.Components.Add(def);
    });

    /// <summary>Deep-copy the components (a JSON round trip through the layout's own serializer, so
    /// a repeater brings its template), offset them and add them as ONE undo entry. Returns the new
    /// ids, so the caller can select the copies.
    /// <para>Added for the canvas's Ctrl+D: <see cref="Add"/> alone cannot clone, and would be one
    /// undo entry per copy.</para></summary>
    public IReadOnlyList<string> Duplicate(IEnumerable<string> ids, int dx, int dy)
    {
        var originals = ids.Select(Find).OfType<ComponentDef>().ToList();
        if (originals.Count == 0) return Array.Empty<string>();
        var clones = Clone(originals);
        var made = new List<string>(clones.Count);
        Edit("Duplicate", l =>
        {
            var taken = AllIds(l).ToHashSet(StringComparer.Ordinal);
            foreach (var c in clones)
            {
                var id = Uniquify(taken, c.Id);
                c.Id = id;
                // A repeater's template children carry their own ids through the clone, and a copy
                // whose children still answer to "letter" makes the copy's template unreachable: the
                // panels address a child as (repeater id, child id) and the layers tree shows both.
                if (c is RepeaterDef r)
                    foreach (var t in r.Template) t.Id = Uniquify(taken, t.Id);
                c.Rect = c.Rect.Offset(dx, dy);
                l.Components.Add(c);
                made.Add(id);
            }
        });
        return made;
    }

    /// <summary>Every id in the layout, template children included.</summary>
    private static IEnumerable<string> AllIds(LayoutFile l)
    {
        foreach (var c in l.Components)
        {
            yield return c.Id;
            if (c is RepeaterDef r)
                foreach (var t in r.Template) yield return t.Id;
        }
    }

    /// <summary>baseId, or baseId-2, -3, ... until it is not in <paramref name="taken"/>. The answer
    /// joins the set, so a run of clones cannot collide with each other either.</summary>
    private static string Uniquify(HashSet<string> taken, string baseId)
    {
        var id = baseId; var n = 2;
        while (!taken.Add(id)) id = $"{baseId}-{n++}";
        return id;
    }

    private static List<ComponentDef> Clone(List<ComponentDef> defs)
        => LayoutFile.Parse(new LayoutFile { BaseImage = "", Components = defs }.ToJson()).Components;

    public void Remove(IEnumerable<string> ids)
    {
        var set = ids.ToHashSet();
        Edit("Remove", l => l.Components.RemoveAll(c => set.Contains(c.Id)));
        if (_selection.RemoveAll(set.Contains) > 0) SelectionChanged?.Invoke();
    }

    /// <summary>Move several groups of components, each group by its own offset, as ONE undo
    /// entry.
    /// <para>What a drag, a group drag, an align, a distribute and an arrow-key nudge all come
    /// down to: the canvas works out an offset per target (<see cref="Placement"/>), and this
    /// writes them. One entry, not one per component - undoing an align that moved six widgets six
    /// times is not undo, it is a chore. A set of offsets that all come to nothing is not an edit
    /// at all, so a click that happened to wobble does not fill the history.</para></summary>
    public void MoveGroups(string label, IReadOnlyList<(IReadOnlyList<string> Ids, int Dx, int Dy)> moves)
    {
        ArgumentNullException.ThrowIfNull(moves);
        if (moves.All(m => m.Dx == 0 && m.Dy == 0)) return;
        Edit(label, _ =>
        {
            foreach (var (ids, dx, dy) in moves)
            {
                if (dx == 0 && dy == 0) continue;
                foreach (var id in ids) if (Find(id) is { } c) c.Rect = c.Rect.Offset(dx, dy);
            }
        });
    }

    /// <summary>Scale everything in <paramref name="ids"/> from one bounding box to another, as
    /// ONE undo entry: the whole resize gesture, however many components and however many mouse
    /// moves it took. <paramref name="scaleSizes"/> carries the pixel sizes inside each component
    /// (font size, dial stroke) along with the box, which is what a corner drag means and an edge
    /// drag does not (<see cref="Resize.Apply"/>).</summary>
    public void Scale(string label, IReadOnlyList<string> ids, Rect from, Rect to, bool scaleSizes)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0 || from == to || from.W <= 0 || from.H <= 0) return;
        Edit(label, _ =>
        {
            foreach (var id in ids) if (Find(id) is { } c) Resize.Apply(c, from, to, scaleSizes);
        });
    }

    // ---- persistence ---------------------------------------------------------------------

    public string ToJson() => Layout.ToJson();

    public void Save()
    {
        if (Path is null) throw new InvalidOperationException("no path; use Save As");
        Layout.Save(Path);
        _savedJson = Layout.ToJson();
        Changed?.Invoke();
    }

    /// <summary>Discard unsaved edits: reload from the saved JSON. Keeps the undo history so the
    /// revert itself can be undone.</summary>
    public void RevertToSaved() => Edit("Revert", l =>
    {
        var saved = LayoutFile.Parse(_savedJson);
        l.Components = saved.Components;
        l.Sources = saved.Sources;
        l.BaseImage = saved.BaseImage; l.BaseFit = saved.BaseFit; l.Encode = saved.Encode; l.JpegQuality = saved.JpegQuality;
    });

    private void PruneSelection()
    {
        if (_selection.RemoveAll(id => Find(id) is null) > 0) SelectionChanged?.Invoke();
    }
}
