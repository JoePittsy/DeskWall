using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeskWall.Core;
using DeskWall.Core.Display;
using DeskWall.Core.Layout;
using DeskWall.Core.Widgets;
using DeskWall.Designer.Model.Widgets;

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
    /// other. The widget overlay is in here for the same reason: Make widget adds a copy AND a
    /// widget in one edit, and its undo has to take both away.</summary>
    private readonly record struct Snapshot(string Json, DisplaySignature Signature, string WidgetEditsJson);

    private readonly List<Snapshot> _undo = new();
    private readonly List<Snapshot> _redo = new();   // top is the end, like _undo
    private readonly List<string> _selection = new();
    private string _savedJson;
    private string _savedEditsJson = EmptyEdits;

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
    public bool Dirty => Layout.ToJson() != _savedJson || WidgetEditsJson() != _savedEditsJson;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public IReadOnlyList<string> Selection => _selection;

    public event Action? Changed;
    public event Action? SelectionChanged;

    /// <summary>Raised by <see cref="SetDepth"/> when the depth actually changes.</summary>
    public event Action? DepthChanged;

    // ---- widgets and depth (the Phase 2 seam) ------------------------------------------------

    private readonly Dictionary<string, WidgetTemplate> _widgetEdits = new(StringComparer.OrdinalIgnoreCase);
    private Expansion? _expanded;

    /// <summary>The widget overlay: widgets edited in this document and not yet applied, by key.
    /// <see cref="Save"/> writes each to <c>WidgetCatalog.UserDir\&lt;key&gt;.json</c> (the fork, plan
    /// D2). Changed only inside <see cref="Edit(string, Action{LayoutFile, IDictionary{string, WidgetTemplate}})"/>,
    /// so it is undone with the layout. Entries stay after a save: the overlay is the document's
    /// view of each widget it has touched.</summary>
    public IReadOnlyDictionary<string, WidgetTemplate> WidgetEdits => _widgetEdits;

    private const string EmptyEdits = "{}";

    /// <summary>The overlay as one canonical string (keys sorted, each value the widget file's
    /// JSON), for the undo snapshot and <see cref="Dirty"/>.</summary>
    private string WidgetEditsJson() => EditsJson(_widgetEdits);

    private static string EditsJson(IReadOnlyDictionary<string, WidgetTemplate> edits)
    {
        if (edits.Count == 0) return EmptyEdits;
        var o = new JsonObject();
        foreach (var (key, t) in edits.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
            o[key] = JsonNode.Parse(WidgetTemplateWriter.ToJson(t));
        return o.ToJsonString();
    }

    private static Dictionary<string, WidgetTemplate> ParseWidgetEdits(string json)
    {
        var result = new Dictionary<string, WidgetTemplate>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, node) in JsonNode.Parse(json)!.AsObject())
            result[key] = TemplateFromJson(key, node!.ToJsonString());
        return result;
    }

    /// <summary>The inverse of <see cref="WidgetTemplateWriter.ToJson"/>, for a template that was
    /// valid when it went in (so no second copy of <see cref="WidgetTemplate.Load"/>'s checks).</summary>
    private static WidgetTemplate TemplateFromJson(string key, string json)
    {
        var f = JsonSerializer.Deserialize(json, WidgetJsonContext.Default.WidgetTemplateFile)!;
        return new WidgetTemplate
        {
            Name = f.Name!,
            Key = key,
            Description = f.Description!,
            Width = f.Size![0],
            Height = f.Size[1],
            Anchor = f.Anchor ?? "top",
            Requires = f.Requires,
            Sources = f.Sources ?? [],
            Components = f.Components ?? [],
            Knobs = (f.Knobs ?? []).Select(k => new Knob(k.Id!, k.Label ?? k.Id!, Enum.Parse<KnobType>(k.Type!, ignoreCase: true),
                k.Default ?? "", k.Sets ?? [], k.Choices, k.Min, k.Max)).ToList(),
        };
    }

    private static WidgetTemplate? TryFind(Func<string, WidgetTemplate?> find, string key)
    {
        try { return find(key); }
#pragma warning disable CA1031 // a widget file that fails to load counts as absent here
        catch (Exception) { return null; }
#pragma warning restore CA1031
    }

    /// <summary>A widget lookup by key: the overlay first, then <see cref="WidgetCatalog.UserDir"/>,
    /// then <see cref="WidgetCatalog.ShippedDir"/>. The overlay is copied when this is called, so
    /// the returned function is safe to hand to another thread (the preview renderer). Like
    /// <see cref="WidgetCatalog.Finder"/>, it returns null for an unknown key and throws for a file
    /// that fails to load.</summary>
    public Func<string, WidgetTemplate?> Finder()
    {
        var overlay = new Dictionary<string, WidgetTemplate>(_widgetEdits, StringComparer.OrdinalIgnoreCase);
        var disk = WidgetCatalog.Finder(WidgetCatalog.ShippedDir, WidgetCatalog.UserDir);
        return key => overlay.TryGetValue(key, out var edited) ? edited : disk(key);
    }

    /// <summary><see cref="Layout"/> with its copies expanded through <see cref="Finder"/>: what
    /// the canvas draws and the daemon would paint. Cached until the next <see cref="Changed"/>.
    /// Read-only: for a layout with no copies <c>Expansion.Layout</c> is <see cref="Layout"/>
    /// itself, so mutate through <see cref="Edit"/>, never through this.</summary>
    public Expansion Expanded() => _expanded ??= WidgetExpander.Expand(Layout, Finder());

    /// <summary>The depth the canvas is editing at. Starts at <see cref="Depth.Layout"/>.</summary>
    public Depth Depth { get; private set; } = Depth.Layout;

    /// <summary>Go to <paramref name="depth"/>. Not an undo entry. Raises <see cref="DepthChanged"/>
    /// when it differs from the current depth.</summary>
    public void SetDepth(Depth depth)
    {
        ArgumentNullException.ThrowIfNull(depth);
        if (depth == Depth) return;
        Depth = depth;
        _projection = null;
        DepthChanged?.Invoke();
    }

    // ---- selection ------------------------------------------------------------------------

    public void Select(IEnumerable<string> ids)
    {
        _selection.Clear();
        foreach (var id in ids) if (Selectable(id) && !_selection.Contains(id)) _selection.Add(id);
        SelectionChanged?.Invoke();
    }

    /// <summary>A part at the current depth, or (at layout depth) a copy by its id: the canvas selects
    /// a copy as a whole, including one whose widget is missing and so has no parts at all.</summary>
    private bool Selectable(string id) => Find(id) is not null || IsCopy(id);

    private bool IsCopy(string id) => Depth.Kind == DepthKind.Layout && Copies.Find(Layout, id) is not null;

    private bool HasCopies => Layout.Copies is { Count: > 0 };

    /// <summary>The widget files on disk changed (the widget editor saved, or one was deleted): drop
    /// the cached expansion and projection so every copy of them redraws, and tell listeners. Not an
    /// undo entry: the document did not change, what it links to did.</summary>
    public void WidgetsChanged()
    {
        PruneSelection();
        AfterChange();
    }

    public void ClearSelection() { if (_selection.Count == 0) return; _selection.Clear(); SelectionChanged?.Invoke(); }

    // ---- editing -------------------------------------------------------------------------

    /// <summary>Snapshot, mutate the live LayoutFile, notify.</summary>
    public void Edit(string label, Action<LayoutFile> mutate) => Edit(label, (l, _) => mutate(l));

    /// <summary>The same, with the widget overlay (<see cref="WidgetEdits"/>) as well: one undo
    /// entry over the layout and the widgets together. Put a new <see cref="WidgetTemplate"/> in,
    /// never mutate one that is already there.</summary>
    public void Edit(string label, Action<LayoutFile, IDictionary<string, WidgetTemplate>> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        _undo.Add(Capture());
        if (_undo.Count > UndoCap) _undo.RemoveAt(0);
        _redo.Clear();
        mutate(Layout, _widgetEdits);
        LastEditLabel = label;
        AfterChange();
    }

    /// <summary>Every change ends here: the caches go, a depth whose copy or widget went climbs
    /// out, and then listeners hear about it.</summary>
    private void AfterChange()
    {
        _expanded = null;
        _projection = null;
        PruneDepth();
        Changed?.Invoke();
    }

    /// <summary>Climb out of a copy or widget that no longer exists (an undo of Make widget, a redo
    /// of Remove): a copy depth whose copy went goes to layout depth; a widget depth whose widget
    /// went does too, and one whose origin copy went stays on the widget with no copy.</summary>
    private void PruneDepth()
    {
        var copyGone = Depth.CopyId is { } id && Layout.Copies?.Exists(c => c.Id == id) != true;
        var next = Depth.Kind switch
        {
            DepthKind.Copy when copyGone => Depth.Layout,
            DepthKind.Widget when Depth.WidgetKey is not { } key || (!_widgetEdits.ContainsKey(key) && TryFind(Finder(), key) is null) => Depth.Layout,
            DepthKind.Widget when copyGone => Depth.Widget(Depth.WidgetKey!, null),
            _ => Depth,
        };
        SetDepth(next);
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
        _redo.Add(Capture());
        Restore(_undo[^1]);
        _undo.RemoveAt(_undo.Count - 1);
        PruneSelection();
        AfterChange();
    }

    public void Redo()
    {
        if (!CanRedo) return;
        _undo.Add(Capture());
        Restore(_redo[^1]);
        _redo.RemoveAt(_redo.Count - 1);
        PruneSelection();
        AfterChange();
    }

    private Snapshot Capture() => new(Layout.ToJson(), Signature, WidgetEditsJson());

    private void Restore(Snapshot snapshot)
    {
        Layout = LayoutFile.Parse(snapshot.Json);
        Signature = snapshot.Signature;
        RestoreWidgetEdits(snapshot.WidgetEditsJson);
        _projection = null;   // before PruneSelection reads it
    }

    private void RestoreWidgetEdits(string json)
    {
        _widgetEdits.Clear();
        foreach (var (key, t) in ParseWidgetEdits(json)) _widgetEdits[key] = t;
    }

    // ---- the lens: the parts at the current depth -----------------------------------------------

    private LayoutFile? _projection;   // Lens.Project at copy or widget depth, until the next change
    private LayoutFile? _working;      // the projection an EditAtDepth is mutating

    /// <summary>What <see cref="Find"/>, <see cref="Select"/> and the component edits below work
    /// on: the layout itself at layout depth, else the <see cref="Lens"/> projection.</summary>
    /// <para>At layout depth a layout with copies is projected too (<see cref="Lens.Project"/>): the
    /// loose components plus every copy's placed parts, so a copy is moved, scaled and removed by the
    /// same code as a component. Read it; edit through <see cref="EditAtDepth"/>.</para>
    public LayoutFile Parts => _working ?? (Depth.Kind == DepthKind.Layout && !HasCopies ? Layout : _projection ??= Lens.Project(this));

    /// <summary>A component (at layout depth) or a projected part (at copy and widget depth; read
    /// it, but edit it through <see cref="EditAtDepth"/>).</summary>
    public ComponentDef? Find(string id) => Parts.Components.FirstOrDefault(c => c.Id == id);

    /// <summary><see cref="Edit"/> at layout depth. At copy and widget depth, mutate the
    /// projection (<see cref="Lens.Project"/>) instead and <see cref="Lens.Commit"/> it: overrides on the copy,
    /// or the overlay template. One undo entry either way (none at those depths when nothing
    /// changed). Every component edit on this class goes through here, so the canvas's move, scale,
    /// z-order, add, duplicate and remove work unchanged at every depth.</summary>
    public void EditAtDepth(string label, Action<LayoutFile> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        if (Depth.Kind == DepthKind.Layout && !HasCopies) { Edit(label, mutate); return; }
        var before = Lens.Project(this);
        var after = Lens.Project(this);
        _working = after;
        try { mutate(after); }
        finally { _working = null; }
        Lens.Commit(this, before, after, label);
    }

    public void Move(IEnumerable<string> ids, int dx, int dy) => EditAtDepth("Move", l => Offset(l, ids, dx, dy));

    /// <summary>What <paramref name="ids"/> name in <paramref name="l"/> (the layout or projection an
    /// edit is mutating): components and parts by id, and at layout depth copies by id, each copy
    /// bringing every part it has. Each thing once, however many of its ids were passed.</summary>
    private (HashSet<ComponentDef> Parts, List<WidgetCopy> Copies) Resolve(LayoutFile l, IEnumerable<string> ids)
    {
        var parts = new HashSet<ComponentDef>(ReferenceEqualityComparer.Instance);
        var copies = new List<WidgetCopy>();
        foreach (var id in ids)
        {
            if (IsCopy(id) && l.Copies?.Find(c => c.Id == id) is { } copy)
            {
                if (copies.Contains(copy)) continue;
                copies.Add(copy);
                foreach (var c in l.Components) if (IsPartOf(c, copy)) parts.Add(c);
            }
            else if (Find(id) is { } c) parts.Add(c);
        }
        return (parts, copies);
    }

    private static bool IsPartOf(ComponentDef c, WidgetCopy copy)
        => c.Widget == copy.Id && c.Id.StartsWith(copy.Id + ".", StringComparison.Ordinal);

    /// <summary>A copy moves by its origin, with its parts, so its overrides stay as they are.</summary>
    private void Offset(LayoutFile l, IEnumerable<string> ids, int dx, int dy)
    {
        var (parts, copies) = Resolve(l, ids);
        foreach (var c in parts) c.Rect = c.Rect.Offset(dx, dy);
        foreach (var copy in copies) { copy.X += dx; copy.Y += dy; }
    }

    /// <summary>Put one component's rect somewhere exactly. Named SetRect and not Resize so the
    /// canvas's <see cref="Model.Resize"/> maths is reachable by name from in here.
    /// <para>A copy's id (layout depth) is a <see cref="Scale"/> of the whole copy from its bounds to
    /// <paramref name="newRect"/>, sizes left alone: an edge drag.</para></summary>
    public void SetRect(string id, Rect newRect)
    {
        if (IsCopy(id))
        {
            var bounds = Copies.Bounds(Copies.Find(Layout, id)!, Expanded(), Finder());
            Scale("Resize", [id], bounds, new Rect(newRect.X, newRect.Y, Math.Max(4, newRect.W), Math.Max(4, newRect.H)), scaleSizes: false);
            return;
        }
        EditAtDepth("Resize", l =>
        {
            if (Find(id) is { } c) c.Rect = new Rect(newRect.X, newRect.Y, Math.Max(4, newRect.W), Math.Max(4, newRect.H));
        });
    }

    public void SetZ(string id, int z) => EditAtDepth("Set Z", l => { if (Find(id) is { } c) c.Z = z; });

    public void BringToFront(string id) => EditAtDepth("Bring to front", l =>
    {
        if (Find(id) is not { } c) return;
        var max = l.Components.Where(o => o != c).Select(o => o.Z).DefaultIfEmpty(0).Max();
        c.Z = max + 1;
    });

    public void SendToBack(string id) => EditAtDepth("Send to back", l =>
    {
        if (Find(id) is not { } c) return;
        var min = l.Components.Where(o => o != c).Select(o => o.Z).DefaultIfEmpty(0).Min();
        c.Z = min - 1;
    });

    /// <summary>Adds the component, making its id unique with a -2, -3, ... suffix if needed.</summary>
    public void Add(ComponentDef def) => EditAtDepth("Add", l =>
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
        EditAtDepth("Duplicate", l =>
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
        EditAtDepth("Remove", l =>
        {
            l.Components.RemoveAll(c => set.Contains(c.Id));
            if (Depth.Kind == DepthKind.Layout) l.Copies?.RemoveAll(c => set.Contains(c.Id));   // its parts go with it
        });
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
        EditAtDepth(label, l =>
        {
            foreach (var (ids, dx, dy) in moves)
            {
                if (dx == 0 && dy == 0) continue;
                Offset(l, ids, dx, dy);
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
        EditAtDepth(label, l =>
        {
            var (parts, copies) = Resolve(l, ids);
            foreach (var c in parts) Resize.Apply(c, from, to, scaleSizes);
            // A copy with parts keeps its origin and gets rect (and size) overrides, as a stamped
            // instance's components were scaled in place. One with none (its widget is missing)
            // has nothing to scale: its box follows the gesture.
            foreach (var copy in copies.Where(copy => !l.Components.Any(c => IsPartOf(c, copy))))
            {
                var r = Resize.Map(new Rect(copy.X, copy.Y, 1, 1), from, to);
                copy.X = r.X; copy.Y = r.Y;
            }
        });
    }

    // ---- persistence ---------------------------------------------------------------------

    public string ToJson() => Layout.ToJson();

    /// <summary>Write every overlay widget to <c>WidgetCatalog.UserDir\&lt;key&gt;.json</c> (a shipped
    /// key is forked, plan D2: every copy of it on the machine follows), then the layout. Each is a
    /// temp file then a rename. Widgets first, so a layout is never on disk ahead of the widgets
    /// it was drawn with.</summary>
    public void Save()
    {
        if (Path is null) throw new InvalidOperationException("no path; use Save As");
        var disk = WidgetCatalog.Finder(WidgetCatalog.ShippedDir, WidgetCatalog.UserDir);
        var before = _widgetEdits.Keys.ToDictionary(k => k, k => TryFind(disk, k), StringComparer.OrdinalIgnoreCase);
        if (_widgetEdits.Count > 0) Directory.CreateDirectory(WidgetCatalog.UserDir);
        foreach (var (key, t) in _widgetEdits)
            WriteAtomically(System.IO.Path.Combine(WidgetCatalog.UserDir, key + ".json"), WidgetTemplateWriter.ToJson(t));
        Layout.Save(Path);
        _savedJson = Layout.ToJson();
        _savedEditsJson = WidgetEditsJson();
        KeepHistoryTrue(before);
        AfterChange();
    }

    /// <summary>An undo entry that does not have a widget in its overlay meant "as on disk". The
    /// save just changed the disk, so each such entry gets the widget as it was before the save:
    /// otherwise an undo past an Apply would show (and the next Apply keep) the new fork.</summary>
    private void KeepHistoryTrue(Dictionary<string, WidgetTemplate?> before)
    {
        foreach (var stack in new[] { _undo, _redo })
            for (var i = 0; i < stack.Count; i++)
            {
                var edits = ParseWidgetEdits(stack[i].WidgetEditsJson);
                var patched = false;
                foreach (var (key, was) in before)
                    if (was is not null && edits.TryAdd(key, was)) patched = true;
                if (patched) stack[i] = stack[i] with { WidgetEditsJson = EditsJson(edits) };
            }
    }

    private static void WriteAtomically(string path, string text)
    {
        var tmp = path + ".tmp";
        try
        {
            File.WriteAllText(tmp, text);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>Discard unsaved edits: the layout and the widget overlay as last saved. Keeps the
    /// undo history so the revert itself can be undone.</summary>
    public void RevertToSaved() => Edit("Revert", (_, edits) =>
    {
        Layout = LayoutFile.Parse(_savedJson);
        edits.Clear();
        foreach (var (key, t) in ParseWidgetEdits(_savedEditsJson)) edits[key] = t;
    });

    private void PruneSelection()
    {
        if (_selection.RemoveAll(id => !Selectable(id)) > 0) SelectionChanged?.Invoke();
    }
}
