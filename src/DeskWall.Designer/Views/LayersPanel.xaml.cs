using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using DeskWall.Core.Layout;
using DeskWall.Designer.Model;

namespace DeskWall.Designer.Views;

/// <summary>Job: say what is on the canvas front to back, which copies differ from their widget and
/// which are broken, and keep selection in step with the canvas. Rows come from
/// <see cref="LayerTree"/> (front first, as in Figma). Leaves out: drag to reorder (Ctrl+[ and
/// Ctrl+] are the z-order verbs, Task 3.5), rename, visibility toggles and thumbnails.
/// <para>Host: <see cref="Attach"/> a model, and route <see cref="TemplateChildActivated"/> to
/// <c>PropertiesPanel.ShowTemplateChild</c>. The panel selects through <see cref="DesignerModel.Select"/>
/// only, and climbs to layout depth when the row it was asked for is not selectable at the current one.</para></summary>
public partial class LayersPanel : UserControl
{
    private sealed record Entry(TreeViewItem Item, LayerRow Row, Border Bar);

    private DesignerModel? _model;
    private readonly Dictionary<string, Entry> _rows = new();
    private readonly HashSet<string> _expanded = new();
    private bool _syncing;

    /// <summary>A repeater's template child row was chosen. The model has already selected its
    /// repeater; the arguments are resolved against <see cref="DesignerModel.Parts"/>.</summary>
    public event Action<ComponentDef, RepeaterDef>? TemplateChildActivated;

    public LayersPanel()
    {
        InitializeComponent();
        Tree.PreviewMouseLeftButtonDown += Tree_PreviewMouseLeftButtonDown;
    }

    /// <summary>The rows on show, front first.</summary>
    public IReadOnlyList<LayerRow> Rows { get; private set; } = [];

    public void Attach(DesignerModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        Detach();
        _model = model;
        _model.Changed += Rebuild;
        _model.SelectionChanged += ApplySelection;
        _model.DepthChanged += ApplySelection;
        Rebuild();
    }

    public void Detach()
    {
        if (_model is null) return;
        _model.Changed -= Rebuild;
        _model.SelectionChanged -= ApplySelection;
        _model.DepthChanged -= ApplySelection;
        _model = null;
        Tree.Items.Clear();
        _rows.Clear();
    }

    private void Rebuild()
    {
        if (_model is null) return;
        var focusedKey = (Tree.SelectedItem as TreeViewItem)?.Tag is LayerRow r ? r.Key : null;
        var hadFocus = Tree.IsKeyboardFocusWithin;
        Rows = LayerTree.Build(_model);
        _syncing = true;
        try
        {
            Tree.Items.Clear();
            _rows.Clear();
            foreach (var row in Rows) Tree.Items.Add(Item(row));
            if (focusedKey is not null && _rows.TryGetValue(focusedKey, out var e))
            {
                e.Item.IsSelected = true;
                if (hadFocus) e.Item.Focus();
            }
        }
        finally { _syncing = false; }
        EmptyNote.Visibility = Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplySelection();
    }

    private TreeViewItem Item(LayerRow row)
    {
        var bar = new Border { Width = 3, Height = 16, CornerRadius = new CornerRadius(1.5), Margin = new Thickness(0, 0, 6, 0), Visibility = Visibility.Hidden };
        bar.SetResourceReference(Border.BackgroundProperty, "AccentFillColorDefaultBrush");
        var line = new StackPanel { Orientation = Orientation.Horizontal };
        line.Children.Add(bar);
        line.Children.Add(Text(row.Name, 14, "TextFillColorPrimaryBrush"));
        if (row.Detail.Length > 0) line.Children.Add(Text(row.Detail, 12, "TextFillColorSecondaryBrush", 6));
        if (row.HasOverride && row.Kind != LayerKind.Orphan)
        {
            var dot = new Ellipse { Width = 7, Height = 7, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = "Overridden on this copy" };
            dot.SetResourceReference(Shape.FillProperty, "AccentFillColorDefaultBrush");
            line.Children.Add(dot);
        }
        if (row.IsForkedShipped) line.Children.Add(Badge("fork", null, "Your edited version of a shipped widget"));
        if (row.IsBroken) line.Children.Add(Badge("broken", "SystemFillColorCriticalBackgroundBrush", $"Widget '{row.Name}' is missing or fails to load"));
        if (row.IsOrphan) line.Children.Add(Badge("orphan", "SystemFillColorCautionBackgroundBrush",
            row.Kind == LayerKind.Orphan ? "The widget no longer has what this names; it is kept, and applies again if it comes back" : "Has an override the widget no longer matches"));

        line.Margin = new Thickness(2, 1, 6, 1);
        var item = new TreeViewItem { Header = line, Tag = row, IsExpanded = _expanded.Contains(row.Key) };
        AutomationProperties.SetName(item, SpokenName(row));
        item.Expanded += (_, e) => { if (e.OriginalSource == item) _expanded.Add(row.Key); };
        item.Collapsed += (_, e) => { if (e.OriginalSource == item) _expanded.Remove(row.Key); };
        foreach (var child in row.Children) item.Items.Add(Item(child));
        _rows[row.Key] = new Entry(item, row, bar);
        return item;
    }

