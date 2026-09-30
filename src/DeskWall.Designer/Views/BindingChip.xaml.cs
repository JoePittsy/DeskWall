using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using DeskWall.Core.Bindings;
using DeskWall.Core.Layout;
using DeskWall.Core.Values;
using DeskWall.Designer.Model;

namespace DeskWall.Designer.Views;

/// <summary>Job: show what a property is bound to as the owner would say it ("CPU load · 27%"), and
/// change it by typing a few letters of that name rather than a path. The chip opens an inline
/// editor: a search box over <see cref="ValueCatalog"/>, filtered by <see cref="ValueCatalog.Fits"/>
/// for the row's editor kind, and, for text, a format preset (<see cref="FormatPresets"/>).
/// Leaves out: no path box, no format-string box (a binding with a custom format keeps it and shows
/// it as "Custom"), no popup, so the editor is in the panel's own tab order and in a screenshot.
/// <para>It never edits the document: <see cref="Chosen"/> and <see cref="Unbound"/> say what was
/// picked, and the host commits it.</para></summary>
public partial class BindingChip : UserControl
{
    private IReadOnlyList<ValueEntry> _entries = [];
    private RecordValue _tree = ValueTree.Empty;
    private bool _syncing;

    public BindingChip() => InitializeComponent();

    /// <summary>The row's editor kind: what <see cref="ValueCatalog.Fits"/> filters by, and whether a
    /// format is offered (text only: a format on a number or a colour would break it).</summary>
    public PropertySchema.Editor Editor { get; set; } = PropertySchema.Editor.Text;

    /// <summary>The row's human label, for the controls' accessible names.</summary>
    public string PropertyLabel { get; set; } = "";

    /// <summary>The binding shown, or null while binding a row that has none yet.</summary>
    public Binding? Binding { get; private set; }

    public bool IsEditing => EditorPanel.Visibility == Visibility.Visible;

    /// <summary>A value was picked (or the format of the current one changed): bind to it.</summary>
    public event Action<PropertyValue, ValueEntry>? Chosen;

    /// <summary>Unbind was pressed.</summary>
    public event Action? Unbound;

    /// <summary>The editor closed without a pick.</summary>
    public event Action? Closed;

    public void Show(Binding? binding, IReadOnlyList<ValueEntry> entries, RecordValue tree)
    {
        Binding = binding;
        _entries = entries;
        _tree = tree;
        // Hidden, not collapsed: the editor spans the row's label column, and the empty pill's line
        // is what keeps it clear of the label.
        Chip.Visibility = binding is null ? Visibility.Hidden : Visibility.Visible;
        UnbindButton.Visibility = binding is null || Editor == PropertySchema.Editor.Binding ? Visibility.Collapsed : Visibility.Visible;
        FormatRow.Visibility = Editor == PropertySchema.Editor.Text ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(Search, $"Search values for {PropertyLabel}");
        AutomationProperties.SetName(Matches, $"Values for {PropertyLabel}");
        AutomationProperties.SetName(FormatBox, $"Show {PropertyLabel} as");
        AutomationProperties.SetName(UnbindButton, $"Unbind {PropertyLabel}");
        AutomationProperties.SetName(DoneButton, "Done");
        AutomationProperties.SetName(RulesMode, $"Rules for {PropertyLabel}");
        AutomationProperties.SetName(AddRuleButton, $"Add a rule for {PropertyLabel}");
        Refresh(tree);
        if (IsEditing) Filter();
    }

    /// <summary>New live values: the chip's own text follows them, the list does not jump.</summary>
    public void Refresh(RecordValue tree)
    {
        _tree = tree;
        if (Binding is null) return;
        var text = Describe(Binding, _entries, tree);
        ChipText.Text = text;
        AutomationProperties.SetName(Chip, $"{PropertyLabel}: bound to {text}");
        Chip.ToolTip = Binding.ToString();
    }

    /// <summary>Open the editor with the search box focused.</summary>
    public void BeginEdit()
    {
        EditorPanel.Visibility = Visibility.Visible;
        Search.Text = "";
        Filter();
        Dispatcher.BeginInvoke(new Action(() => Search.Focus()), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    public void EndEdit()
    {
        if (!IsEditing) return;
        EditorPanel.Visibility = Visibility.Collapsed;
        Closed?.Invoke();
    }

    // ---- what it says -------------------------------------------------------------------------

    /// <summary>"CPU load · 27%": the value's human label and what it resolves to now.</summary>
    public static string Describe(Binding binding, IReadOnlyList<ValueEntry> entries, RecordValue tree)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(entries);
        var path = PathText(binding);
        // The label table when the value is not in the tree yet (a source that has not read, or a
        // drop's first moment), so the chip never says "hardware.cpu"; and a fraction with no format
        // of its own reads as the Data row reads it ("25%"), never as 0.254.
        var entry = entries.FirstOrDefault(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));
        var (label, fraction) = entry is not null ? (entry.Label, entry.Kind == ValueKind.Fraction) : ValueCatalog.Known(path, []);
        var format = binding.Format ?? (fraction && FormatPresets.For(ValueKind.Fraction) is [var first, ..] ? first.Format : null);
        var value = BindingResolver.Resolve(binding, tree) switch
        {
            null => "no value yet",
            ListValue l => Items(l),
            var v => v.ToText(format),
        };
        return $"{label} · {value}";
    }

