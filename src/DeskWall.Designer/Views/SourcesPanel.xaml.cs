using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using DeskWall.Core.Layout;
using DeskWall.Core.Sources;
using DeskWall.Core.Values;
using DeskWall.Designer.Model;
using DeskWall.Designer.Model.Widgets;

namespace DeskWall.Designer.Views;

/// <summary>
/// The right column at widget depth: what the widget being edited reads, and what it is reading
/// right now. Hidden at the other depths (a copy's sources are its widget's; the layout's own are
/// listed by the properties panel with nothing selected).
/// <para>
/// Job: get from "I want the CPU load" to a value on screen without knowing the word "source". The
/// list is what the widget has, "+ Source" adds one (the four built-ins need no answers at all),
/// the form asks the two or three things the chosen type genuinely needs, and the tree underneath
/// is the live answer - so a bound value on the canvas and a value in the tree can never disagree,
/// they come from the same running set.
/// </para>
/// <para>
/// Binding a part to a value is not here: that is the properties panel's binding chip, or a value
/// dragged from Insert. Clicking a path here copies it. Every change is one edit to the widget
/// (<see cref="Lens.EditWidget"/>), undone with everything else, written by Apply.
/// </para>
/// </summary>
public partial class SourcesPanel : UserControl
{
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);

    private DesignerModel? _model;
    private LiveSources? _live;
    private string? _selected;
    private string _rowsKey = "";
    private string? _treeKey;

    public SourcesPanel()
    {
        InitializeComponent();
        ValueTree.AddHandler(TreeViewItem.ExpandedEvent, new RoutedEventHandler(OnTreeExpanded));
        ValueTree.AddHandler(TreeViewItem.CollapsedEvent, new RoutedEventHandler(OnTreeCollapsed));
    }

    /// <summary>Something worth putting on the window's status line.</summary>
    public event Action<string>? Status;

    public void Attach(DesignerModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (_model is not null) { _model.Changed -= OnChanged; _model.DepthChanged -= OnChanged; }
        _model = model;
        _model.Changed += OnChanged;
        _model.DepthChanged += OnChanged;
        _rowsKey = "";
        Refresh();
    }

    /// <summary>The widget open at widget depth, or null.</summary>
    private string? OpenKey => _model?.Depth is { Kind: DepthKind.Widget, WidgetKey: { } key } ? key : null;

    /// <summary>The open widget's sources (the projection's, which are the widget's own).</summary>
    private IReadOnlyList<SourceDef> Defs => OpenKey is null ? [] : _model!.Parts.Sources;

    private bool EditWidget(string label, Action<WidgetDocument> change)
        => _model is not null && OpenKey is { } key && Lens.EditWidget(_model, key, label, change);

    /// <summary>The window's running sources, which include the open widget's own at widget depth.
    /// Replaced whenever the set changes, because a source's identity is its definition.</summary>
    public LiveSources? Live
    {
        get => _live;
        set
        {
            if (_live is not null) _live.Updated -= OnLiveUpdated;
            _live = value;
            if (_live is not null) _live.Updated += OnLiveUpdated;
            _treeKey = null;
            UpdateTree();
        }
    }

    private void OnChanged() => Refresh();

    private void OnLiveUpdated() => Dispatcher.BeginInvoke(new Action(() => { _rowsKey = ""; Refresh(); }));

    // ---- the list ------------------------------------------------------------------------------

    /// <summary>Rebuilt only when what it shows has changed: every commit in the form goes through
    /// DesignerModel.Edit, and a rebuild on each one would take the focus out of the box being
    /// typed into.</summary>
    private void Refresh()
    {
        if (_model is null) return;
        var sources = Defs;
        if (_selected is not null && !sources.Any(s => s.Name == _selected)) _selected = null;
        _selected ??= sources.FirstOrDefault()?.Name;

        var key = OpenKey + "|" + string.Join(";", sources.Select(s => s.Name + ":" + s.Type + ":" + Age(s.Name))) + "|" + _selected;
        if (key == _rowsKey) { UpdateTree(); return; }
        _rowsKey = key;

        List.SelectionChanged -= List_SelectionChanged;
        List.Items.Clear();
        foreach (var s in sources)
        {
            var item = new ListBoxItem { Content = Row(s), Tag = s.Name, Padding = new Thickness(6, 3, 6, 3) };
            List.Items.Add(item);
            if (s.Name == _selected) List.SelectedItem = item;
        }
        List.SelectionChanged += List_SelectionChanged;

        EmptyNote.Visibility = sources.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        List.Visibility = sources.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        BuildForm();
        UpdateTree();
    }

    private FrameworkElement Row(SourceDef def)
    {
        var panel = new DockPanel();
        var state = Themed(new TextBlock
        {
            Text = Age(def.Name),
            FontSize = 12,
            Margin = new Thickness(8, 0, 0, 0),
        }, Snapshot(def.Name)?.LastError is null ? "TextFillColorSecondaryBrush" : "SystemFillColorCriticalBrush");
        DockPanel.SetDock(state, Dock.Right);
        panel.Children.Add(state);
        panel.Children.Add(Themed(new TextBlock
        {
            Text = $"{def.Name}  \u00b7  {def.Type}",
            TextTrimming = TextTrimming.CharacterEllipsis,
        }, "TextFillColorPrimaryBrush"));
        return panel;
    }

    private SourceSnapshot? Snapshot(string name)
        => _live?.Snapshots.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    private string Age(string name)
    {
        var snap = Snapshot(name);
        if (snap?.LastError is not null) return "failing";
        return snap?.LastRefresh is { } at ? at.LocalDateTime.ToString("HH:mm:ss", System.Globalization.CultureInfo.CurrentCulture) : "waiting";
    }

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selected = (List.SelectedItem as ListBoxItem)?.Tag as string;
        BuildForm();
        UpdateTree();
    }

    // ---- adding --------------------------------------------------------------------------------

    /// <summary>Seven rows, in one menu rather than seven buttons: four that need no answers and
    /// three that open the form.</summary>
    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (OpenKey is null) return;
        var menu = new ContextMenu { PlacementTarget = AddButton, Placement = PlacementMode.Bottom };
        foreach (var type in SourceForms.BuiltIn) menu.Items.Add(AddItem(type, Describe(type)));
        menu.Items.Add(new Separator());
        foreach (var type in SourceForms.Configurable) menu.Items.Add(AddItem(type, Describe(type)));
        menu.IsOpen = true;
    }

    private MenuItem AddItem(string type, string description)
    {
        var item = new MenuItem { Header = type, InputGestureText = description };
        item.Click += (_, _) => Add(type);
        return item;
    }

    private static string Describe(string type) => type switch
    {
        "time" => "the clock",
        "disks" => "free space on every drive",
        "system" => "uptime, last crash, pending reboot",
        "hardware" => "CPU, GPU and RAM load",
        "http" => "a web address",
        "command" => "a program you run",
        "file" => "a file on disk",
        _ => "",
    };

    private void Add(string type)
    {
        if (OpenKey is null) return;
        var taken = Defs.Select(s => s.Name).ToList();
        if (SourceForms.BuiltIn.Contains(type))
        {
            // One instance of each: two "hardware" samplers read the same machine, and the second
            // costs a native library for nothing.
            if (taken.Contains(type, StringComparer.OrdinalIgnoreCase)) { Status?.Invoke($"This widget already reads {type}."); Select(type); return; }
            EditWidget($"Add {type}", d => d.AddSource(new SourceDef { Name = type, Type = type }));
            Select(type);
            return;
        }

        var values = SourceForms.Defaults(type);
        values[SourceForms.NameKey] = FreeName(type, taken);
        EditWidget($"Add {type}", d => d.AddSource(SourceForms.ToSourceDef(type, values)));
        Select(values[SourceForms.NameKey]);
    }

    private static string FreeName(string type, IReadOnlyCollection<string> taken)
    {
        if (!taken.Contains(type, StringComparer.OrdinalIgnoreCase)) return type;
        var n = 2;
        while (taken.Contains(type + n, StringComparer.OrdinalIgnoreCase)) n++;
        return type + n;
    }

    private void Select(string name)
    {
        _selected = name;
        _rowsKey = "";
        Refresh();
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (OpenKey is null || _selected is null) return;
        var name = _selected;
        _selected = null;
        EditWidget($"Remove {name}", d => d.RemoveSource(name));
        Status?.Invoke($"Removed {name}.");
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_live is null || _selected is null) return;
        try { await _live.RefreshNowAsync(_selected); }
        catch (ArgumentException) { /* the set was rebuilt out from under the click */ }
    }

    // ---- the form -------------------------------------------------------------------------------

    private SourceDef? SelectedDef()
        => Defs.FirstOrDefault(s => s.Name == _selected);

    private void BuildForm()
    {
        Form.Children.Clear();
        var def = SelectedDef();
        FormActions.Visibility = def is null ? Visibility.Collapsed : Visibility.Visible;
        if (def is null) return;

        var fields = SourceForms.For(def.Type);
        if (fields.Count == 0)
        {
            Form.Children.Add(new TextBlock
            {
                Text = "Nothing to set: this one reads the machine.",
                Style = (Style)FindResource("Hint"),
                Margin = new Thickness(0, 0, 0, 4),
            });
            return;
        }

        var values = SourceForms.FromSourceDef(def);
        foreach (var field in fields) Form.Children.Add(BuildRow(def, field, values));
    }

    /// <summary>Caption above the field, not beside it: this column is 296 px wide, and a label
    /// and a Knob toggle either side of a url box leave about a hundred pixels of url.</summary>
    private FrameworkElement BuildRow(SourceDef def, SourceField field, Dictionary<string, string> values)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        stack.Children.Add(Themed(new TextBlock
        {
            Text = field.Label,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 3),
        }, "TextFillColorSecondaryBrush"));

        var row = new DockPanel();
        if (BuildKnobToggle(def, field) is { } toggle)
        {
            DockPanel.SetDock(toggle, Dock.Right);
            row.Children.Add(toggle);
        }

        if (field.Editor == SourceFieldEditor.Choice)
        {
            var combo = new ComboBox { ItemsSource = field.Choices, SelectedItem = values[field.Key] };
            combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is string s) Commit(def.Name, field.Key, s); };
            System.Windows.Automation.AutomationProperties.SetName(combo, field.Label);
            row.Children.Add(combo);
        }
        else
        {
            // The hint wins over the value: a path field's value is short and readable in the box,
            // and what may be typed in it (runtime:, %ENV%) is the part that is not obvious.
            var box = new TextBox { Text = values[field.Key], ToolTip = field.Hint ?? values[field.Key] };
            void Do() => Commit(def.Name, field.Key, box.Text.Trim());
            box.LostFocus += (_, _) => Do();
            box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Do(); Window.GetWindow(box)?.Focus(); } };
            System.Windows.Automation.AutomationProperties.SetName(box, field.Label);
            row.Children.Add(box);
        }

        stack.Children.Add(row);
        return stack;
    }

    /// <summary>The same "Make adjustable" toggle a property row has (spec 3.4). Not offered on
    /// the name: a knob that renamed the source would leave every binding in the widget pointing
    /// at something that is no longer there.</summary>
    private ToggleButton? BuildKnobToggle(SourceDef def, SourceField field)
    {
        if (OpenKey is null || field.Key == SourceForms.NameKey) return null;
        var name = def.Name;
        var toggle = new ToggleButton
        {
            Content = "Knob",
            FontSize = 11,
            Padding = new Thickness(6, 1, 6, 1),
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsChecked = IsSettingAdjustable(name, field.Key),
            ToolTip = "Let whoever places this widget change it",
        };
        toggle.Click += (_, _) =>
        {
            EditWidget($"Knob {field.Label}", d => d.ToggleSettingAdjustable(name, field.Key));
            toggle.IsChecked = IsSettingAdjustable(name, field.Key);
        };
        System.Windows.Automation.AutomationProperties.SetName(toggle, $"Knob: let a copy change {field.Label}");
        return toggle;
    }

    private bool IsSettingAdjustable(string name, string settingKey)
        => _model is not null && OpenKey is { } key && Copies.TryFind(_model.Finder(), key) is { } t
           && WidgetDocument.FromTemplate(t).IsSettingAdjustable(name, settingKey);

    private void Commit(string name, string key, string value)
    {
        if (OpenKey is null) return;
        var def = Defs.FirstOrDefault(s => s.Name == name);
        if (def is null) return;

        var values = SourceForms.FromSourceDef(def);
        if (string.Equals(values.GetValueOrDefault(key), value, StringComparison.Ordinal)) return;
        values[key] = value;

        if (SourceForms.Validate(def.Type, values,
                Defs.Where(s => s.Name != name).Select(s => s.Name)) is { } problem)
        {
            Status?.Invoke(problem);
            _rowsKey = ""; Refresh();                 // put the box back to what the document holds
            return;
        }

        var replacement = SourceForms.ToSourceDef(def.Type, values, def);
        if (key == SourceForms.NameKey) _selected = replacement.Name;
        EditWidget($"Edit {name}", d => d.ReplaceSource(name, replacement));
        Status?.Invoke("");
    }

    // ---- the live values --------------------------------------------------------------------------

    private void OnTreeExpanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is TreeViewItem { Tag: string path }) _expanded.Add(path);
    }

    private void OnTreeCollapsed(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is TreeViewItem { Tag: string path }) _expanded.Remove(path);
    }

    private void UpdateTree()
    {
        var snap = _selected is null ? null : Snapshot(_selected);
        if (snap?.Values is null)
        {
            if (_treeKey is null) return;
            ValueTree.Items.Clear();
            _treeKey = null;
            return;
        }
        var key = $"{_selected}|{snap.LastRefresh:O}|{snap.LastError}";
        if (key == _treeKey) return;
        _treeKey = key;
        // The path the tree prints is the source's own name plus the path inside it, which is
        // exactly what a binding takes, so what is copied can be pasted into the picker as it is.
        ValueTreeView.Populate(ValueTree, new RecordValue(
            new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase) { [_selected!] = snap.Values }),
            CopyPath, _expanded);
    }

    private void CopyPath(string path)
    {
        try
        {
            Clipboard.SetText(path);
            Status?.Invoke($"Copied {path}");
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Another process has the clipboard open. Saying the path is still most of the use.
            Status?.Invoke(path);
        }
    }

    /// <summary>The text's colour as a live theme reference (it follows a theme switch), not a
    /// lookup of the brush the theme had when the row was built.</summary>
    private static TextBlock Themed(TextBlock text, string key)
    {
        text.SetResourceReference(TextBlock.ForegroundProperty, key);
        return text;
    }
}