    private static TextBlock Text(string text, double size, string brush, double left = 0)
    {
        var t = new TextBlock { Text = text, FontSize = size, Margin = new Thickness(left, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return t;
    }

    /// <summary>A state word. With a background it is a pill of the InfoBar's own severity fill with
    /// primary text: the severity colours as text fall under 4.5:1 on the selected row in the light
    /// theme (caution measured 4.36:1), their background fills under primary text do not. The fork
    /// is accent text, over 8:1 in both themes.</summary>
    private static Border Badge(string text, string? background, string tip)
    {
        var t = Text(text, 12, background is null ? "AccentTextFillColorPrimaryBrush" : "TextFillColorPrimaryBrush");
        t.FontWeight = FontWeights.SemiBold;
        var pill = new Border { Child = t, CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 0, 5, 1), Margin = new Thickness(8, 0, 0, 0), ToolTip = tip, VerticalAlignment = VerticalAlignment.Center };
        if (background is not null) pill.SetResourceReference(Border.BackgroundProperty, background);
        return pill;
    }

    /// <summary>What a screen reader says for a row: the name, what kind of thing it is, then each marker.</summary>
    internal static string SpokenName(LayerRow row)
    {
        var parts = new List<string>(6);
        switch (row.Kind)
        {
            case LayerKind.Copy: parts.Add(row.Name); parts.Add($"copy {row.Detail}"); break;
            case LayerKind.Orphan: parts.Add($"orphan {row.Detail} {row.Name}"); break;
            default: parts.Add(row.Name); parts.Add(row.Detail); break;
        }
        if (row.HasOverride && row.Kind != LayerKind.Orphan) parts.Add("overridden");
        if (row.IsForkedShipped) parts.Add("forked from shipped");
        if (row.IsBroken) parts.Add("broken widget");
        if (row.IsOrphan && row.Kind == LayerKind.Copy) parts.Add("has orphan overrides");
        return string.Join(", ", parts);
    }

    // ---- tree to model -------------------------------------------------------------------------

    private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        ShowBars();
        if (_syncing || e.NewValue is not TreeViewItem { Tag: LayerRow row }) return;
        Activate(row, add: (Keyboard.Modifiers & ModifierKeys.Control) != 0);
    }

    /// <summary>A click on a row that the tree already has selected changes nothing in the tree, so
    /// the model hears about every click here; Ctrl+click adds, and is handled so the tree keeps its
    /// own selection where it was.</summary>
    private void Tree_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        for (var d = e.OriginalSource as DependencyObject; d is not null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (d is ToggleButton) return;   // the expander arrow
            if (d is not TreeViewItem { Tag: LayerRow row }) continue;
            var add = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            Activate(row, add);
            if (add) e.Handled = true;
            return;
        }
    }

    private void Activate(LayerRow row, bool add)
    {
        if (_model is null) return;
        var id = row.SelectId;
        List<string> ids = add ? [.. _model.Selection.Where(s => s != id), id] : [id];
        if (!ids.SequenceEqual(_model.Selection))
        {
            _model.Select(ids);
            if (!_model.Selection.Contains(id) && _model.Depth.Kind != DepthKind.Layout)
            {
                _model.SetDepth(Depth.Layout);
                _model.Select(ids);
            }
        }
        if (row is { Kind: LayerKind.Child, ChildId: { } child } && ComponentLookup.Find(_model.Parts, id, child) is { Parent: { } parent } found)
            TemplateChildActivated?.Invoke(found.Def, parent);
    }

    // ---- model to tree -------------------------------------------------------------------------

    private void ApplySelection()
    {
        if (_model is null) return;
        var selected = _model.Selection.ToHashSet(StringComparer.Ordinal);
        Entry? first = null;
        foreach (var e in _rows.Values)
        {
            var on = e.Row.Kind is LayerKind.Copy or LayerKind.Part or LayerKind.Component && selected.Contains(e.Row.SelectId);
            e.Bar.Tag = on;
            if (!on) continue;
            first ??= e;
            if (e.Row.Kind == LayerKind.Part && ItemsControl.ItemsControlFromItemContainer(e.Item) is TreeViewItem parent) parent.IsExpanded = true;
        }

        // The tree's own (single) selection follows, unless it already sits on a row the model has
        // selected: a child or orphan row stays put under its selected repeater or copy.
        var current = (Tree.SelectedItem as TreeViewItem)?.Tag as LayerRow;
        if (current is null || !selected.Contains(current.SelectId))
        {
            _syncing = true;
            try
            {
                if (first is not null) first.Item.IsSelected = true;
                else if (Tree.SelectedItem is TreeViewItem old) old.IsSelected = false;
            }
            finally { _syncing = false; }
        }
        ShowBars();
    }

    /// <summary>Every row the model has selected gets an accent bar, except the tree's own selected
    /// row, which the Fluent TreeViewItem already marks with its own pill. No background wash: the
    /// badges' contrast is measured against the window and the tree's selected row only.</summary>
    private void ShowBars()
    {
        foreach (var e in _rows.Values)
            e.Bar.Visibility = e.Bar.Tag is true && !e.Item.IsSelected ? Visibility.Visible : Visibility.Hidden;
    }
}
