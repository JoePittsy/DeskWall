using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using DeskWall.Core;
using DeskWall.Core.Display;
using DeskWall.Core.Layout;
using DeskWall.Core.Values;
using DeskWall.Designer.Model;
using CRect = DeskWall.Core.Rect;

namespace DeskWall.Designer.Views;

/// <summary>
/// The shell.
/// <para>
/// Job: keep the right layout file open for the right display, and get an edit from the canvas to
/// the wallpaper in one keystroke.
/// </para>
/// <para>
/// That sentence is what adjudicates. Deliberately left out: a menu bar (every command it would
/// hold is a toolbar button, a canvas gesture or a key); a status bar (the canvas has one, and a
/// second would repeat it); undo, redo, zoom and align buttons (they live on the canvas and its
/// context menu, and copying them here would turn the toolbar into a list of everything the app can
/// do instead of the four things the shell decides); a Save-As dialog and a recent-files list (a
/// layout belongs to a display, so where it is written is derived, not chosen); an empty state (with
/// no layout the shell does not open at all, FirstRun does); icons on buttons, separators, group
/// boxes and panel headings; splitters and dockable panels; a confirmation dialog for Apply and any
/// progress indicator (Apply writes one file, and the wallpaper changing is the confirmation).
/// </para>
/// <para>
/// The toolbar carries four facts, each once: the layout file name with a dirty marker (without it,
/// nothing on screen says whether what you see has reached disk); the display this layout is for
/// (the preview of a scaled layout looks identical, so nothing else can tell you); the
/// "scaled from ..." banner and its one button (without it you would silently edit a stretched copy
/// of another display's layout and Apply it as this one's); and, in grey, "F9 sources F10 properties
/// F11 layers" - the one label here that is not a control, because the collapse keys have no other
/// affordance and without it the feature does not exist.
/// </para>
/// </summary>
public partial class MainWindow : Window
{
    private const double SourcesWidth = 280;
    private const double PropertiesWidth = 320;

    private readonly LayoutStore _store;
    private readonly PreviewRenderer _renderer;

    private Settings _settings;
    private DesignerModel _model = null!;
    private LiveSources? _live;
    private string? _scaledFrom;
    private bool _syncingCombo;
    private bool _allowClose;

    public MainWindow(LayoutStore store, Settings settings, DisplaySignature signature, LayoutResolution resolution)
    {
        InitializeComponent();
        _store = store;
        _settings = settings;

        // The preview always resolves against whatever the sources have produced most recently;
        // LiveSources is owned by SourcesPanel and replaced whenever the layout's source list
        // changes, so this reads the field rather than capturing an instance.
        _renderer = new PreviewRenderer(() => _live?.Tree() ?? ValueTree.Empty);

        Sources.LiveSourcesChanged += OnLiveSourcesChanged;
        Layers.TemplateChildActivated += OnTemplateChildActivated;

        RestoreWindowPlacement();
        ApplyPanelVisibility();
        Open(signature, resolution);
    }

    public DesignerModel Model => _model;

    // ---- opening a layout --------------------------------------------------------------------

    /// <summary>Point the whole shell at one layout for one display. A scaled resolution is opened
    /// with no path: it came out of another display's file and must never be written back over it,
    /// so Apply has to create this display's own layout first (which is what the banner says).</summary>
    private void Open(DisplaySignature signature, LayoutResolution resolution)
    {
        if (_model is not null) _model.Changed -= OnModelChanged;

        _scaledFrom = resolution.Scaled ? resolution.SourceSignature.Key : null;
        _model = new DesignerModel(resolution.Layout, signature, resolution.Scaled ? null : resolution.SourcePath);
        _model.Changed += OnModelChanged;

        Sources.Attach(_model);      // raises LiveSourcesChanged, which feeds the renderer and the properties panel
        Properties.Attach(_model);
        Layers.Attach(_model);
        Editor.Attach(_model, _renderer);

        RefreshDisplayCombo();
        RefreshToolbar();
        Remember(s => { s.LastSignatureKey = signature.Key; s.LastLayoutPath = _model.Path; });
    }

    private void OnModelChanged() => RefreshToolbar();

    private void OnLiveSourcesChanged(LiveSources live)
    {
        if (ReferenceEquals(_live, live)) return;
        if (_live is not null) _live.Updated -= OnLiveUpdated;
        _live = live;
        _live.Updated += OnLiveUpdated;
        Properties.Live = live;
        if (_model is not null) _renderer.Request(_model);
    }

    /// <summary>A source published new values: the preview is now out of date even though nothing
    /// was edited. Raised off the UI thread, and Request reads the model, so it marshals first.</summary>
    private void OnLiveUpdated() => Dispatcher.BeginInvoke(new Action(() =>
    {
        if (_model is not null) _renderer.Request(_model);
    }));

