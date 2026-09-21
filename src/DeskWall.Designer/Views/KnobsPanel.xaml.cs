using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Rectangle = System.Windows.Shapes.Rectangle;
using IOPath = System.IO.Path;
using DeskWall.Core.Layout;
using DeskWall.Core.Sources;
using DeskWall.Designer.Model;
using DeskWall.Designer.Model.Widgets;

namespace DeskWall.Designer.Views;

/// <summary>
/// The right-hand panel: what the selected widget lets you change, and nothing else.
/// <para>
/// Job: answer "make this one say something different" with a control per knob and no vocabulary
/// to learn. With nothing selected it answers the only two questions the layout itself poses - the
/// photo behind it and how hard it is compressed - and shows whether each source is actually
/// getting data. Ids, rects and bindings exist in exactly one place in this application: behind the
/// Details expander at the bottom of this panel, which is closed until someone opens it.
/// </para>
/// </summary>
public partial class KnobsPanel : UserControl
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private DesignerModel? _model;
    private IReadOnlyList<WidgetTemplate> _catalog = Array.Empty<WidgetTemplate>();
    private LiveSources? _live;
    private PropertiesPanel? _details;
    private string _renderedKey = "";

    public KnobsPanel() => InitializeComponent();

    /// <summary>The owner asked for this widget to go. The shell removes it and re-arranges, because
    /// the column closing up is the other half of the answer.</summary>
    public event Action<string>? RemoveRequested;

    /// <summary>A widget joined or left the arranger's stack (the Unlock switch). The shell
    /// re-arranges.</summary>
    public event Action? ArrangeRequested;

    public void Attach(DesignerModel model, IReadOnlyList<WidgetTemplate> catalog)
    {
        if (_model is not null) { _model.Changed -= OnChanged; _model.SelectionChanged -= OnChanged; }
        _model = model;
        _catalog = catalog;
        _model.Changed += OnChanged;
        _model.SelectionChanged += OnChanged;
        _renderedKey = "";
        Render();
    }

    /// <summary>The running sources, for the Details binding picker and for the no-selection view's
    /// "is this source actually working" list.</summary>
    public LiveSources? Live
    {
        get => _live;
        set
        {
            if (_live is not null) _live.Updated -= OnLiveUpdated;
            _live = value;
            if (_live is not null) _live.Updated += OnLiveUpdated;
            if (_details is not null) _details.Live = value;
            _renderedKey = "";
            Render();
        }
    }

    private void OnLiveUpdated() => Dispatcher.BeginInvoke(new Action(() => { if (SelectedInstance() is null) { _renderedKey = ""; Render(); } }));

    private void OnChanged() => Render();

    // ---- what is selected -----------------------------------------------------------------------

    private string? SelectedInstance()
    {
        if (_model is not { Selection.Count: > 0 }) return null;
        return _model.Find(_model.Selection[0])?.Widget;
    }

    private WidgetRecord? Record(string instanceId)
        => _model?.Layout.Widgets is { } w && w.TryGetValue(instanceId, out var r) ? r : null;

    private WidgetTemplate? TemplateFor(string instanceId)
        => Record(instanceId) is { } r ? _catalog.FirstOrDefault(t => string.Equals(t.Key, r.Template, StringComparison.OrdinalIgnoreCase)) : null;

    /// <summary>Rebuild only when what this panel shows has actually changed. Every commit here goes
    /// through DesignerModel.Edit, which raises Changed, and rebuilding on that would take the focus
    /// out of the box being typed into.</summary>
    private void Render()
    {
        var instance = SelectedInstance();
        var key = instance is null
            ? "none|" + (_model?.Layout.BaseImage ?? "") + "|" + (_model?.Layout.JpegQuality ?? 0) + "|" + SourcesKey()
            : instance + "|" + string.Join(",", Record(instance)?.Knobs.Select(kv => kv.Key + "=" + kv.Value) ?? [])
                       + "|" + (Record(instance)?.Unlocked == true);
        if (key == _renderedKey) return;
        _renderedKey = key;

        Root.Children.Clear();
        _details = null;
        if (_model is null) return;
        if (instance is null) BuildLayoutPanel();
        else BuildWidgetPanel(instance);
    }

    private string SourcesKey()
    {
        if (_model is null) return "";
        var snaps = _live?.Snapshots ?? Array.Empty<SourceSnapshot>();
        return string.Join(";", _model.Layout.Sources.Select(s =>
            s.Name + ":" + (snaps.FirstOrDefault(x => x.Name == s.Name) is { } sn
                ? (sn.LastError is not null ? "err" : sn.LastRefresh?.ToString("HH:mm:ss", CultureInfo.InvariantCulture) ?? "-")
                : "-")));
    }

    // ---- a widget -------------------------------------------------------------------------------

    private void BuildWidgetPanel(string instanceId)
    {
        var template = TemplateFor(instanceId);
        Root.Children.Add(Header(template?.Name ?? instanceId));

        if (template is null)
        {
            Root.Children.Add(Hint($"This widget was made from a template ('{Record(instanceId)?.Template}') that is not installed. Its knobs cannot be shown, but Details still edits it."));
        }
        else if (template.Knobs.Count == 0)
        {
            Root.Children.Add(Hint("Nothing to set: this widget shows the same thing for everyone."));
        }
        else
        {
            foreach (var knob in template.Knobs) Root.Children.Add(BuildKnob(instanceId, template, knob));
        }

        var remove = new Button { Content = "Remove widget", Margin = new Thickness(0, 20, 0, 0), Padding = new Thickness(12, 6, 12, 6), HorizontalAlignment = HorizontalAlignment.Left };
        remove.Click += (_, _) => RemoveRequested?.Invoke(instanceId);
        Root.Children.Add(remove);

        Root.Children.Add(BuildDetails(instanceId));
    }

    private FrameworkElement BuildKnob(string instanceId, WidgetTemplate template, Knob knob)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        stack.Children.Add(new TextBlock { Text = knob.Label, FontSize = 12, Margin = new Thickness(0, 0, 0, 6), Foreground = Brush("TextFillColorSecondaryBrush") });
        var current = Record(instanceId)?.Knobs.GetValueOrDefault(knob.Id) ?? knob.Default;

        switch (knob.Type)
        {
            case KnobType.Choice:
                stack.Children.Add(ChoiceControl(instanceId, template, knob, current, knob.Choices ?? []));
                break;
            case KnobType.Drive:
                stack.Children.Add(ChoiceControl(instanceId, template, knob, current, FixedDrives()));
                break;
            case KnobType.Color:
                stack.Children.Add(ColorControl(instanceId, template, knob, current));
                break;
            case KnobType.Town:
                stack.Children.Add(TownControl(instanceId, template, knob, current, stack));
                break;
            default:
                stack.Children.Add(TextControl(instanceId, template, knob, current));
                break;
        }
        return stack;
    }

    private void Commit(string instanceId, WidgetTemplate template, Knob knob, string value)
    {
        if (_model is null) return;
        if (string.Equals(Record(instanceId)?.Knobs.GetValueOrDefault(knob.Id), value, StringComparison.Ordinal)) return;
        _model.Edit($"Set {knob.Label}", l => WidgetInstance.SetKnob(l, template, instanceId, knob.Id, value));
    }

    /// <summary>A choice's value may be a composite ("GPU temperature||hardware.gpuTempFraction||...")
    /// whose first part is the only thing a human should ever see; the whole composite is what goes
    /// back to SetKnob. The list therefore shows Display(...) and carries the raw value alongside.</summary>
    private ComboBox ChoiceControl(string instanceId, WidgetTemplate template, Knob knob, string current, IReadOnlyList<string> choices)
    {
        var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        // Real ComboBoxItems with the label as their Content, rather than a wrapper object and a
        // DisplayMemberPath: this way what a screen reader announces is what is on screen, instead
        // of the composite the knob actually stores.
        foreach (var choice in choices)
            combo.Items.Add(new ComboBoxItem { Content = Display(choice), Tag = choice });
        combo.SelectedItem =
            combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => string.Equals((string?)i.Tag, current, StringComparison.OrdinalIgnoreCase))
            ?? combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => string.Equals((string?)i.Content, Display(current), StringComparison.OrdinalIgnoreCase))
            ?? combo.Items.OfType<ComboBoxItem>().FirstOrDefault();
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem { Tag: string value }) Commit(instanceId, template, knob, value);
        };
        return combo;
    }

    /// <summary>The part of a knob value a human is meant to see. Knob values may be composites of
    /// parts joined by "||" (docs/layout-format.md, "Widgets"); part 0 is the display value.</summary>
    private static string Display(string value)
    {
        var i = value.IndexOf("||", StringComparison.Ordinal);
        return i < 0 ? value : value[..i];
    }

    private TextBox TextControl(string instanceId, WidgetTemplate template, Knob knob, string current)
    {
        var box = new TextBox { Text = Display(current) };
        void Do()
        {
            var text = box.Text.Trim();
            if (knob.Type == KnobType.Number)
            {
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
                { box.Text = Display(Record(instanceId)?.Knobs.GetValueOrDefault(knob.Id) ?? knob.Default); return; }
                n = Math.Clamp(n, knob.Min ?? double.MinValue, knob.Max ?? double.MaxValue);
                text = n.ToString("R", CultureInfo.InvariantCulture);
                box.Text = text;
            }
            Commit(instanceId, template, knob, text);
        }
        box.LostFocus += (_, _) => Do();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Do(); Keyboard.ClearFocus(); } };
        return box;
    }

    private FrameworkElement ColorControl(string instanceId, WidgetTemplate template, Knob knob, string current)
    {
        var panel = new DockPanel();
        var swatch = new Rectangle { Width = 24, Height = 24, RadiusX = 4, RadiusY = 4, Margin = new Thickness(0, 0, 8, 0), Stroke = Brush("ControlStrokeColorDefaultBrush"), StrokeThickness = 1 };
        DockPanel.SetDock(swatch, Dock.Left);
        var box = new TextBox { Text = Display(current) };
        void Paint()
        {
            try { swatch.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(box.Text.Trim())); }
            catch (FormatException) { swatch.Fill = Brushes.Transparent; }
        }
        Paint();
        box.TextChanged += (_, _) => Paint();
        box.LostFocus += (_, _) => Commit(instanceId, template, knob, box.Text.Trim());
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(instanceId, template, knob, box.Text.Trim()); Keyboard.ClearFocus(); } };
        panel.Children.Add(swatch);
        panel.Children.Add(box);
        return panel;
    }

    /// <summary>A town is typed, then looked up once. Until it resolves nothing is written: a
    /// half-applied latitude would put the weather somewhere off the coast of Africa.</summary>
    private FrameworkElement TownControl(string instanceId, WidgetTemplate template, Knob knob, string current, StackPanel host)
    {
        var box = new TextBox { Text = Display(current) };
        var note = Hint("");
        note.Margin = new Thickness(0, 4, 0, 0);
        async void Resolve()
        {
            var town = box.Text.Trim();
            if (town.Length == 0 || string.Equals(town, Display(Record(instanceId)?.Knobs.GetValueOrDefault(knob.Id) ?? ""), StringComparison.OrdinalIgnoreCase)) return;
            note.Text = "looking up...";
            var hit = await WidgetInstance.ResolveTownAsync(town, Http).ConfigureAwait(true);
            if (hit is not { } p) { note.Text = $"'{town}' was not found."; return; }
            note.Text = string.Format(CultureInfo.InvariantCulture, "{0} ({1:0.00}, {2:0.00})", town, p.lat, p.lon);
            // "town||lat||lon": SetKnob never makes a network call, so the coordinates travel with
            // the name (docs/layout-format.md, "Widgets").
            Commit(instanceId, template, knob, string.Format(CultureInfo.InvariantCulture, "{0}||{1}||{2}", town, p.lat, p.lon));
        }
        box.LostFocus += (_, _) => Resolve();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Resolve(); Keyboard.ClearFocus(); } };
        host.Children.Add(note);
        return box;
    }

    private static IReadOnlyList<string> FixedDrives()
    {
        try { return DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed).Select(d => d.Name.TrimEnd('\\', ':')).ToList(); }
        catch (IOException) { return Array.Empty<string>(); }
    }

    // ---- details --------------------------------------------------------------------------------

    /// <summary>The one place in the application that shows an id, a rect or a binding, and it is
    /// shut. Inside: which component of this widget to edit, the Phase 5 properties panel for it,
    /// and the switch that takes this widget out of the arranger's hands.</summary>
    private FrameworkElement BuildDetails(string instanceId)
    {
        var body = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };

        var unlock = new CheckBox
        {
            Content = "Unlock position (the column stops arranging this one)",
            IsChecked = Record(instanceId)?.Unlocked == true,
            Margin = new Thickness(0, 0, 0, 12),
        };
        unlock.Checked += (_, _) => SetUnlocked(instanceId, true);
        unlock.Unchecked += (_, _) => SetUnlocked(instanceId, false);
        body.Children.Add(unlock);

        var components = _model is null ? new List<ComponentDef>() : WidgetInstance.Components(_model.Layout, instanceId).ToList();
        var combo = new ComboBox
        {
            ItemsSource = components.Select(c => c.Id).ToList(),
            SelectedItem = _model is { Selection.Count: 1 } ? _model.Selection[0] : components.FirstOrDefault()?.Id,
            Margin = new Thickness(0, 0, 0, 12),
        };
        combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is string id) _model?.Select([id]); };
        body.Children.Add(combo);

        _details = new PropertiesPanel { Live = _live };
        if (_model is not null) _details.Attach(_model);
        body.Children.Add(_details);

        var expander = new Expander { Header = "Details", Margin = new Thickness(0, 20, 0, 0), Content = body };
        // Opening Details narrows the selection to one component so the properties panel has
        // something to show; closing it puts the whole widget back.
        expander.Expanded += (_, _) => { if (combo.SelectedItem is string id) _model?.Select([id]); };
        expander.Collapsed += (_, _) =>
        {
            if (_model is null) return;
            _model.Select(WidgetInstance.Components(_model.Layout, instanceId).Select(c => c.Id).ToList());
        };
        return expander;
    }

    private void SetUnlocked(string instanceId, bool unlocked)
    {
        if (_model is null || Record(instanceId) is not { } record || record.Unlocked == unlocked) return;
        _model.Edit(unlocked ? "Unlock position" : "Lock position", l =>
        {
            if (l.Widgets is not null && l.Widgets.TryGetValue(instanceId, out var r)) r.Unlocked = unlocked;
        });
        ArrangeRequested?.Invoke();
    }

    // ---- nothing selected: the layout's own two knobs, and the sources ------------------------------

    private void BuildLayoutPanel()
    {
        if (_model is null) return;
        Root.Children.Add(Header("Wallpaper"));

        Root.Children.Add(new TextBlock { Text = "Photo behind the widgets", FontSize = 12, Margin = new Thickness(0, 0, 0, 6), Foreground = Brush("TextFillColorSecondaryBrush") });
        var photo = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
        var browse = new Button { Content = "Change...", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(8, 0, 0, 0) };
        DockPanel.SetDock(browse, Dock.Right);
        var name = new TextBlock
        {
            Text = _model.Layout.BaseImage.Length == 0 ? "(none)" : IOPath.GetFileName(_model.Layout.BaseImage),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = _model.Layout.BaseImage,
            Foreground = Brush("TextFillColorPrimaryBrush"),
        };
        browse.Click += (_, _) =>
        {
            var dir = IOPath.GetDirectoryName(_model.Layout.BaseImage);
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp|All files|*.*",
                InitialDirectory = !string.IsNullOrEmpty(dir) && Directory.Exists(dir) ? dir : null,
            };
            if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
            _model.Edit("Set photo", l => l.BaseImage = dlg.FileName);
            _renderedKey = ""; Render();
        };
        photo.Children.Add(browse);
        photo.Children.Add(name);
        Root.Children.Add(photo);

        Root.Children.Add(new TextBlock { Text = "JPEG quality", FontSize = 12, Margin = new Thickness(0, 0, 0, 6), Foreground = Brush("TextFillColorSecondaryBrush") });
        var quality = new DockPanel { Margin = new Thickness(0, 0, 0, 24) };
        var readout = new TextBlock { Width = 32, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Foreground = Brush("TextFillColorSecondaryBrush") };
        DockPanel.SetDock(readout, Dock.Right);
        var slider = new Slider { Minimum = 60, Maximum = 100, Value = _model.Layout.JpegQuality, IsSnapToTickEnabled = true, TickFrequency = 1, VerticalAlignment = VerticalAlignment.Center };
        readout.Text = _model.Layout.JpegQuality.ToString(CultureInfo.InvariantCulture);
        slider.ValueChanged += (_, _) => readout.Text = ((int)slider.Value).ToString(CultureInfo.InvariantCulture);
        // One undo entry per drag, not one per pixel of travel.
        slider.PreviewMouseUp += (_, _) => CommitQuality((int)slider.Value);
        slider.KeyUp += (_, _) => CommitQuality((int)slider.Value);
        quality.Children.Add(readout);
        quality.Children.Add(slider);
        Root.Children.Add(quality);

        Root.Children.Add(Header("Sources"));
        if (_model.Layout.Sources.Count == 0) Root.Children.Add(Hint("None yet. Adding a widget adds whatever it needs."));
        var snaps = _live?.Snapshots ?? Array.Empty<SourceSnapshot>();
        foreach (var source in _model.Layout.Sources)
        {
            var snap = snaps.FirstOrDefault(s => string.Equals(s.Name, source.Name, StringComparison.OrdinalIgnoreCase));
            var row = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            row.Children.Add(new TextBlock { Text = source.Name, FontSize = 13, Foreground = Brush("TextFillColorPrimaryBrush") });
            var state = snap?.LastError is { } err ? $"{source.Type} - failing: {err}"
                : snap?.LastRefresh is { } at ? $"{source.Type} - updated {at.LocalDateTime:HH:mm:ss}"
                : $"{source.Type} - waiting for its first read";
            row.Children.Add(new TextBlock
            {
                Text = state,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = snap?.LastError is null ? Brush("TextFillColorTertiaryBrush") : Brush("SystemFillColorCriticalBrush"),
            });
            Root.Children.Add(row);
        }

        Root.Children.Add(Hint("Select a widget on the preview to change what it says."));
    }

    private void CommitQuality(int value)
    {
        if (_model is null || _model.Layout.JpegQuality == value) return;
        _model.Edit("Set JPEG quality", l => l.JpegQuality = value);
        _renderedKey = "";
    }

    // ---- small parts ------------------------------------------------------------------------------

    private TextBlock Header(string text) => new()
    {
        Text = text,
        FontSize = 16,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 0, 0, 14),
        TextTrimming = TextTrimming.CharacterEllipsis,
        Foreground = Brush("TextFillColorPrimaryBrush"),
    };

    private TextBlock Hint(string text) => new()
    {
        Text = text,
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 0, 8),
        Foreground = Brush("TextFillColorTertiaryBrush"),
    };

    private Brush Brush(string key) => TryFindResource(key) as Brush ?? SystemColors.ControlTextBrush;
}
