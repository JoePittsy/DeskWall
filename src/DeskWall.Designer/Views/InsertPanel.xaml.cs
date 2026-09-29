using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using DeskWall.Core;
using DeskWall.Core.Events;
using DeskWall.Core.Layout;
using DeskWall.Core.Sources;
using DeskWall.Core.Widgets;
using DeskWall.Designer.Model;
using DeskWall.Designer.Model.Widgets;

namespace DeskWall.Designer.Views;

/// <summary>
/// The Insert panel: what can go on the canvas. It replaces the widget gallery (brief section 3).
/// <para>
/// Job: put a part, a widget or a live value on the canvas in one gesture, without a dialog or a
/// binding path. Three sections: <b>Parts</b> (text, image, bar, dial: the widget editor's palette),
/// <b>Widgets</b> (the gallery's cards, unchanged, and still the only place to edit, duplicate, reset
/// or start a widget) and <b>Data</b> (every live value, "CPU load · 27%", searchable). Anything in
/// it can be dragged onto the canvas (<see cref="PreviewView"/> is the drop target), or inserted from
/// the keyboard: arrow keys move within a section and Enter puts a part or a value at the middle of
/// the canvas, and a widget where a click puts it, in the right-hand margin.
/// </para>
/// <para>
/// The Data section runs its own sources (<see cref="Insert.DataSources"/>): the document's own plus
/// the local defaults (time, hardware, disks, system), so a value no source in the layout produces yet
/// can be dropped, and the drop adds the source. Pushed providers come from <see cref="SetProviders"/>.
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
    private string _sourcesKey = "";
    private IReadOnlyList<ProviderRecord> _providers = [];
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
        Unloaded += (_, _) => { _live?.Dispose(); _live = null; _sourcesKey = ""; };
        Loaded += (_, _) => RebuildSources();
    }

    // ---- the gallery's hosting surface, unchanged -----------------------------------------------

    /// <summary>The owner clicked a widget card (or pressed Enter on it). The shell owns the layout,
    /// so the panel only names the widget; the shell puts it in the right-hand margin.</summary>
    public event Action<WidgetTemplate>? AddRequested;

    /// <summary>Build one from scratch.</summary>
    public event Action? NewRequested;

    /// <summary>Open a template in the widget editor.</summary>
    public event Action<WidgetTemplate>? EditRequested;

    /// <summary>Open a copy of this template, shipped or not, as a new unsaved widget.</summary>
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

    /// <summary>The open document (the Data section runs its sources, and follows depth) and the canvas
    /// that keyboard inserts go to. Call again for every document.</summary>
    public void Attach(DesignerModel model, PreviewView canvas)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(canvas);
        if (_model is not null) { _model.Changed -= RebuildSources; _model.DepthChanged -= RebuildSources; }
        _model = model;
        _canvas = canvas;
        _model.Changed += RebuildSources;
        _model.DepthChanged += RebuildSources;
        RebuildSources();
    }

    /// <summary>The pushed providers the shell knows about, so their values are rows too.</summary>
    public void SetProviders(IEnumerable<ProviderRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        _providers = records.ToList();
        _live?.SetProviders(_providers);
    }

    /// <summary>Rebuilt only when the set of definitions differs, so a knob turn does not restart
    /// the sources (the same rule as the shell's own <c>RebuildLiveSources</c>).</summary>
    private void RebuildSources()
    {
        if (_model is null || !IsLoaded) return;
        var defs = Insert.DataSources(_model);
        var key = string.Join(";", defs.Select(s =>
            $"{s.Name}|{s.Type}|{s.EverySeconds}|{string.Join(",", s.Settings.Select(kv => kv.Key + "=" + kv.Value))}"));
        if (key == _sourcesKey && _live is not null) return;
        _sourcesKey = key;
        _defs = defs;
        var previous = _live;
        if (previous is not null) previous.Updated -= OnUpdated;
        // ponytail: a second hardware sampler beside the shell's own LiveSources while the designer
        // is open. Share the shell's if the cost ever shows up; it would need the defaults added there.
        _live = new LiveSources(defs, Secrets.Default(), SystemClock.Instance);
        _live.Updated += OnUpdated;
        _live.SetProviders(_providers);
        previous?.Dispose();
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
        var entries = ValueCatalog.From(_live.Tree(), _defs);
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

    /// <summary>An open section takes a share of the height; a closed one only its header.</summary>
    private void Section_Toggled(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || DataSection is null) return;
        Sections.RowDefinitions[1].Height = WidgetsSection.IsExpanded ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        Sections.RowDefinitions[2].Height = DataSection.IsExpanded ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
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

        public bool Matches(string? query)
            => string.IsNullOrWhiteSpace(query)
               || query.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(w =>
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
        /// for a shipped template he has never edited. Edit is offered on everything (a shipped
        /// one saves a copy under the same key, <see cref="WidgetDocument.ForEditing"/>).</summary>
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
