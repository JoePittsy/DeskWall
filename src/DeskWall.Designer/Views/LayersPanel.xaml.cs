using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DeskWall.Core.Layout;
using DeskWall.Designer.Model;

namespace DeskWall.Designer.Views;

/// <summary>Job: show z-order top-first so overlap is visible and fixable by dragging, and keep
/// selection in sync with the canvas. Leaves out: no thumbnails, no per-row menu button (that lives
/// on the canvas's own context menu), no folder chrome beyond a repeater's own template children.</summary>
public partial class LayersPanel : UserControl
{
    private DesignerModel? _model;
    private readonly Dictionary<string, TreeViewItem> _rows = new();
    private string? _selectedTemplateKey;
    private bool _syncingSelection;
    private string? _dragId;

    public event Action<ComponentDef, RepeaterDef>? TemplateChildActivated;

    public LayersPanel() => InitializeComponent();

    public void Attach(DesignerModel model)
    {
        if (_model is not null) { _model.Changed -= OnChanged; _model.SelectionChanged -= OnSelectionChanged; }
        _model = model;
        _model.Changed += OnChanged;
        _model.SelectionChanged += OnSelectionChanged;
        Rebuild();
    }

    private void OnChanged() => Rebuild();
    private void OnSelectionChanged() => SyncSelectionFromModel();

    private void Rebuild()
    {
        if (_model is null) return;
        Tree.Items.Clear();
        _rows.Clear();
        foreach (var c in _model.Layout.Components.OrderByDescending(c => c.Z))
        {
            var item = BuildRow(c, null);
            _rows[c.Id] = item;
            if (c is RepeaterDef r)
                foreach (var t in r.Template)
                {
                    var child = BuildRow(t, r);
                    _rows[TemplateKey(r.Id, t.Id)] = child;
                    item.Items.Add(child);
                }
            Tree.Items.Add(item);
        }
        SyncSelectionFromModel();
    }

    private TreeViewItem BuildRow(ComponentDef def, RepeaterDef? parent)
    {
        var label = parent is null ? def.Id : $"{def.Id} (template)";
        var header = new TextBlock { Text = $"{label}   {TypeName(def)}   [{def.Rect.X},{def.Rect.Y} {def.Rect.W}x{def.Rect.H}]" };
        var item = new TreeViewItem { Header = header, Tag = new ComponentLookup.Found(def, parent), IsExpanded = true };
        item.Selected += (_, e) => { if (ReferenceEquals(e.OriginalSource, item)) { OnRowSelected(item); e.Handled = true; } };
        if (parent is null)
        {
            item.AllowDrop = true;
            item.PreviewMouseLeftButtonDown += Row_PreviewMouseLeftButtonDown;
            item.PreviewMouseMove += Row_PreviewMouseMove;
            item.DragOver += (_, e) => { e.Effects = DragDropEffects.Move; e.Handled = true; };
            item.Drop += Row_Drop;
        }
        return item;
    }

    private static string TypeName(ComponentDef def) => def switch
    {
        TextDef => "text",
        ImageDef => "image",
        BarDef => "bar",
        ShortcutDef => "shortcut",
        RepeaterDef => "repeater",
        _ => "?",
    };

    private static string TemplateKey(string parentId, string childId) => parentId + "\u0000" + childId;

    private void OnRowSelected(TreeViewItem item)
    {
        if (_syncingSelection || _model is null) return;
        if (item.Tag is not ComponentLookup.Found found) return;

        if (found.Parent is null)
        {
            _selectedTemplateKey = null;
            _model.Select([found.Def.Id]);
        }
        else
        {
            _selectedTemplateKey = TemplateKey(found.Parent.Id, found.Def.Id);
            if (_model.Selection.Count > 0) _model.ClearSelection();
            else ApplyHighlight();
            TemplateChildActivated?.Invoke(found.Def, found.Parent);
        }
    }

    private void SyncSelectionFromModel()
    {
        if (_model is null) return;
        if (_model.Selection.Count > 0) _selectedTemplateKey = null;
        ApplyHighlight();
    }

    private void ApplyHighlight()
    {
        if (_model is null) return;
        _syncingSelection = true;
        try
        {
            var id = _model.Selection.Count == 1 ? _model.Selection[0] : null;
            foreach (var kv in _rows) kv.Value.IsSelected = kv.Key == id || kv.Key == _selectedTemplateKey;
        }
        finally { _syncingSelection = false; }
    }

    // ---- drag to reorder (top-level rows only; a repeater's own template order is fixed) --------

    private void Row_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is TreeViewItem { Tag: ComponentLookup.Found { Parent: null } found }) _dragId = found.Def.Id;
    }

    private void Row_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && _dragId is not null && sender is TreeViewItem item)
        {
            var id = _dragId;
            _dragId = null;
            DragDrop.DoDragDrop(item, id, DragDropEffects.Move);
        }
    }

    private void Row_Drop(object sender, DragEventArgs e)
    {
        if (_model is null) return;
        if (sender is not TreeViewItem { Tag: ComponentLookup.Found { Parent: null } target }) return;
        if (e.Data.GetData(typeof(string)) is not string draggedId || draggedId == target.Def.Id) return;

        var order = _model.Layout.Components.OrderByDescending(c => c.Z).Select(c => c.Id).ToList();
        order.Remove(draggedId);
        var idx = order.IndexOf(target.Def.Id);
        order.Insert(idx, draggedId);

        _model.Edit("Reorder layers", l =>
        {
            for (var i = 0; i < order.Count; i++)
            {
                var c = l.Components.First(x => x.Id == order[i]);
                c.Z = order.Count - i;
            }
        });
        e.Handled = true;
    }
}
