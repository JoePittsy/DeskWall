using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using DeskWall.Core;
using DeskWall.Core.Layout;
using DeskWall.Core.Sources;
using DeskWall.Core.Values;
using DeskWall.Core.Widgets;
using DeskWall.Designer.Model;
using DeskWall.Designer.Model.Widgets;

namespace DeskWall.Designer.Views;

/// <summary>
/// The Insert panel: what can go on the canvas. It replaces the widget gallery (brief section 3).
/// <para>
/// Job: put a part, a widget or a live value on the canvas in one gesture, without a dialog or a
/// binding path. Three sections: <b>Parts</b> (text, image, bar, dial), <b>Widgets</b> (a card per
/// widget; its menu edits, duplicates, resets or starts one, all on the main canvas at widget depth)
/// and <b>Data</b> (every live value, "CPU load · 27%", searchable). Anything in
/// it can be dragged onto the canvas (<see cref="PreviewView"/> is the drop target), or inserted from
/// the keyboard: arrow keys move within a section and Enter puts a part or a value at the middle of
/// the canvas, and a widget where a click puts it, in the right-hand margin.
/// </para>
/// <para>
/// The Data section lists the values of <see cref="Insert.DataSources"/>: the document's own plus
/// the local defaults (time, hardware, disks, system), so a value no source in the layout produces yet
/// can be dropped, and the drop adds the source. It reads them from the window's one running set
/// (<see cref="Live"/>), which includes those defaults and the pushed providers.
/// Deliberately left out: grouping the values by source (the labels already say "CPU", "C:" and so
/// on, and the search box finds the rest), and a preview picture per widget card (see the gallery's
/// old notes: eight grey rectangles told the owner less than the names).
/// </para>
/// </summary>
public partial class InsertPanel : UserControl
{
    /// <summary>The drag format. The payload is the object itself (in-process): a
    /// <see cref="PartKind"/>, a <see cref="WidgetTemplate"/> or a <see cref="ValueEntry"/>.</summary>
    public const string DataFormat = "DeskWall.Insert";

    private readonly ObservableCollection<CardView> _cards = new();
    private readonly ObservableCollection<DataRow> _rows = new();
    private readonly ICollectionView _rowView;
    private IReadOnlyList<WidgetTemplate> _templates = Array.Empty<WidgetTemplate>();

    private DesignerModel? _model;
    private PreviewView? _canvas;
    private LiveSources? _live;
    private IReadOnlyList<SourceDef> _defs = [];
    private bool _refreshQueued;

    private Point _pressAt;
    private FrameworkElement? _pressed;

    public InsertPanel()
    {
        InitializeComponent();
        Cards.ItemsSource = _cards;
        Rows.ItemsSource = _rows;
        _rowView = CollectionViewSource.GetDefaultView(_rows);
        _rowView.Filter = o => o is DataRow r && r.Matches(SearchBox.Text);
    }

    // ---- the widget cards ------------------------------------------------------------------------

    /// <summary>The owner clicked a widget card (or pressed Enter on it). The shell owns the layout,
    /// so the panel only names the widget; the shell puts it in the right-hand margin.</summary>
    public event Action<WidgetTemplate>? AddRequested;

    /// <summary>Build one from scratch: an empty frame at widget depth.</summary>
    public event Action? NewRequested;

    /// <summary>Edit the widget at widget depth; every copy follows.</summary>
    public event Action<WidgetTemplate>? EditRequested;

    /// <summary>A new widget from this one, shipped or not, under a key of its own.</summary>
    public event Action<WidgetTemplate>? DuplicateRequested;

    /// <summary>Delete (or reset) one of the owner's own template files.</summary>
    public event Action<WidgetTemplate>? DeleteRequested;

    /// <summary>Fill the Widgets section.</summary>
    public void Load(IReadOnlyList<WidgetTemplate> templates)
    {
        _templates = templates;
        _cards.Clear();
        foreach (var t in templates) _cards.Add(new CardView(t));
        EmptyNote.Visibility = templates.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>How many of each widget are on the wallpaper now, for the count badge.</summary>
    public void SetCounts(IReadOnlyDictionary<string, int> byKey)
    {
        foreach (var card in _cards) card.Count = byKey.TryGetValue(card.Key, out var n) ? n : 0;
    }

    // ---- new: the document, the canvas and the providers ----------------------------------------

    /// <summary>The open document (the Data section lists its sources, and follows depth) and the canvas
    /// that keyboard inserts go to. Call again for every document.</summary>
    public void Attach(DesignerModel model, PreviewView canvas)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(canvas);
        if (_model is not null) { _model.Changed -= OnDocumentChanged; _model.DepthChanged -= OnDocumentChanged; }
        _model = model;
        _canvas = canvas;
        _model.Changed += OnDocumentChanged;
        _model.DepthChanged += OnDocumentChanged;
        OnDocumentChanged();
    }