    /// <summary>The path half of a binding as a binding writes it, without the format.</summary>
    public static string PathText(Binding binding) => ValueCatalog.PathText(binding);

    /// <summary>The values a row of this editor kind can take whose label or path contains every
    /// word of <paramref name="search"/>, in catalog order.</summary>
    public static IReadOnlyList<ValueEntry> Matching(IReadOnlyList<ValueEntry> entries, PropertySchema.Editor editor, string? search)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var words = (search ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return entries.Where(e => ValueCatalog.Fits(editor, e.Kind)
                && words.All(w => e.Label.Contains(w, StringComparison.OrdinalIgnoreCase) || e.Path.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    /// <summary>The binding for <paramref name="entry"/> shown with <paramref name="format"/>.</summary>
    public static Binding BindingFor(ValueEntry entry, string? format)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return Binding.Parse(string.IsNullOrEmpty(format) ? entry.Path : $"{entry.Path} | \"{format}\"");
    }

    private static string Items(ListValue l) => string.Create(CultureInfo.InvariantCulture, $"{l.Items.Count} item{(l.Items.Count == 1 ? "" : "s")}");

    private static string Sample(ValueEntry e) => e.Sample switch
    {
        ListValue l => Items(l),
        var v => v.ToText(FormatPresets.For(e.Kind).FirstOrDefault()?.Format),
    };

    // ---- the editor ---------------------------------------------------------------------------

    /// <summary>A match: its label, and its sample value right-aligned and secondary, each trimmed
    /// on its own so a long sample (command output) never hides which value it is.</summary>
    private static DockPanel Row(ValueEntry e)
    {
        var sample = new TextBlock { Text = Sample(e), Margin = new Thickness(12, 0, 0, 0), MaxWidth = 110, TextTrimming = TextTrimming.CharacterEllipsis };
        sample.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        DockPanel.SetDock(sample, Dock.Right);
        var row = new DockPanel { LastChildFill = true };
        row.Children.Add(sample);
        row.Children.Add(new TextBlock { Text = e.Label, TextTrimming = TextTrimming.CharacterEllipsis });
        return row;
    }

    private void Filter()
    {
        SearchHint.Visibility = Search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var matches = Matching(_entries, Editor, Search.Text);
        _syncing = true;
        try
        {
            Matches.Items.Clear();
            foreach (var e in matches)
            {
                var text = $"{e.Label} · {Sample(e)}";
                var item = new ListBoxItem { Content = Row(e), Tag = e, Padding = new Thickness(8, 4, 8, 4), ToolTip = text };
                AutomationProperties.SetName(item, text);
                Matches.Items.Add(item);
            }
            var current = Binding is null ? null : PathText(Binding);
            Matches.SelectedIndex = Math.Max(0, matches.ToList().FindIndex(e => string.Equals(e.Path, current, StringComparison.OrdinalIgnoreCase)));
            if (matches.Count == 0) Matches.SelectedIndex = -1;
        }
        finally { _syncing = false; }
        Matches.Visibility = matches.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        NoMatches.Visibility = matches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoMatches.Text = _entries.Count == 0 ? "No live values yet." : "Nothing that fits matches.";
        ShowPresets();
    }

    private ValueEntry? Highlighted => (Matches.SelectedItem as ListBoxItem)?.Tag as ValueEntry;

    /// <summary>The presets for the highlighted value's kind, the current format selected when the
    /// highlighted value is the one bound, else the kind's default.</summary>
    private void ShowPresets()
    {
        _syncing = true;
        try
        {
            FormatBox.Items.Clear();
            if (Highlighted is not { } e) return;
            var presets = FormatPresets.For(e.Kind);
            foreach (var p in presets) FormatBox.Items.Add(new ComboBoxItem { Content = p.Label, Tag = p.Format });
            var current = Binding is not null && string.Equals(PathText(Binding), e.Path, StringComparison.OrdinalIgnoreCase) ? Binding.Format : null;
            if (current is not null && !presets.Any(p => p.Format == current))
                FormatBox.Items.Add(new ComboBoxItem { Content = "Custom", Tag = current, ToolTip = current });
            FormatBox.SelectedItem = FormatBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == current && current is not null)
                ?? FormatBox.Items.OfType<ComboBoxItem>().FirstOrDefault();
        }
        finally { _syncing = false; }
        ShowRules();
    }

    private string? SelectedFormat => RulesFormat()
        ?? (Editor == PropertySchema.Editor.Text ? (FormatBox.SelectedItem as ComboBoxItem)?.Tag as string : _customFormat);

    // ---- rules: a number picks or blends the property's value (Model/Rules.cs) ---------------

    private readonly List<(TextBox At, TextBox To)> _rows = [];
    private TextBox? _otherwise;
    /// <summary>A format on a non-text row that the rules cannot show (a hand-written bool map),
    /// kept as it is rather than dropped when Done is pressed.</summary>
    private string? _customFormat;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>A fraction's rows read in percent: 30 is 0.3.</summary>
    private bool Percent => Highlighted?.Kind == ValueKind.Fraction;

    private RuleMode Mode => (RuleMode)Math.Max(0, RulesMode.SelectedIndex);

    private void ShowRules()
    {
        var e = Highlighted;
        var numeric = e is not null && e.Kind is ValueKind.Fraction or ValueKind.Number && Editor != PropertySchema.Editor.Binding;
        var current = e is not null && Binding is not null && string.Equals(PathText(Binding), e.Path, StringComparison.OrdinalIgnoreCase) ? Binding.Format : null;
        var parsed = Rules.Parse(current);
        _customFormat = parsed is null && Editor != PropertySchema.Editor.Text ? current : null;
        RulesPanel.Visibility = numeric ? Visibility.Visible : Visibility.Collapsed;
        _syncing = true;
        try { RulesMode.SelectedIndex = numeric && parsed is { } p ? (int)p.Mode : 0; }
        finally { _syncing = false; }
        BuildRows(numeric ? parsed : null);
    }

    private void BuildRows((RuleMode Mode, List<Rule> Rows, string Otherwise)? rules)
    {
        RuleRows.Children.Clear();
        _rows.Clear();
        _otherwise = null;
        var mode = Mode;
        AddRuleButton.Visibility = mode == RuleMode.None ? Visibility.Collapsed : Visibility.Visible;
        FormatRow.Visibility = Editor == PropertySchema.Editor.Text && mode == RuleMode.None ? Visibility.Visible : Visibility.Collapsed;
        if (mode == RuleMode.None) return;
        var mine = rules is { } r && r.Mode == mode ? r : ((RuleMode, List<Rule>, string)?)null;
        var top = Percent ? 1 : 100;
        foreach (var row in mine?.Item2 ?? (mode == RuleMode.Step ? [new Rule(top / 2.0, "")] : [new Rule(0, ""), new Rule(top, "")]))
            AddRow(row);
        if (mode == RuleMode.Step)
        {
            _otherwise = RuleBox(mine?.Item3 ?? "", "Otherwise value");
            RuleRows.Children.Add(RuleLine("Otherwise", null, _otherwise, null));
        }
    }

    private void AddRow(Rule rule)
    {
        var at = RuleBox((Percent ? rule.At * 100 : rule.At).ToString("0.###", Inv), Mode == RuleMode.Step ? "Below" : "At");
        at.Width = 52;
        var to = RuleBox(rule.To, "Value");
        var remove = new Button { Content = "✕", Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(4, 0, 0, 0), ToolTip = "Remove" };
        AutomationProperties.SetName(remove, "Remove rule");
        var line = RuleLine(Mode == RuleMode.Step ? "Below" : "At", at, to, remove);
        remove.Click += (_, _) => { RuleRows.Children.Remove(line); _rows.Remove((at, to)); };
        _rows.Add((at, to));
        RuleRows.Children.Insert(_otherwise is null ? RuleRows.Children.Count : RuleRows.Children.Count - 1, line);
    }

    private DockPanel RuleLine(string label, TextBox? at, TextBox to, Button? remove)
    {
        var line = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var text = new TextBlock { Text = label, Width = 64, VerticalAlignment = VerticalAlignment.Center };
        text.SetResourceReference(StyleProperty, "PropLabel");
        DockPanel.SetDock(text, Dock.Left);
        line.Children.Add(text);
        if (at is not null)
        {
            DockPanel.SetDock(at, Dock.Left);
            line.Children.Add(at);
            var unit = new TextBlock { Text = Percent ? "% →" : "→", Margin = new Thickness(4, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(unit, Dock.Left);
            line.Children.Add(unit);
        }
        if (remove is not null) { DockPanel.SetDock(remove, Dock.Right); line.Children.Add(remove); }
        line.Children.Add(to);
        return line;
    }

    private TextBox RuleBox(string text, string name)
    {
        var box = new TextBox { Text = text };
        AutomationProperties.SetName(box, $"{name} for {PropertyLabel}");
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Commit(); e.Handled = true; }
            else if (e.Key == Key.Escape) { EndEdit(); e.Handled = true; }
        };
        return box;
    }

    /// <summary>The rows as a format, or null when there are none (mode None, or not a number).</summary>
    private string? RulesFormat()
    {
        if (RulesPanel.Visibility != Visibility.Visible || Mode == RuleMode.None) return null;
        var rows = _rows.Select(r => double.TryParse(r.At.Text, NumberStyles.Float, Inv, out var a) ? new Rule(Percent ? a / 100 : a, r.To.Text.Trim()) : null)
            .OfType<Rule>();
        return Rules.Write(Mode, rows, _otherwise?.Text.Trim() ?? "");
    }

    private void RulesMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncing && IsLoaded) BuildRows(null);
    }

    private void AddRule_Click(object sender, RoutedEventArgs e)
    {
        var last = _rows.Select(r => double.TryParse(r.At.Text, NumberStyles.Float, Inv, out var a) ? a : 0).DefaultIfEmpty(0).Max();
        AddRow(new Rule((last + 10) / (Percent ? 100 : 1), ""));
        _rows[^1].At.Focus();
    }

    /// <summary>Done, or Enter in a rule: bind with what the editor now says. Nothing changed, or
    /// nothing to bind yet, just closes.</summary>
    private void Commit()
    {
        if (Highlighted is { } entry)
        {
            var bound = Binding is not null && string.Equals(PathText(Binding), entry.Path, StringComparison.OrdinalIgnoreCase);
            if (!bound && RulesFormat() is not null) { Pick(); return; }
            if (bound && BindingFor(entry, SelectedFormat).ToString() != Binding!.ToString())
            {
                EditorPanel.Visibility = Visibility.Collapsed;
                Chosen?.Invoke(PropertyValue.Bound(BindingFor(entry, SelectedFormat)), entry);
                return;
            }
        }
        EndEdit();
    }

    /// <summary>Bind to the highlighted value. The editor stays open only while the format can still
    /// be changed; otherwise it closes, which is the whole pick.</summary>
    internal void Pick()
    {
        if (Highlighted is not { } e) return;
        var binding = BindingFor(e, SelectedFormat);
        EditorPanel.Visibility = Visibility.Collapsed;
        Chosen?.Invoke(PropertyValue.Bound(binding), e);
    }

    private void Chip_Click(object sender, RoutedEventArgs e)
    {
        if (IsEditing) EndEdit(); else BeginEdit();
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e) => Filter();

    private void Search_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down when Matches.Items.Count > 0:
                Matches.SelectedIndex = Math.Min(Matches.Items.Count - 1, Matches.SelectedIndex + 1);
                Matches.ScrollIntoView(Matches.SelectedItem);
                e.Handled = true;
                break;
            case Key.Up when Matches.Items.Count > 0:
                Matches.SelectedIndex = Math.Max(0, Matches.SelectedIndex - 1);
                Matches.ScrollIntoView(Matches.SelectedItem);
                e.Handled = true;
                break;
            case Key.Enter:
                Pick();
                e.Handled = true;
                break;
            case Key.Escape:
                EndEdit();
                e.Handled = true;
                break;
        }
    }

    private void Matches_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { Pick(); e.Handled = true; }
        else if (e.Key == Key.Escape) { EndEdit(); e.Handled = true; }
    }

    private void Matches_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(Matches, d) is ListBoxItem item)
        {
            Matches.SelectedItem = item;
            Pick();
        }
    }

    private void Matches_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncing) ShowPresets();
    }

    /// <summary>A new format on the value already bound applies at once; on any other value it waits
    /// for the pick.</summary>
    private void FormatBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || Binding is null || Highlighted is not { } entry) return;
        if (!string.Equals(PathText(Binding), entry.Path, StringComparison.OrdinalIgnoreCase)) return;
        var binding = BindingFor(entry, SelectedFormat);
        if (binding.ToString() == Binding.ToString()) return;
        Chosen?.Invoke(PropertyValue.Bound(binding), entry);
    }

    private void UnbindButton_Click(object sender, RoutedEventArgs e)
    {
        EditorPanel.Visibility = Visibility.Collapsed;
        Unbound?.Invoke();
    }

    private void DoneButton_Click(object sender, RoutedEventArgs e) => Commit();
}
