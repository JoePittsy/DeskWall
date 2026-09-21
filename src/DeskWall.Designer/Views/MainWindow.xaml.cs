using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using DeskWall.Core;
using DeskWall.Core.Display;
using DeskWall.Core.Layout;
using DeskWall.Core.Sources;
using DeskWall.Core.Values;
using DeskWall.Designer.Model;
using DeskWall.Designer.Model.Widgets;
using CRect = DeskWall.Core.Rect;

namespace DeskWall.Designer.Views;

/// <summary>
/// The window.
/// <para>
/// Job: get a widget onto the column and looking right in under a minute, without the owner seeing
/// a coordinate or a binding. Three panes and a verb: pick from the gallery on the left, see it on
/// the wallpaper in the middle, change what it says on the right, Apply.
/// </para>
/// <para>
/// Deliberately left out: a menu bar; a display selector and a "copy from another display" button
/// (a layout belongs to the display in front of you, and the store scales the rest); a file name
/// with a dirty marker (Apply is enabled exactly when there is something to apply, which says the
/// same thing with no text); zoom, align, duplicate, bring-to-front and the rest of a drawing
/// program's verbs (a widget's position is the arranger's answer); panel collapse keys; a
/// confirmation for Apply. The status line at the foot says when it last reached the wallpaper and
/// whether the daemon is there to paint it.
/// </para>
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>What every starter layout uses, and what a brand new layout starts with: the one
    /// Spotlight asset that is on every Windows 11 install. Only a default - the Wallpaper panel
    /// changes it.</summary>
    public const string DefaultBaseImage =
        @"C:\Windows\SystemApps\MicrosoftWindows.Client.CBS_cw5n1h2txyewy\DesktopSpotlight\Assets\Images\image_3.jpg";

    private readonly LayoutStore _store;
    private readonly PreviewRenderer _renderer;
    private readonly DispatcherTimer _status = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer _galleryRefresh = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly IReadOnlyList<WidgetTemplate> _catalog;

    private Settings _settings;
    private DesignerModel _model = null!;
    private LiveSources? _live;
    private string _sourcesKey = "";
    private bool _allowClose;

    public MainWindow(LayoutStore store, Settings settings, DisplaySignature signature, LayoutResolution? resolution)
    {
        InitializeComponent();
        _store = store;
        _settings = settings;
        _catalog = WidgetCatalog.Load(WidgetCatalog.ShippedDir, WidgetCatalog.UserDir);
        _renderer = new PreviewRenderer(() => _live?.Tree() ?? ValueTree.Empty);

        Gallery.AddRequested += Add;
        Preview.Reordered += ReorderWidget;
        Preview.Moved += MoveUnlocked;
        Preview.IsUnlocked = id => _model.Layout.Widgets is { } w && w.TryGetValue(id, out var r) && r.Unlocked;
        Knobs.RemoveRequested += Remove;
        Knobs.ArrangeRequested += () => Arrange("Arrange");

        RestorePlacement();
        Open(signature, resolution);

        _status.Tick += (_, _) => RefreshStatus();
        _status.Start();
        _galleryRefresh.Tick += (_, _) => { _galleryRefresh.Stop(); Gallery.Refresh(); };
    }

    public DesignerModel Model => _model;

    // ---- opening ---------------------------------------------------------------------------------

    /// <summary>Point the window at the layout the daemon would paint on this display. What that is
    /// (and why a scaled resolution still opens the authored file, on the authored canvas) is
    /// <see cref="ShellState.OpenFrom"/>. No layout at all anywhere is not a dialog: an empty one is
    /// made here and the gallery is the first thing seen, which is the whole first-run story.</summary>
    private void Open(DisplaySignature signature, LayoutResolution? resolution)
    {
        if (_model is not null) _model.Changed -= OnModelChanged;

        var target = ShellState.OpenFrom(resolution, signature, LoadAuthored, DefaultBaseImage);
        _model = new DesignerModel(target.Layout, target.Signature, target.Path);
        _model.Changed += OnModelChanged;

        ShellState.CopyAssets(Path.Combine(AppContext.BaseDirectory, "assets", "weather"));

        Preview.Attach(_model, _renderer);
        Knobs.Attach(_model, _catalog);
        Gallery.Load(_catalog, () => _live?.Tree() ?? ValueTree.Empty);

        RebuildLiveSources();
        RefreshChrome();
        // The model's signature, not the monitor's: it is the one that resolves straight back to
        // this file if the shell cannot enumerate monitors next time.
        Remember(s => { s.LastSignatureKey = _model.Signature.Key; s.LastLayoutPath = _model.Path; });
    }

    /// <summary>The authored file behind a resolution, or null if it has become unreadable between
    /// the store reading it and now.</summary>
    private static LayoutFile? LoadAuthored(string path)
    {
        try { return LayoutFile.Load(path); }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException) { return null; }
    }

    private void OnModelChanged()
    {
        RebuildLiveSources();
        RefreshChrome();
    }

    private void RefreshChrome()
    {
        LayoutNameText.Text = LayoutLabel();
        LayoutNameText.ToolTip = _model.Path;
        DisplayText.Text = ShellState.DisplayLabel(_model.Signature.Key);
        UndoButton.IsEnabled = _model.CanUndo;
        RedoButton.IsEnabled = _model.CanRedo;
        ApplyButton.IsEnabled = _model.Path is null || _model.Dirty;
        Gallery.SetCounts(Counts());
        RefreshStatus();
    }

    /// <summary>What the top line calls the open layout: the name of the file being edited, ellipsed
    /// if it is long, with the full path on the tooltip. It said "Layout for this display" before,
    /// which is true of every layout it will ever open and so says nothing; the file name is the one
    /// fact that tells the owner whether the designer found the layout his desktop is showing.
    /// Only a layout with no file yet has no name.</summary>
    private string LayoutLabel() => _model.Path is null ? "New layout" : Path.GetFileName(_model.Path);

    private Dictionary<string, int> Counts()
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (_model.Layout.Widgets is not { } widgets) return counts;
        foreach (var record in widgets.Values)
            counts[record.Template] = counts.GetValueOrDefault(record.Template) + 1;
        return counts;
    }

    /// <summary>The running sources: the layout's, plus one of each source every catalogue widget
    /// wants. The extras are what makes a gallery card a real render rather than an empty field -
    /// the weather card cannot show a temperature unless something is fetching one -
    /// and the layout's own definition always wins on a name clash, so adding the widget changes
    /// nothing. Rebuilt only when the set actually differs: doing it on every knob turn would
    /// restart the weather fetch on each keystroke.</summary>
    private void RebuildLiveSources()
    {
        var defs = new List<SourceDef>(_model.Layout.Sources);
        foreach (var source in _catalog.SelectMany(t => t.Sources))
            if (!defs.Any(d => string.Equals(d.Name, source.Name, StringComparison.OrdinalIgnoreCase)))
                defs.Add(source);

        var key = string.Join(";", defs.Select(s =>
            $"{s.Name}|{s.Type}|{s.EverySeconds}|{string.Join(",", s.Settings.Select(kv => kv.Key + "=" + kv.Value))}"));
        if (key == _sourcesKey && _live is not null) return;
        _sourcesKey = key;

        var previous = _live;
        if (previous is not null) previous.Updated -= OnLiveUpdated;
        _live = new LiveSources(defs, Secrets.Default(), SystemClock.Instance);
        _live.Updated += OnLiveUpdated;
        Knobs.Live = _live;
        previous?.Dispose();
        _renderer.Request(_model);
    }

    /// <summary>A source published. The preview is cheap and goes at once; the gallery's eight
    /// renders wait until the flurry of first reads has settled.</summary>
    private void OnLiveUpdated() => Dispatcher.BeginInvoke(new Action(() =>
    {
        _renderer.Request(_model);
        _galleryRefresh.Stop();
        _galleryRefresh.Start();
    }));

    // ---- the four things that change a layout ------------------------------------------------------

    private void Add(WidgetTemplate template)
    {
        string? added = null;
        _model.Edit($"Add {template.Name}", l =>
        {
            // Dropped at the far end of the column so Order puts it last in its own anchor group;
            // Arrange then gives it its real place.
            var column = Column();
            added = WidgetInstance.Add(l, template, new CRect(column.X, column.Bottom, 0, 0));
            ArrangeIn(l);
        });
        if (added is not null) SelectInstance(added);
    }

    private void Remove(string instanceId)
    {
        _model.Edit("Remove widget", l =>
        {
            WidgetInstance.Remove(l, instanceId);
            ArrangeIn(l);
        });
        _model.ClearSelection();
    }

    private void ReorderWidget(string instanceId, int index)
    {
        if (!Reorder.Changes(Arranger.Order(_model.Layout), instanceId, index)) return;
        _model.Edit("Reorder", l => Arranger.Arrange(l, _catalog, Reorder.Move(Arranger.Order(l), instanceId, index),
            _model.Signature.Width, _model.Signature.Height));
        SelectInstance(instanceId);
    }

    /// <summary>A free move: a widget the arranger has been told to leave alone, or a loose component
    /// from a layout written before widgets existed, which the arranger never owned in the first
    /// place. Either way it is one undo entry.</summary>
    private void MoveUnlocked(string id, int dx, int dy)
    {
        var ids = WidgetInstance.Components(_model.Layout, id).Select(c => c.Id).ToList();
        if (ids.Count == 0 && _model.Find(id) is not null) ids.Add(id);
        if (ids.Count > 0) _model.Move(ids, dx, dy);
    }

    private void Arrange(string label) => _model.Edit(label, ArrangeIn);

    private void ArrangeIn(LayoutFile l)
        => Arranger.Arrange(l, _catalog, Arranger.Order(l), _model.Signature.Width, _model.Signature.Height);

    private CRect Column() => Arranger.Column(_model.Signature.Width, _model.Signature.Height);

    private void SelectInstance(string instanceId)
        => _model.Select(WidgetInstance.Components(_model.Layout, instanceId).Select(c => c.Id).ToList());

    // ---- commands ------------------------------------------------------------------------------------

    private void Undo_Click(object sender, RoutedEventArgs e) => _model.Undo();

    private void Redo_Click(object sender, RoutedEventArgs e) => _model.Redo();

    private void Apply_Click(object sender, RoutedEventArgs e) => Apply();

    /// <summary>Apply is save: write the layout to this display's path and register it, which is the
    /// only way the daemon ever picks it up. Its watcher repaints within two seconds.</summary>
    private bool Apply()
    {
        // A knob commits on LostFocus; Ctrl+S never moves focus, so without this the value being
        // typed is not in the document that gets written.
        Keyboard.ClearFocus();
        var created = _model.Path is null;
        var dest = _model.Path ?? ShellState.LayoutPathFor(_model.Signature);
        try
        {
            if (created)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                _model.Path = dest;
            }
            _model.Save();
            if (created) _store.Set(_model.Signature, dest);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            if (created) _model.Path = null;
            MessageBox.Show(this, $"Could not write {dest}: {ex.Message}", "DeskWall",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        _appliedAt = DateTime.Now;
        Remember(s => { s.LastSignatureKey = _model.Signature.Key; s.LastLayoutPath = _model.Path; });
        RefreshChrome();
        return true;
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var page = new SettingsPage(_model, _model.Signature, _store)
        {
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };
        page.ShowDialog();
        _settings = Settings.Load();
        RefreshChrome();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Handled) return;
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        // Not while a box is being typed into: Delete and Ctrl+Z belong to the text there.
        var typing = Keyboard.FocusedElement is System.Windows.Controls.TextBox;
        switch (e.Key)
        {
            case Key.S when ctrl: Apply(); e.Handled = true; break;
            case Key.Z when ctrl && !typing: _model.Undo(); e.Handled = true; break;
            case Key.Y when ctrl && !typing: _model.Redo(); e.Handled = true; break;
            case Key.Delete when !typing && SelectedInstance() is { } id: Remove(id); e.Handled = true; break;
            case Key.Escape when !typing: _model.ClearSelection(); e.Handled = true; break;
        }
    }

    private string? SelectedInstance()
        => _model is { Selection.Count: > 0 } ? _model.Find(_model.Selection[0])?.Widget : null;

    // ---- the status line ------------------------------------------------------------------------------

    private DateTime? _appliedAt;

    /// <summary>Two facts, read from the machine, never from a channel of our own: when this layout
    /// last reached disk, and whether the process that paints it is running. The daemon's own last
    /// error, when it has one, replaces both - it is the only thing worth reading then.</summary>
    private void RefreshStatus()
    {
        var applied = _appliedAt is { } at ? $"Applied {at:HH:mm}"
            : _model.Path is { } p && File.Exists(p) ? $"Applied {File.GetLastWriteTime(p):HH:mm}"
            : "Not applied yet";

        var running = false;
        var procs = Process.GetProcessesByName("deskwall");
        try { running = procs.Length > 0; }
        finally { foreach (var proc in procs) proc.Dispose(); }

        var error = LastDaemonError();
        StatusText.Text = error is not null
            ? $"{applied}  \u00b7  daemon: {error}"
            : string.Format(CultureInfo.InvariantCulture, "{0}  \u00b7  daemon {1}", applied, running ? "running" : "not running");
    }

    private static string? LastDaemonError()
    {
        var path = Paths.InRuntime("deskwall.log");
        try
        {
            if (!File.Exists(path)) return null;
            var last = File.ReadAllLines(path).LastOrDefault(l => l.Contains("[ERROR]", StringComparison.Ordinal));
            if (last is null) return null;
            var i = last.IndexOf("[ERROR]", StringComparison.Ordinal);
            return last[(i + 7)..].Trim();
        }
        catch (IOException) { return null; }
    }

    // ---- window placement and settings -------------------------------------------------------------------

    private void RestorePlacement()
    {
        if (ShellState.Placement(_settings.WindowLeft, _settings.WindowTop, _settings.WindowWidth, _settings.WindowHeight, MonitorBounds())
            is { } placement)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = placement.Left; Top = placement.Top; Width = placement.Width; Height = placement.Height;
        }
        else
        {
            Width = ShellState.DefaultWidth;
            Height = ShellState.DefaultHeight;
        }
        if (_settings.WindowMaximized) WindowState = WindowState.Maximized;
    }

    private static IReadOnlyList<CRect> MonitorBounds()
    {
        try { return Monitors.Enumerate().Select(m => m.Bounds).ToList(); }
        catch (Exception) { return Array.Empty<CRect>(); }
    }

    /// <summary>Read-modify-write, every time: the settings page writes the same file and the daemon
    /// reads it, so the window must never push a stale whole-file copy back over either.</summary>
    private void Remember(Action<Settings> mutate)
    {
        try
        {
            var settings = Settings.Load();
            mutate(settings);
            settings.Save();
            _settings = settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing the window position is not worth a dialog, let alone failing the edit.
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!_allowClose && !ConfirmDiscard()) { e.Cancel = true; return; }
        _allowClose = true;
        var bounds = RestoreBounds;
        Remember(s =>
        {
            s.WindowMaximized = WindowState == WindowState.Maximized;
            if (bounds.Width > 0 && bounds.Height > 0)
            {
                s.WindowLeft = bounds.Left; s.WindowTop = bounds.Top;
                s.WindowWidth = bounds.Width; s.WindowHeight = bounds.Height;
            }
        });
    }

    private bool ConfirmDiscard()
    {
        if (!_model.Dirty && _model.Path is not null) return true;
        if (!_model.Dirty) return true;
        var answer = MessageBox.Show(this, "Apply the changes before closing?", "DeskWall",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return answer switch
        {
            MessageBoxResult.Yes => Apply(),
            MessageBoxResult.No => true,
            _ => false,
        };
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _status.Stop();
        _galleryRefresh.Stop();
        _renderer.Dispose();
        _live?.Dispose();
        Application.Current?.Shutdown();
    }
}