    /// <summary>The window's running sources, which must include every one of
    /// <see cref="Insert.DataSources"/>. The host replaces it whenever the set changes.</summary>
    public LiveSources? Live
    {
        get => _live;
        set
        {
            if (_live is not null) _live.Updated -= OnUpdated;
            _live = value;
            if (_live is not null) _live.Updated += OnUpdated;
            OnUpdated();
        }
    }

    private void OnDocumentChanged()
    {
        if (_model is null) return;
        _defs = Insert.DataSources(_model);
        OnUpdated();
    }

    /// <summary>Not on the UI thread, and many times a second at start-up: coalesced into one refresh.</summary>
    private void OnUpdated()
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        Dispatcher.BeginInvoke(new Action(() => { _refreshQueued = false; RefreshRows(); }),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>Updates the rows in place when the same values are there, so focus and the scroll
    /// position survive a refresh; rebuilds them when values came or went.</summary>
    private void RefreshRows()
    {
        if (_live is null) return;
        // The window runs more than this list (every catalogue widget's sources): only these are rows.
        var names = _defs.Select(d => d.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tree = new RecordValue(_live.Tree().Fields.Where(f => names.Contains(f.Key))
            .ToDictionary(f => f.Key, f => f.Value, StringComparer.OrdinalIgnoreCase));
        var entries = ValueCatalog.From(tree, _defs);
        if (entries.Count == _rows.Count && entries.Select(e => e.Path).SequenceEqual(_rows.Select(r => r.Path)))
        {
            for (var i = 0; i < entries.Count; i++) _rows[i].Entry = entries[i];
        }
        else
        {
            _rows.Clear();
            foreach (var e in entries) _rows.Add(new DataRow(e));
        }
        DataNote.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The rows as they are now (for the harness and tests).</summary>
    public IReadOnlyList<ValueEntry> Values => _rows.Select(r => r.Entry).ToList();

    // ---- sections ------------------------------------------------------------------------------

    /// <summary>Data's rows are one line each and its header carries a search box, so it takes the
    /// larger share (as the XAML starts it): at 1366x768 that is the difference between three rows
    /// and five.</summary>
    private const double WidgetsWeight = 1, DataWeight = 2.2;

    /// <summary>The least an open Widgets section is given: its header and one whole card (a
    /// two-line description), so the list never shows only slices of cards (critique 3, P2-c).</summary>
    public const double WidgetsFloor = 150;

    /// <summary>The least an open Data section is given: its header, the search box and three rows.</summary>
    public const double DataFloor = 176;

    /// <summary>What the whole panel needs at the least: Parts and both floors. The shell keeps this
    /// much of the column for it however far Layers is dragged.</summary>
    public const double Floor = 76 + WidgetsFloor + 6 + DataFloor;

    /// <summary>An open section takes a share of the height, down to its floor; a closed one only its
    /// header. The splitter only means something between two open sections.</summary>
    private void Section_Toggled(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || DataSection is null) return;
        WidgetsShare.Height = WidgetsSection.IsExpanded ? new GridLength(WidgetsWeight, GridUnitType.Star) : GridLength.Auto;
        WidgetsShare.MinHeight = WidgetsSection.IsExpanded ? WidgetsFloor : 0;
        DataShare.Height = DataSection.IsExpanded ? new GridLength(DataWeight, GridUnitType.Star) : GridLength.Auto;
        DataShare.MinHeight = DataSection.IsExpanded ? DataFloor : 0;
        SectionsSplitter.Visibility = WidgetsSection.IsExpanded && DataSection.IsExpanded ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _rowView.Refresh();
    }

    // ---- clicks and Enter ------------------------------------------------------------------------

    private void Card_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key }) return;
        if (_templates.FirstOrDefault(t => t.Key == key) is { } t) AddRequested?.Invoke(t);
    }

    /// <summary>A click or Enter on a part or a value: the middle of the canvas. A drag goes where it
    /// is dropped instead.</summary>
    private void Part_Click(object sender, RoutedEventArgs e)
    {
        if (Payload(sender) is { } payload) _canvas?.InsertAtCentre(payload);
    }

    private void Row_Click(object sender, RoutedEventArgs e)
    {
        if (Payload(sender) is { } payload) _canvas?.InsertAtCentre(payload);
    }

    private void New_Click(object sender, RoutedEventArgs e) => NewRequested?.Invoke();

    private void Edit_Click(object sender, RoutedEventArgs e) => Raise(sender, EditRequested);

    private void Duplicate_Click(object sender, RoutedEventArgs e) => Raise(sender, DuplicateRequested);

    private void Delete_Click(object sender, RoutedEventArgs e) => Raise(sender, DeleteRequested);

    /// <summary>A context-menu item's DataContext is the card it was opened on, which is the only
    /// way back to the template from inside the item template.</summary>
    private static void Raise(object sender, Action<WidgetTemplate>? handler)
    {
        if (sender is FrameworkElement { DataContext: CardView card }) handler?.Invoke(card.Template);
    }

    // ---- dragging --------------------------------------------------------------------------------

    /// <summary>What a source element inserts: a part by its tag, a widget card's template, a row's value.</summary>
    private static object? Payload(object sender) => sender switch
    {
        FrameworkElement { Tag: string tag } when Enum.TryParse<PartKind>(tag, out var kind) => kind,
        FrameworkElement { DataContext: CardView card } => card.Template,
        FrameworkElement { DataContext: DataRow row } => row.Entry,
        _ => null,
    };

    private void Source_Press(object sender, MouseButtonEventArgs e)
    {
        _pressed = sender as FrameworkElement;
        _pressAt = e.GetPosition(this);
    }

    /// <summary>Past the system drag distance a press becomes a drag. The button's own press is
    /// cancelled afterwards (its capture released), so the drop is not followed by a click.</summary>
    private void Source_Move(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || !ReferenceEquals(sender, _pressed)) return;
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - _pressAt.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(p.Y - _pressAt.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var source = _pressed;
        _pressed = null;
        if (Payload(source) is not { } payload) return;
        DragDrop.DoDragDrop(source, new DataObject(DataFormat, payload), DragDropEffects.Copy);
        source.ReleaseMouseCapture();
    }

    // ---- view models -----------------------------------------------------------------------------

    /// <summary>One value: "CPU load" and " · 27%", the sample in the value's first format preset.</summary>
    public sealed class DataRow(ValueEntry entry) : INotifyPropertyChanged
    {
        private ValueEntry _entry = entry;

        public ValueEntry Entry
        {
            get => _entry;
            set
            {
                if (Equals(_entry.Sample, value.Sample) && _entry.Label == value.Label) { _entry = value; return; }
                _entry = value;
                Raise(nameof(Sample)); Raise(nameof(Label)); Raise(nameof(SpokenName));
            }
        }

        public string Path => _entry.Path;
        public string Label => _entry.Label;
        public string Sample => " · " + SampleText(_entry);
        public string SpokenName => $"{_entry.Label}, {SampleText(_entry)}";

        public static string SampleText(ValueEntry e)
            => e.Sample.ToText(FormatPresets.For(e.Kind) is [var first, ..] ? first.Format : null);

        /// <summary>Everything but the diagnostic values while the box is empty; a search finds those too.</summary>
        public bool Matches(string? query)
            => string.IsNullOrWhiteSpace(query) ? !_entry.Diagnostic
               : query.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(w =>
                   Label.Contains(w, StringComparison.OrdinalIgnoreCase) || Path.Contains(w, StringComparison.OrdinalIgnoreCase));

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>One widget card. A view model rather than a hand-built visual tree, because the count
    /// badge changes on every add and remove and nothing else about the card does.</summary>
    public sealed class CardView : INotifyPropertyChanged
    {
        private int _count;

        public CardView(WidgetTemplate template) => Template = template;

        public WidgetTemplate Template { get; }
        public string Key => Template.Key;
        public string Name => Template.Name;
        public string Description => Template.Description;
        public string Requires => Template.Requires ?? "";
        public Visibility RequiresVisibility => Template.Requires is null ? Visibility.Collapsed : Visibility.Visible;

        public int Count
        {
            get => _count;
            set { if (_count == value) return; _count = value; Raise(); Raise(nameof(CountVisibility)); }
        }

        public Visibility CountVisibility => _count > 0 ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>Delete is for the owner's own files only: there is no file of his to remove
        /// for a shipped template he has never edited. Edit is offered on everything (Apply saves a
        /// shipped one's edits as a fork under the same key, plan D2).</summary>
        public Visibility MineVisibility => WidgetCatalog.IsUserTemplate(Template) ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>Deleting an override is undoing an edit, not losing a widget, and the menu
        /// has to say which of the two it is about to do.</summary>
        public string DeleteHeader => Template.OverridesShipped ? "Reset to the out-of-the-box version" : "Delete";

        public Visibility EditedVisibility =>
            Template.OverridesShipped && WidgetCatalog.IsUserTemplate(Template) ? Visibility.Visible : Visibility.Collapsed;

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Raise([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
