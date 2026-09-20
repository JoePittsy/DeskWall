using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using DeskWall.Core.Bindings;
using DeskWall.Core.Layout;
using DeskWall.Core.Values;
using DeskWall.Designer.Model;

namespace DeskWall.Designer.Views;

/// <summary>Job: show and edit the geometry and per-type properties of the current selection, with
/// an inline path to make any property live-bound, so the owner never opens the layout JSON by hand.
/// Leaves out: no grouping cards beyond geometry vs. the rest, no tooltip that repeats the label, no
/// icon per property, colour swatches only on colour-typed properties.</summary>
public partial class PropertiesPanel : UserControl
{
    private DesignerModel? _model;
    private LiveSources? _live;
    private string? _templateParentId;
    private string? _templateChildId;
    private readonly Dictionary<string, TextBlock> _previewLabels = new();

    public PropertiesPanel() => InitializeComponent();

    public void Attach(DesignerModel model)
    {
        if (_model is not null) { _model.Changed -= OnChanged; _model.SelectionChanged -= OnSelectionChanged; }
        _model = model;
        _model.Changed += OnChanged;
        _model.SelectionChanged += OnSelectionChanged;
        Render();
    }

    /// <summary>The running sources feeding the binding picker and the resolved-value previews.
    /// Settable independently of Attach because the host may recreate LiveSources whenever the
    /// layout's Sources list changes (see SourcesPanel).</summary>
    public LiveSources? Live
    {
        get => _live;
        set
        {
            if (_live is not null) _live.Updated -= OnLiveUpdated;
            _live = value;
            if (_live is not null) _live.Updated += OnLiveUpdated;
            RefreshPreviews();
        }
    }

    /// <summary>Called by LayersPanel when a repeater's template child is activated. Template
    /// children are not reachable through DesignerModel.Find, so this panel tracks the pair of ids
    /// and re-resolves against the live LayoutFile on every read and edit, never holding a stale
    /// ComponentDef reference across an Undo/Redo.</summary>
    public void ShowTemplateChild(ComponentDef child, RepeaterDef parent)
    {
        _templateParentId = parent.Id;
        _templateChildId = child.Id;
        Render();
    }

    private void OnChanged() => Render();

    private void OnSelectionChanged()
    {
        if (_model is { Selection.Count: > 0 }) { _templateParentId = null; _templateChildId = null; }
        Render();
    }

    private void OnLiveUpdated() => Dispatcher.Invoke(RefreshPreviews);

    private string? CurrentId() => _templateChildId ?? (_model is { Selection.Count: 1 } ? _model.Selection[0] : null);

    private ComponentLookup.Found? CurrentFound()
        => _model is not null && CurrentId() is { } id ? ComponentLookup.Find(_model.Layout, _templateParentId, id) : null;

    private void EditCurrent(string label, Action<ComponentDef> mutate)
    {
        if (_model is null || CurrentId() is not { } id) return;
        // Captured, not read inside the callback: the pair (parent, child) is what identifies a
        // template child; the child id alone matches the first one in any repeater.
        var parentId = _templateParentId;
        _model.Edit(label, l => { if (ComponentLookup.Find(l, parentId, id) is { } f) mutate(f.Def); });
    }

    /// <summary>The tree a binding picker for the current selection should show. Inside a repeater
    /// template, that is the first item of the repeater's own bound list, not the whole tree.</summary>
    private RecordValue PickerRoot()
    {
        var full = _live?.Tree() ?? ValueTree.Empty;
        if (_templateParentId is null || _model is null) return full;
        if (ComponentLookup.FindRepeater(_model.Layout, _templateParentId) is { Items.Binding: { } items })
        {
            var resolved = BindingResolver.Resolve(items, full);
            if (resolved is ListValue { Items.Count: > 0 } list) return list.Items[0];
        }
        return full;
    }

    private void Render()
    {
        Root.Children.Clear();
        _previewLabels.Clear();

        if (CurrentFound() is not { } found)
        {
            EmptyText.Visibility = Visibility.Visible;
            Root.Visibility = Visibility.Collapsed;
            return;
        }
        EmptyText.Visibility = Visibility.Collapsed;
        Root.Visibility = Visibility.Visible;

        Root.Children.Add(BuildGeometryRow(found.Def));
        foreach (var prop in PropertySchema.For(found.Def)) Root.Children.Add(BuildPropRow(found.Def, prop));
    }

    private void RefreshPreviews()
    {
        if (_previewLabels.Count == 0) return;
        var found = CurrentFound();
        if (found is not { } f) return;
        var tree = PickerRoot();
        foreach (var (name, label) in _previewLabels)
        {
            var prop = PropertySchema.For(f.Def).FirstOrDefault(p => p.Name == name);
            if (prop?.Get(f.Def) is { Binding: { } binding }) label.Text = Preview(binding, tree);
        }
    }

