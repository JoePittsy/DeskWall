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
/// Job: get a widget onto the wallpaper, where the owner wants it and looking right, in under a
/// minute, without seeing a coordinate or a binding. Three panes and a verb: pick from the gallery
/// on the left, drag it about on the wallpaper in the middle, change what it says on the right, Apply.
/// </para>
/// <para>
/// Deliberately left out: a menu bar; a display selector and a "copy from another display" button
/// (a layout belongs to the display in front of you, and the store scales the rest); a file name
/// with a dirty marker (Apply is enabled exactly when there is something to apply, which says the
/// same thing with no text); duplicate, bring-to-front and the rest of a drawing program's verbs;
/// panel collapse keys; a confirmation for Apply. The status line at the foot says when it last
/// reached the wallpaper and whether the daemon is there to paint it.
/// </para>
/// <para>
/// Placement lives on the canvas, not up here: zoom, the grid, align and distribute are all
/// controls over the preview, where what they act on is visible (<see cref="PreviewView"/>). The
/// top bar keeps its four verbs and its six-control budget.
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

    private Settings _settings;
    private IReadOnlyList<WidgetTemplate> _catalog;
    private WidgetEditorWindow? _editor;
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
        Gallery.NewRequested += () => OpenWidgetEditor(WidgetDocument.New());
        Gallery.EditRequested += t => OpenWidgetEditor(WidgetDocument.FromTemplate(t, t.Path));
        Gallery.DuplicateRequested += DuplicateTemplate;
        Gallery.DeleteRequested += DeleteTemplate;
        Knobs.RemoveRequested += Remove;

        RestorePlacement();
        Open(signature, resolution);

        _status.Tick += (_, _) => RefreshStatus();
        _status.Start();
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
        Gallery.Load(_catalog);

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
    }));

    // ---- the four things that change a layout ------------------------------------------------------

    /// <summary>A widget picked from the gallery lands in the right-hand margin, below whatever is
    /// already there, and is free to drag from that moment
    /// (<see cref="Placement.Spawn"/>). Nothing is arranged, before or after: the canvas has no
    /// column any more, and where a widget sits is the owner's answer.
    /// <para>The margin rather than the middle of the canvas because windows sit centred on the
    /// ultrawide and leave roughly 440 px either side (CLAUDE.md); dropping a new widget behind a
    /// browser window would look like nothing had happened.</para></summary>
    private void Add(WidgetTemplate template)
    {
        string? added = null;
        _model.Edit($"Add {template.Name}", l =>
        {
            var region = Arranger.Column(_model.Signature.Width, _model.Signature.Height);
            var at = Placement.Spawn(region, Targets.All(l).Select(t => t.Bounds).ToList(), template.Width, template.Height);
            added = WidgetInstance.Add(l, template, new CRect(at.X, at.Y, 0, 0));
        });
        if (added is not null) SelectInstance(added);
    }

    private void Remove(string instanceId)
    {
        _model.Edit("Remove widget", l => WidgetInstance.Remove(l, instanceId));
        _model.ClearSelection();
    }

    /// <summary>Delete takes out everything selected, widgets and loose components alike, in one
    /// undo entry. One at a time would be a surprise now that three can be selected at once.</summary>
    private void RemoveSelection()
    {
        var targets = Targets.From(_model.Layout, _model.Selection);
        if (targets.Count == 0) return;
        _model.Edit(targets.Count > 1 ? "Remove widgets" : "Remove widget", l =>
        {
            foreach (var target in targets)
            {
                if (target.IsWidget) WidgetInstance.Remove(l, target.Id);
                else l.Components.RemoveAll(c => c.Id == target.Id);
            }
        });
        _model.ClearSelection();
    }

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

    // ---- the widget editor -------------------------------------------------------------------

    /// <summary>One editor at a time. A second request asks the open one whether to keep what is
    /// in it first: two windows editing two templates that may share a key is a race to the same
    /// file, and the gallery behind them can only show one answer.</summary>
    private void OpenWidgetEditor(WidgetDocument document)
    {
        if (_editor is not null)
        {
            if (!_editor.ConfirmDiscard()) { _editor.Activate(); return; }
            _editor.ForceClose();
        }
        var editor = new WidgetEditorWindow(document) { Owner = this };
        _editor = editor;
        editor.Saved += ReloadCatalog;
        editor.Closed += (_, _) => { if (ReferenceEquals(_editor, editor)) _editor = null; };
        editor.Show();
    }

    /// <summary>"Duplicate to mine": a copy, named apart from the original and not yet on disk, so
    /// the first Save cannot land on the shipped key it came from.</summary>
    private void DuplicateTemplate(WidgetTemplate template)
    {
        var document = WidgetDocument.FromTemplate(template, null);
        document.Name = template.Name + " copy";
        OpenWidgetEditor(document);
    }

    private void DeleteTemplate(WidgetTemplate template)
    {
        if (template.Path is null) return;
        var answer = MessageBox.Show(this,
            $"Delete the widget '{template.Name}'? Copies already on a wallpaper stay exactly as they are.",
            "DeskWall", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        try { File.Delete(template.Path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"Could not delete {template.Path}: {ex.Message}", "DeskWall",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        ReloadCatalog();
    }

    /// <summary>A template file changed on disk. Everything that reads the catalog is rebuilt from
    /// it: the gallery's cards, the knobs panel (an instance of the edited template shows its new
    /// knobs at once) and the running sources, which include one of each catalog source.</summary>
    private void ReloadCatalog()
    {
        _catalog = WidgetCatalog.Load(WidgetCatalog.ShippedDir, WidgetCatalog.UserDir);
        Gallery.Load(_catalog);
        Knobs.Attach(_model, _catalog);
        _sourcesKey = "";
        RebuildLiveSources();
        RefreshChrome();
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
            case Key.Delete when !typing: RemoveSelection(); e.Handled = true; break;
            case Key.Escape when !typing: _model.ClearSelection(); e.Handled = true; break;
        }
    }

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
        _renderer.Dispose();
        _live?.Dispose();
        Application.Current?.Shutdown();
    }
}