    /// <summary>The layers panel can select a repeater's template child, which has no rect of its
    /// own on the canvas. The properties panel edits it; the canvas outlines the repeater it lives
    /// in, so "where is that" still has an answer on screen.</summary>
    private void OnTemplateChildActivated(ComponentDef child, RepeaterDef parent)
    {
        Properties.ShowTemplateChild(child, parent);
        Editor.HighlightComponent(parent.Id);
    }

    // ---- toolbar ------------------------------------------------------------------------------

    private void RefreshToolbar()
    {
        FileText.Text = ShellState.FileLabel(_model.Path, _model.Dirty);
        ApplyButton.IsEnabled = _model.Path is null || _model.Dirty;
        RevertButton.IsEnabled = _model.Dirty;

        if (_scaledFrom is null) Banner.Visibility = Visibility.Collapsed;
        else
        {
            BannerText.Text = ShellState.BannerText(_scaledFrom);
            Banner.Visibility = Visibility.Visible;
        }

        // "Copy from..." is only an answer when this display has no layout of its own to copy over.
        CopyFromButton.Visibility = _store.Entries.ContainsKey(_model.Signature.Key)
            ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RefreshDisplayCombo()
    {
        _syncingCombo = true;
        try
        {
            var choices = ShellState.DisplayChoices(MonitorKeys(), _store.Entries.Keys).ToList();
            if (!choices.Any(c => string.Equals(c.Key, _model.Signature.Key, StringComparison.OrdinalIgnoreCase)))
                choices.Insert(0, new DisplayChoice(_model.Signature.Key, ShellState.DisplayLabel(_model.Signature.Key)));
            DisplayCombo.ItemsSource = choices;
            DisplayCombo.SelectedItem = choices.First(c => string.Equals(c.Key, _model.Signature.Key, StringComparison.OrdinalIgnoreCase));
        }
        finally { _syncingCombo = false; }
    }

    private static IReadOnlyList<string> MonitorKeys()
    {
        // Enumerate goes through IDesktopWallpaper; a shell that is mid-restart hands back a COM
        // failure, and a designer that cannot list monitors must still edit the layout it has open.
        try { return Monitors.Enumerate().Select(m => m.Signature.Key).ToList(); }
        catch (Exception) { return Array.Empty<string>(); }
    }

    // ---- commands -----------------------------------------------------------------------------

    private void Apply_Click(object sender, RoutedEventArgs e) => Apply();

    private void SaveForDisplay_Click(object sender, RoutedEventArgs e) => Apply();

    /// <summary>Apply is save. With no path (a scaled layout, or a starter that has not been written
    /// yet) it first creates this display's own layout file and registers it, which is the only way
    /// the daemon will ever pick it up.</summary>
    private bool Apply()
    {
        // Property edits commit on LostFocus; Ctrl+S never moves focus, so without this the value
        // being typed is not in the document that gets written (and the dirty marker clears).
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
            _scaledFrom = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            if (created) _model.Path = null;
            MessageBox.Show(this, $"Could not write {dest}: {ex.Message}", "DeskWall",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        Remember(s => { s.LastSignatureKey = _model.Signature.Key; s.LastLayoutPath = _model.Path; });
        RefreshDisplayCombo();
        RefreshToolbar();
        return true;
    }

    private void Revert_Click(object sender, RoutedEventArgs e) => _model.RevertToSaved();

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var page = new SettingsPage(_model, _model.Signature, _store)
        {
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };
        page.ShowDialog();
        // The page can have written settings.json (tray, start at logon) and the layout store
        // (remove an entry, use this layout for the current display).
        _settings = Settings.Load();
        RefreshDisplayCombo();
        RefreshToolbar();
    }

    /// <summary>Take another display's layout, scale it to this one and make it this display's own.
    /// The store is the only list of candidates there is, so it is the menu.</summary>
    private void CopyFrom_Click(object sender, RoutedEventArgs e)
    {
        var others = _store.Entries
            .Where(kv => !string.Equals(kv.Key, _model.Signature.Key, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (others.Count == 0)
        {
            MessageBox.Show(this, "No other display has a layout to copy.", "DeskWall",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var menu = new ContextMenu { PlacementTarget = CopyFromButton, Placement = PlacementMode.Bottom };
        foreach (var (key, path) in others)
        {
            var item = new MenuItem { Header = $"{ShellState.DisplayLabel(key)}   {Path.GetFileName(path)}", Tag = key };
            item.Click += CopyFromItem_Click;
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void CopyFromItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string sourceKey }) return;
        if (!ConfirmDiscard()) return;
        var dest = ShellState.LayoutPathFor(_model.Signature);
        try
        {
            var sourcePath = _store.Entries[sourceKey];
            var scaled = LayoutScaler.Scale(LayoutFile.Load(sourcePath), DisplaySignature.Parse(sourceKey), _model.Signature);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            scaled.Save(dest);
            _store.Set(_model.Signature, dest);
            Open(_model.Signature, new LayoutResolution(scaled, dest, _model.Signature, Scaled: false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException
                                      or System.Text.Json.JsonException or KeyNotFoundException)
        {
            MessageBox.Show(this, $"Could not copy that layout: {ex.Message}", "DeskWall",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DisplayCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingCombo || DisplayCombo.SelectedItem is not DisplayChoice choice) return;
        if (string.Equals(choice.Key, _model.Signature.Key, StringComparison.OrdinalIgnoreCase)) return;

        DisplaySignature signature;
        try { signature = DisplaySignature.Parse(choice.Key); }
        catch (FormatException) { RefreshDisplayCombo(); return; }

        if (!ConfirmDiscard()) { RefreshDisplayCombo(); return; }

        var resolution = _store.Resolve(signature);
        if (resolution is null)
        {
            var first = new FirstRun(signature, _store) { Owner = this };
            if (first.ShowDialog() != true) { RefreshDisplayCombo(); return; }
            resolution = _store.Resolve(signature);
            if (resolution is null) { RefreshDisplayCombo(); return; }
        }
        Open(signature, resolution);
    }

    /// <summary>Yes/No/Cancel over unsaved edits. True means "carry on".</summary>
    private bool ConfirmDiscard()
    {
        if (!_model.Dirty) return true;
        var answer = MessageBox.Show(this,
            $"Apply the changes to {ShellState.FileLabel(_model.Path, dirty: false)} first?",
            "DeskWall", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return answer switch
        {
            MessageBoxResult.Yes => Apply(),
            MessageBoxResult.No => true,
            _ => false,
        };
    }

    // ---- keyboard -------------------------------------------------------------------------------

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Handled) return;
        // F10 arrives as a system key (it would otherwise open a menu bar this window does not have).
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        switch (key)
        {
            case Key.S when (Keyboard.Modifiers & ModifierKeys.Control) != 0:
                Apply(); e.Handled = true; break;
            case Key.F9:
                TogglePanel(p => p.ShowSources = !p.ShowSources); e.Handled = true; break;
            case Key.F10:
                TogglePanel(p => p.ShowProperties = !p.ShowProperties); e.Handled = true; break;
            case Key.F11:
                TogglePanel(p => p.ShowLayers = !p.ShowLayers); e.Handled = true; break;
        }
    }

    private void TogglePanel(Action<Settings> toggle)
    {
        toggle(_settings);
        ApplyPanelVisibility();
        // Write the resulting state, not the toggle: re-running a flip against whatever is on disk
        // would land on the opposite answer if the file had moved under us.
        bool sources = _settings.ShowSources, properties = _settings.ShowProperties, layers = _settings.ShowLayers;
        Remember(s => { s.ShowSources = sources; s.ShowProperties = properties; s.ShowLayers = layers; });
    }

    private void ApplyPanelVisibility()
    {
        SourcesColumn.Width = new GridLength(_settings.ShowSources ? SourcesWidth : 0);
        SourcesHost.Visibility = _settings.ShowSources ? Visibility.Visible : Visibility.Collapsed;
        PropertiesColumn.Width = new GridLength(_settings.ShowProperties ? PropertiesWidth : 0);
        PropertiesHost.Visibility = _settings.ShowProperties ? Visibility.Visible : Visibility.Collapsed;
        LayersHost.Visibility = _settings.ShowLayers ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- window placement and settings ----------------------------------------------------------

    private void RestoreWindowPlacement()
    {
        if (_settings.WindowLeft is { } left && _settings.WindowTop is { } top &&
            _settings.WindowWidth is { } width && _settings.WindowHeight is { } height &&
            ShellState.OnScreen(left, top, width, height, MonitorBounds()))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left; Top = top; Width = width; Height = height;
        }
        if (_settings.WindowMaximized) WindowState = WindowState.Maximized;
    }

    private static IReadOnlyList<CRect> MonitorBounds()
    {
        try { return Monitors.Enumerate().Select(m => m.Bounds).ToList(); }
        catch (Exception) { return Array.Empty<CRect>(); }
    }

    /// <summary>Read-modify-write, every time: the settings page writes the same file, and the
    /// daemon reads it, so the shell must never push a stale whole-file copy back over either.</summary>
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

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _renderer.Dispose();
        _live?.Dispose();
        Application.Current?.Shutdown();
    }
}
