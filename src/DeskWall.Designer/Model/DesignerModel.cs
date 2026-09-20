using DeskWall.Core;
using DeskWall.Core.Display;
using DeskWall.Core.Layout;

namespace DeskWall.Designer.Model;

public enum AlignEdge { Left, Right, Top, Bottom, CenterX, CenterY }

/// <summary>The open document. All mutation goes through <see cref="Edit"/> so undo and change
/// notification are uniform. Undo is whole-document JSON snapshots, capped at 100. Not
/// thread-affine; views marshal to the UI thread.</summary>
public sealed class DesignerModel
{
    private const int UndoCap = 100;
    private readonly List<string> _undo = new();
    private readonly Stack<string> _redo = new();
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
    public DisplaySignature Signature { get; }
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
        _undo.Add(Layout.ToJson());
        if (_undo.Count > UndoCap) _undo.RemoveAt(0);
        _redo.Clear();
        mutate(Layout);
        LastEditLabel = label;
        Changed?.Invoke();
    }

    public string? LastEditLabel { get; private set; }

    public void Undo()
    {
        if (!CanUndo) return;
        _redo.Push(Layout.ToJson());
        Layout = LayoutFile.Parse(_undo[^1]);
        _undo.RemoveAt(_undo.Count - 1);
        PruneSelection();
        Changed?.Invoke();
    }

    public void Redo()
    {
        if (!CanRedo) return;
        _undo.Add(Layout.ToJson());
        Layout = LayoutFile.Parse(_redo.Pop());
        PruneSelection();
        Changed?.Invoke();
    }

    public ComponentDef? Find(string id) => Layout.Components.FirstOrDefault(c => c.Id == id);

    public void Move(IEnumerable<string> ids, int dx, int dy) => Edit("Move", l =>
    {
        foreach (var id in ids) if (Find(id) is { } c) c.Rect = c.Rect.Offset(dx, dy);
    });

    public void Resize(string id, Rect newRect) => Edit("Resize", l =>
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
            foreach (var c in clones)
            {
                var baseId = c.Id; var id = baseId; var n = 2;
                while (l.Components.Any(o => o.Id == id)) id = $"{baseId}-{n++}";
                c.Id = id;
                c.Rect = c.Rect.Offset(dx, dy);
                l.Components.Add(c);
                made.Add(id);
            }
        });
        return made;
    }

    private static List<ComponentDef> Clone(List<ComponentDef> defs)
        => LayoutFile.Parse(new LayoutFile { BaseImage = "", Components = defs }.ToJson()).Components;

    public void Remove(IEnumerable<string> ids)
    {
        var set = ids.ToHashSet();
        Edit("Remove", l => l.Components.RemoveAll(c => set.Contains(c.Id)));
        if (_selection.RemoveAll(set.Contains) > 0) SelectionChanged?.Invoke();
    }

    public void Align(IEnumerable<string> ids, AlignEdge edge)
    {
        var comps = ids.Select(Find).Where(c => c is not null).Cast<ComponentDef>().ToList();
        if (comps.Count < 2) return;
        var anchor = comps[0].Rect;
        Edit("Align", l =>
        {
            foreach (var c in comps.Skip(1))
            {
                var r = c.Rect;
                c.Rect = edge switch
                {
                    AlignEdge.Left => r with { X = anchor.X },
                    AlignEdge.Right => r with { X = anchor.Right - r.W },
                    AlignEdge.Top => r with { Y = anchor.Y },
                    AlignEdge.Bottom => r with { Y = anchor.Bottom - r.H },
                    AlignEdge.CenterX => r with { X = anchor.X + (anchor.W - r.W) / 2 },
                    AlignEdge.CenterY => r with { Y = anchor.Y + (anchor.H - r.H) / 2 },
                    _ => r,
                };
            }
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