    private static string Preview(Binding binding, RecordValue tree) => BindingResolver.ResolveText(binding, tree) ?? "(no value yet)";

    /// <summary>Commit a literal, unless the property already says exactly that. Enter commits and
    /// then clears focus, which raises LostFocus on the box it just detached and would commit the
    /// identical value a second time: two undo entries per Enter, so the first Ctrl+Z did nothing
    /// visible.</summary>
    private void SetLiteral(PropertySchema.Prop prop, string text)
    {
        if (CurrentFound() is { } f && prop.Get(f.Def) is { Binding: null } current && current.LiteralText == text) return;
        EditCurrent($"Set {prop.Name}", d => prop.Set(d, PropertyValue.Literal(text)));
    }

    // ---- geometry ---------------------------------------------------------------------------

    private FrameworkElement BuildGeometryRow(ComponentDef def)
    {
        var panel = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        foreach (var (name, get, set) in PropertySchema.Geometry)
        {
            panel.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 2, 0) });
            var box = new TextBox { Width = 48, Text = get(def).ToString(System.Globalization.CultureInfo.InvariantCulture) };
            void Commit()
            {
                if (!int.TryParse(box.Text.Trim(), out var v)) return;
                if (CurrentFound() is { } f && get(f.Def) == v) return;   // see SetLiteral
                EditCurrent($"Set {name}", d => set(d, v));
            }
            box.LostFocus += (_, _) => Commit();
            box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); Keyboard.ClearFocus(); } };
            panel.Children.Add(box);
        }
        return panel;
    }

    // ---- properties ---------------------------------------------------------------------------

    private FrameworkElement BuildPropRow(ComponentDef def, PropertySchema.Prop prop)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var label = new TextBlock { Text = prop.Name, Width = 90, Margin = new Thickness(0, 4, 4, 0) };
        DockPanel.SetDock(label, Dock.Left);
        row.Children.Add(label);

        var body = new StackPanel();
        row.Children.Add(body);

        var value = prop.Get(def) ?? PropertyValue.Literal("");

        // A bound Binding-editor property edits through its own text: there is no literal editor to
        // offer it, and so no Bind toggle either.
        if (prop.Editor == PropertySchema.Editor.Binding && value.Binding is { } only)
        {
            body.Children.Add(BuildBoundDisplay(prop, only));
            return row;
        }

        body.Children.Add(BuildBindBox(prop, value));
        if (value.Binding is { } bound) body.Children.Add(BuildBoundDisplay(prop, bound));
        else if (prop.Editor == PropertySchema.Editor.Binding)
        {
            // "items": "drives" parses as a literal, so an unbound Items is loadable and the resolver
            // already refuses it at render time. The panel shows what is there and says so in one
            // line rather than dereferencing a Binding that is null.
            body.Children.Add(new TextBlock { Text = value.LiteralText ?? "" });
            body.Children.Add(Note($"{prop.Name.ToLowerInvariant()} must be a binding"));
        }
        else body.Children.Add(BuildLiteralEditor(prop, value));
        return row;
    }

    /// <summary>The Bind toggle. Checking it opens the picker, and cancelling has to put the box
    /// back - a programmatic reset that raises Unchecked, which used to commit Literal("") over the
    /// value the owner was only looking at. The guard lets the box follow the model without firing
    /// an edit, and Unchecked is an unbind only when there is a binding to remove.</summary>
    private CheckBox BuildBindBox(PropertySchema.Prop prop, PropertyValue value)
    {
        var bindBox = new CheckBox { Content = "Bind", IsChecked = value.IsBound, Margin = new Thickness(0, 0, 0, 4) };
        var syncing = false;
        bindBox.Checked += (_, _) =>
        {
            if (syncing) return;
            var picker = new BindingPicker(PickerRoot(), null) { Owner = Window.GetWindow(this) };
            if (picker.ShowDialog() == true && picker.Result is { } bound) EditCurrent($"Bind {prop.Name}", d => prop.Set(d, bound));
            else { syncing = true; try { bindBox.IsChecked = false; } finally { syncing = false; } }
        };
        bindBox.Unchecked += (_, _) =>
        {
            if (syncing || !value.IsBound) return;
            EditCurrent($"Unbind {prop.Name}", d => prop.Set(d, PropertyValue.Literal("")));
        };
        return bindBox;
    }

    private static TextBlock Note(string text) => new()
    {
        Text = text,
        Foreground = SystemColors.GrayTextBrush,
        FontSize = SystemFonts.MessageFontSize - 1,
    };

    private FrameworkElement BuildBoundDisplay(PropertySchema.Prop prop, Binding binding)
    {
        var panel = new StackPanel();
        var text = new TextBlock
        {
            Text = binding.ToString(),
            Cursor = Cursors.Hand,
            TextDecorations = TextDecorations.Underline,
        };
        text.MouseLeftButtonUp += (_, _) =>
        {
            var picker = new BindingPicker(PickerRoot(), binding) { Owner = Window.GetWindow(this) };
            if (picker.ShowDialog() == true && picker.Result is { } bound) EditCurrent($"Set {prop.Name} binding", d => prop.Set(d, bound));
        };
        var preview = Note(Preview(binding, PickerRoot()));
        _previewLabels[prop.Name] = preview;
        panel.Children.Add(text);
        panel.Children.Add(preview);
        return panel;
    }

    private FrameworkElement BuildLiteralEditor(PropertySchema.Prop prop, PropertyValue value) => prop.Editor switch
    {
        PropertySchema.Editor.Enum => BuildEnumEditor(prop, value),
        PropertySchema.Editor.Font => BuildFontEditor(prop, value),
        PropertySchema.Editor.Color => BuildColorEditor(prop, value),
        PropertySchema.Editor.Path => BuildPathEditor(prop, value),
        _ => BuildTextEditor(prop, value),
    };

    private FrameworkElement BuildTextEditor(PropertySchema.Prop prop, PropertyValue value)
    {
        var box = new TextBox { Text = value.LiteralText ?? "" };
        void Commit() => SetLiteral(prop, box.Text);
        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); Keyboard.ClearFocus(); } };
        return box;
    }

    private FrameworkElement BuildEnumEditor(PropertySchema.Prop prop, PropertyValue value)
    {
        var choices = prop.Choices ?? [];
        var combo = new ComboBox { ItemsSource = choices, SelectedItem = MatchChoice(choices, value.LiteralText) };
        combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is string s) SetLiteral(prop, s); };
        return combo;
    }

    private static string? MatchChoice(string[] choices, string? current)
        => choices.FirstOrDefault(c => string.Equals(c, current, StringComparison.OrdinalIgnoreCase)) ?? choices.FirstOrDefault();

    private FrameworkElement BuildFontEditor(PropertySchema.Prop prop, PropertyValue value)
    {
        var families = Fonts.SystemFontFamilies.OrderBy(f => f.Source, StringComparer.OrdinalIgnoreCase).ToList();
        var combo = new ComboBox { ItemsSource = families, DisplayMemberPath = "Source" };
        combo.SelectedItem = families.FirstOrDefault(f => string.Equals(f.Source, value.LiteralText, StringComparison.OrdinalIgnoreCase));
        combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is FontFamily f) SetLiteral(prop, f.Source); };
        return combo;
    }

    private FrameworkElement BuildColorEditor(PropertySchema.Prop prop, PropertyValue value)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var swatch = new Rectangle { Width = 18, Height = 18, Margin = new Thickness(0, 0, 4, 0), Stroke = SystemColors.ControlDarkBrush, StrokeThickness = 1 };
        var box = new TextBox { Width = 100, Text = value.LiteralText ?? "" };

        void ApplySwatch()
        {
            try { swatch.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(box.Text.Trim())); }
            catch (FormatException) { swatch.Fill = Brushes.Transparent; }
        }
        void Commit() => SetLiteral(prop, box.Text.Trim());

        ApplySwatch();
        box.TextChanged += (_, _) => ApplySwatch();
        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); Keyboard.ClearFocus(); } };

        panel.Children.Add(swatch);
        panel.Children.Add(box);
        return panel;
    }

    private FrameworkElement BuildPathEditor(PropertySchema.Prop prop, PropertyValue value)
    {
        var panel = new DockPanel();
        var browse = new Button { Content = "...", Width = 28 };
        DockPanel.SetDock(browse, Dock.Right);
        var box = new TextBox { Text = value.LiteralText ?? "" };

        void Commit() => SetLiteral(prop, box.Text.Trim());
        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); Keyboard.ClearFocus(); } };

        browse.Click += (_, _) =>
        {
            var dir = System.IO.Path.GetDirectoryName(box.Text);
            var dlg = new Microsoft.Win32.OpenFileDialog { InitialDirectory = !string.IsNullOrEmpty(dir) && Directory.Exists(dir) ? dir : null };
            if (dlg.ShowDialog(Window.GetWindow(this)) == true) { box.Text = dlg.FileName; Commit(); }
        };

        panel.Children.Add(browse);
        panel.Children.Add(box);
        return panel;
    }
}
