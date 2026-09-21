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
/// The widget editor's left-hand bottom half: what this widget reads, and what it is reading right
/// now.
/// <para>
/// Job: get from "I want the CPU load" to a value on screen without knowing the word "source". The
/// list is what the widget has, "+ Source" adds one (the four built-ins need no answers at all),
/// the form asks the two or three things the chosen type genuinely needs, and the tree underneath
/// is the live answer - so a bound value on the canvas and a value in the tree can never disagree,
/// they come from the same running set.
/// </para>
/// <para>
/// Binding a part to a value is not here: that is the properties panel's Bind toggle and its
/// picker, which already lists every source. Clicking a path here copies it, which is what the
/// owner wants it for when he is writing a format string by hand.
/// </para>
/// </summary>
public partial class SourcesPanel : UserControl
{
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);

    private WidgetDocument? _document;
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

    /// <summary>A setting was exposed or unexposed as a knob; the window's knob list is stale.</summary>
    public event Action? AdjustablesChanged;

    public void Attach(WidgetDocument document)
    {
        if (_document is not null) _document.Model.Changed -= OnChanged;
        _document = document;
        _document.Model.Changed += OnChanged;
        _rowsKey = "";
        Refresh();
    }

    /// <summary>The running sources. Replaced by the window whenever the document's source list
    /// changes, because a source's identity is its definition.</summary>
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
        if (_document is null) return;
        var sources = _document.Model.Layout.Sources;
        if (_selected is not null && !sources.Any(s => s.Name == _selected)) _selected = null;
        _selected ??= sources.FirstOrDefault()?.Name;

        var key = string.Join(";", sources.Select(s => s.Name + ":" + s.Type + ":" + Age(s.Name))) + "|" + _selected;
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
        var state = new TextBlock
        {
            Text = Age(def.Name),
            FontSize = 12,
            Margin = new Thickness(8, 0, 0, 0),
            Foreground = Brush(Snapshot(def.Name)?.LastError is null ? "TextFillColorTertiaryBrush" : "SystemFillColorCriticalBrush"),
        };
        DockPanel.SetDock(state, Dock.Right);
        panel.Children.Add(state);
        panel.Children.Add(new TextBlock
        {
            Text = $"{def.Name}  \u00b7  {def.Type}",
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = Brush("TextFillColorPrimaryBrush"),
        });
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
        if (_document is null) return;
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
        if (_document is null) return;
        var taken = _document.Model.Layout.Sources.Select(s => s.Name).ToList();
        if (SourceForms.BuiltIn.Contains(type))
        {
            // One instance of each: two "hardware" samplers read the same machine, and the second
            // costs a native library for nothing.
            if (taken.Contains(type, StringComparer.OrdinalIgnoreCase)) { Status?.Invoke($"This widget already reads {type}."); Select(type); return; }
            _document.AddSource(new SourceDef { Name = type, Type = type });
            Select(type);
            return;
        }

        var values = SourceForms.Defaults(type);
        values[SourceForms.NameKey] = FreeName(type, taken);
        _document.AddSource(SourceForms.ToSourceDef(type, values));
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
        if (_document is null || _selected is null) return;
        var name = _selected;
        _selected = null;
        _document.RemoveSource(name);
        Status?.Invoke($"Removed {name}.");
        AdjustablesChanged?.Invoke();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_live is null || _selected is null) return;
        try { await _live.RefreshNowAsync(_selected); }
        catch (ArgumentException) { /* the set was rebuilt out from under the click */ }
    }

    // ---- the form -------------------------------------------------------------------------------

    private SourceDef? SelectedDef()
        => _document?.Model.Layout.Sources.FirstOrDefault(s => s.Name == _selected);

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
        stack.Children.Add(new TextBlock
        {
            Text = field.Label,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 3),
            Foreground = Brush("TextFillColorSecondaryBrush"),
        });

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
            row.Children.Add(combo);
        }
        else
        {
            // The hint wins over the value: a path field's value is short and readable in the box,
            // and what may be typed in it (runtime:, %ENV%) is the part that is not obvious.
            var box = new TextBox { Text = values[field.Key], ToolTip = field.Hint ?? values[field.Key] };
            void Do() => Commit(def.Name, field.Key, box.Text.Trim());
            box.LostFocus += (_, _) => Do();
            box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Do(); Keyboard.ClearFocus(); } };
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
        if (_document is null || field.Key == SourceForms.NameKey) return null;
        var name = def.Name;
        var toggle = new ToggleButton
        {
            Content = "Knob",
            FontSize = 11,
            Padding = new Thickness(6, 1, 6, 1),
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsChecked = _document.IsSettingAdjustable(name, field.Key),
            ToolTip = "Let whoever places this widget change it",
        };
        toggle.Click += (_, _) =>
        {
            toggle.IsChecked = _document.ToggleSettingAdjustable(name, field.Key);
            AdjustablesChanged?.Invoke();
        };
        return toggle;
    }

    private void Commit(string name, string key, string value)
    {
        if (_document is null) return;
        var def = _document.Model.Layout.Sources.FirstOrDefault(s => s.Name == name);
        if (def is null) return;

        var values = SourceForms.FromSourceDef(def);
        if (string.Equals(values.GetValueOrDefault(key), value, StringComparison.Ordinal)) return;
        values[key] = value;

        if (SourceForms.Validate(def.Type, values,
                _document.Model.Layout.Sources.Where(s => s.Name != name).Select(s => s.Name)) is { } problem)
        {
            Status?.Invoke(problem);
            _rowsKey = ""; Refresh();                 // put the box back to what the document holds
            return;
        }

        var replacement = SourceForms.ToSourceDef(def.Type, values, def);
        _document.ReplaceSource(name, replacement);
        if (key == SourceForms.NameKey) { _selected = replacement.Name; AdjustablesChanged?.Invoke(); }
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

    private Brush Brush(string key) => TryFindResource(key) as Brush ?? SystemColors.ControlTextBrush;
}
