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
        Chip.Visibility = binding is null ? Visibility.Collapsed : Visibility.Visible;
        UnbindButton.Visibility = binding is null || Editor == PropertySchema.Editor.Binding ? Visibility.Collapsed : Visibility.Visible;
        FormatRow.Visibility = Editor == PropertySchema.Editor.Text ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(Search, $"Search values for {PropertyLabel}");
        AutomationProperties.SetName(Matches, $"Values for {PropertyLabel}");
        AutomationProperties.SetName(FormatBox, $"Show {PropertyLabel} as");
        AutomationProperties.SetName(UnbindButton, $"Unbind {PropertyLabel}");
        AutomationProperties.SetName(DoneButton, "Done");
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
                var item = new ListBoxItem { Content = text, Tag = e };
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
    }

    private string? SelectedFormat => Editor == PropertySchema.Editor.Text ? (FormatBox.SelectedItem as ComboBoxItem)?.Tag as string : null;

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

    private void DoneButton_Click(object sender, RoutedEventArgs e) => EndEdit();
}
