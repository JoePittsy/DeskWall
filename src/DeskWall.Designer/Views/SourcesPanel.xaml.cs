using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using DeskWall.Core.Layout;
using DeskWall.Core.Sources;
using DeskWall.Designer.Model;

namespace DeskWall.Designer.Views;

/// <summary>Job: show which of the layout's sources are alive right now (name, type, refresh age,
/// a red dot when failed) and let the owner add, remove or force-refresh one; selecting a row shows
/// its live value tree, the same tree the binding picker reads from. Leaves out: no per-type icons,
/// no health colour beyond the one red dot (nothing to say = nothing drawn), no progress spinners,
/// no card border around each row, no section header repeating "Sources".</summary>
public partial class SourcesPanel : UserControl
{
    private static readonly Brush ErrorBrush = new SolidColorBrush(Color.FromRgb(0xD1, 0x34, 0x38));

    private DesignerModel? _model;
    private LiveSources? _live;
    private string? _liveKey;
    private readonly DispatcherTimer _ageTimer;

    public SourcesPanel()
    {
        InitializeComponent();
        _ageTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _ageTimer.Tick += (_, _) => RefreshRows();
        _ageTimer.Start();
        Unloaded += (_, _) =>
        {
            _ageTimer.Stop();
            if (_model is not null) _model.Changed -= OnModelChanged;
            if (_live is not null) { _live.Updated -= OnLiveUpdated; _live.Dispose(); }
        };
    }

    /// <summary>The live value tree of the currently selected source, or the empty tree. Handy for
    /// hosts that also want to feed a BindingPicker from the same data (the tree there is scoped
    /// to all sources, not just the selection, so this is for this panel's own display only).</summary>
    public LiveSources? Live => _live;

    public void Attach(DesignerModel model)
    {
        if (_model is not null) _model.Changed -= OnModelChanged;
        _model = model;
        _model.Changed += OnModelChanged;
        SyncLiveSources();
        RefreshRows();
    }

    private void OnModelChanged()
    {
        SyncLiveSources();
        RefreshRows();
    }

    private void OnLiveUpdated() => Dispatcher.Invoke(RefreshRows);

    private void SyncLiveSources()
    {
        if (_model is null) return;
        var key = SourcesKey(_model.Layout.Sources);
        if (key == _liveKey) return;
        _liveKey = key;
        if (_live is not null) { _live.Updated -= OnLiveUpdated; _live.Dispose(); }
        _live = new LiveSources(_model.Layout.Sources.ToList(), Secrets.Default(), SystemClock.Instance);
        _live.Updated += OnLiveUpdated;
        LiveSourcesChanged?.Invoke(_live);
    }

    /// <summary>Raised whenever the Sources list changes and LiveSources is re-created, so a host
    /// (or another panel, such as PropertiesPanel.Live) can pick up the new instance.</summary>
    public event Action<LiveSources>? LiveSourcesChanged;

    private static string SourcesKey(IEnumerable<SourceDef> defs) => string.Join("|", defs.Select(d =>
        $"{d.Name}:{d.Type}:{d.EverySeconds}:{string.Join(",", d.Settings.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "=" + kv.Value))}"));

    private void RefreshRows()
    {
        if (_model is null) return;
        var selectedName = (List.SelectedItem as FrameworkElement)?.Tag as string;
        List.Items.Clear();
        var now = SystemClock.Instance.Now;
        foreach (var def in _model.Layout.Sources)
        {
            var snap = _live?.Snapshots.FirstOrDefault(s => string.Equals(s.Name, def.Name, StringComparison.OrdinalIgnoreCase)) ?? SourceSnapshot.Initial(def.Name);
            var row = BuildRow(def, snap, now);
            List.Items.Add(row);
            if (string.Equals(def.Name, selectedName, StringComparison.OrdinalIgnoreCase)) List.SelectedItem = row;
        }
        UpdateValueTree();
    }

    private static FrameworkElement BuildRow(SourceDef def, SourceSnapshot snap, DateTimeOffset now)
    {
        var grid = new Grid { Tag = def.Name, Margin = new Thickness(4, 2, 4, 2), HorizontalAlignment = HorizontalAlignment.Stretch };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });

        var failed = snap.LastError is not null;
        var dot = new Ellipse { Width = 8, Height = 8, Fill = ErrorBrush, Visibility = failed ? Visibility.Visible : Visibility.Hidden };
        if (failed) dot.ToolTip = snap.LastError;
        Grid.SetColumn(dot, 0);

        var name = new TextBlock { Text = def.Name, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(name, 1);

        var type = new TextBlock { Text = def.Type, VerticalAlignment = VerticalAlignment.Center, Foreground = SystemColors.GrayTextBrush };
        Grid.SetColumn(type, 2);

        var age = new TextBlock { Text = FormatAge(snap.LastRefresh, now), VerticalAlignment = VerticalAlignment.Center, Foreground = SystemColors.GrayTextBrush, HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetColumn(age, 3);

        grid.Children.Add(dot);
        grid.Children.Add(name);
        grid.Children.Add(type);
        grid.Children.Add(age);
        return grid;
    }

    private static string FormatAge(DateTimeOffset? last, DateTimeOffset now)
    {
        if (last is null) return "never";
        var s = Math.Max(0, (int)(now - last.Value).TotalSeconds);
        if (s < 60) return $"{s} s ago";
        var m = s / 60;
        if (m < 60) return $"{m} m ago";
        return $"{m / 60} h ago";
    }

    private void UpdateValueTree()
    {
        var name = (List.SelectedItem as FrameworkElement)?.Tag as string;
        var snap = name is null ? null : _live?.Snapshots.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
        if (snap?.Values is null) { ValueTree.Items.Clear(); return; }
        ValueTreeView.Populate(ValueTree, snap.Values, null);
    }

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateValueTree();

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        if (_model is null) return;
        var editor = new SourceEditor { Owner = Window.GetWindow(this) };
        if (editor.ShowDialog() == true && editor.Result is { } def)
            _model.Edit("Add source", l => l.Sources.Add(def));
    }

    private void RemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_model is null) return;
        if ((List.SelectedItem as FrameworkElement)?.Tag is not string name) return;
        _model.Edit("Remove source", l => l.Sources.RemoveAll(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)));
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if ((List.SelectedItem as FrameworkElement)?.Tag is not string name || _live is null) return;
        await _live.RefreshNowAsync(name);
    }

    private void List_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_model is null) return;
        if ((List.SelectedItem as FrameworkElement)?.Tag is not string name) return;
        var existing = _model.Layout.Sources.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is null) return;
        var editor = new SourceEditor(existing) { Owner = Window.GetWindow(this) };
        if (editor.ShowDialog() == true && editor.Result is { } updated)
            _model.Edit("Edit source", l =>
            {
                var idx = l.Sources.FindIndex(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0) l.Sources[idx] = updated;
            });
    }
}
