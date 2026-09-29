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
using DeskWall.Core.Widgets;
using CRect = DeskWall.Core.Rect;

namespace DeskWall.Designer.Views;

/// <summary>
/// The window.
/// <para>
/// Job: get a widget onto the wallpaper, where the owner wants it and looking right, in under a
/// minute, without seeing a coordinate or a binding. Three panes and a verb: pick from the gallery
/// on the left (under Layers, which lists what is already placed), drag it about on the wallpaper
/// in the middle, change what it says on the right, Apply.
/// </para>
/// <para>
/// One canvas, three depths (brief section 3), named by the breadcrumb in the top bar. The keys
/// live in <see cref="OnPreviewKeyDown"/> so they work wherever the focus is, except in a text box:
/// Enter and Esc go down and up a depth (Esc climbs before it clears the selection); Tab and
/// Shift+Tab cycle siblings on the canvas; Ctrl+C, V, D and A; Ctrl+] and Ctrl+[ bring to front
/// and send to back; Ctrl+Alt+K makes a widget from loose parts or edits the selected one;
/// Shift+1, Shift+2, Ctrl+plus, Ctrl+minus and Ctrl+0 zoom.
/// </para>
/// <para>
/// Deliberately left out: a menu bar; a display selector and a "copy from another display" button
/// (a layout belongs to the display in front of you, and the store scales the rest); a file name
/// with a dirty marker (Apply is enabled exactly when there is something to apply, which says the
/// same thing with no text); menus or buttons for the keyboard verbs above (their tooltips name
/// them), one-step forward and backward, and the system clipboard; panel collapse keys; a
/// confirmation for Apply. The status line at the foot says when it last
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
    private string? _transient;
    private DateTime _transientUntil;
    private bool _allowClose;

    /// <summary>The open document is the in-memory v2 migration of a v1 file, and the first Apply
    /// has not happened yet: it writes <c>&lt;file&gt;.v1.json</c> before overwriting anything.</summary>
    private bool _backupBeforeApply;

    public MainWindow(LayoutStore store, Settings settings, DisplaySignature signature, LayoutResolution? resolution)
    {
        InitializeComponent();
        _store = store;
        _settings = settings;
        _catalog = WidgetCatalog.Load(WidgetCatalog.ShippedDir, WidgetCatalog.UserDir);
        _renderer = new PreviewRenderer(() => _live?.Tree() ?? ValueTree.Empty);

        Gallery.AddRequested += Add;
        Gallery.NewRequested += () => OpenWidgetEditor(WidgetDocument.New());
        Gallery.EditRequested += t => OpenWidgetEditor(WidgetDocument.ForEditing(t));
        Gallery.DuplicateRequested += DuplicateTemplate;
        Gallery.DeleteRequested += DeleteTemplate;
        Knobs.RemoveRequested += Remove;
        // The shell owns depth: the canvas asks, and the one path (the model) answers, so a
        // double-click, Enter, Esc and Ctrl+Alt+K cannot disagree about where they end up.
        Preview.DepthRequested += Preview.GoToDepth;
        Preview.EditWidgetRequested += copyId =>
        {
            if (Copies.Find(_model.Layout, copyId) is { } copy) Preview.GoToDepth(Depth.Widget(copy.Widget, copy.Id));
        };
        Layers.TemplateChildActivated += Knobs.ShowTemplateChild;
        Providers.Status += SetStatus;
        // The records go into LiveSources, not into a second tree of their own: the binding
        // picker, the preview and the value trees all read that one, so a provider that is not in
        // it is one the user cannot bind.
        Providers.ProvidersChanged += records => _live?.SetProviders(records);

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
        if (_model is not null) { _model.Changed -= OnModelChanged; _model.DepthChanged -= RefreshBreadcrumb; }

        var target = ShellState.OpenFrom(resolution, signature, LoadAuthored, DefaultBaseImage,
            WidgetCatalog.Finder(WidgetCatalog.ShippedDir, WidgetCatalog.UserDir));
        _model = new DesignerModel(target.Layout, target.Signature, target.Path);
        _model.Changed += OnModelChanged;
        _model.DepthChanged += RefreshBreadcrumb;
        _backupBeforeApply = target.Migrated;

        ShellState.CopyAssets(Path.Combine(AppContext.BaseDirectory, "assets", "weather"));

        Preview.Attach(_model, _renderer);
        Knobs.Attach(_model);
        Layers.Attach(_model);
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
        RefreshBreadcrumb();
        RefreshStatus();
    }

    /// <summary>"Layout", or "Layout › Hardware dial (copy)" / "(widget)": the widget's name,
    /// its key when it cannot be read.</summary>
    private void RefreshBreadcrumb()
    {
        var depth = _model.Depth;
        var name = depth.WidgetKey is { } key ? Copies.TryFind(_model.Finder(), key)?.Name ?? key : null;
        Breadcrumb.Text = depth.Kind switch
        {
            DepthKind.Copy => $"Layout \u203a {name} (copy)",
            DepthKind.Widget => $"Layout \u203a {name} (widget)",
            _ => "Layout",
        };
        System.Windows.Automation.AutomationProperties.SetName(Breadcrumb, "Editing: " + Breadcrumb.Text);
    }

    /// <summary>What the top line calls the open layout: the name of the file being edited, ellipsed
    /// if it is long, with the full path on the tooltip. It said "Layout for this display" before,
    /// which is true of every layout it will ever open and so says nothing; the file name is the one
    /// fact that tells the owner whether the designer found the layout his desktop is showing.
    /// Only a layout with no file yet has no name.</summary>
    private string LayoutLabel() => _model.Path is null ? "New layout" : Path.GetFileName(_model.Path);

    /// <summary>Copies per widget key in the open layout.</summary>
    private Dictionary<string, int> Counts()
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var copy in _model.Layout.Copies ?? [])
            counts[copy.Widget] = counts.GetValueOrDefault(copy.Widget) + 1;
        return counts;
    }

    /// <summary>The running sources: the expansion's (the layout's own, then every copy's, merged the
    /// way the daemon merges them), plus one of each source every catalogue widget wants. The extras
    /// are what makes a gallery card a real render rather than an empty field - the weather card
    /// cannot show a temperature unless something is fetching one - and the layout's own definition
    /// always wins on a name clash, so adding the widget changes nothing. Rebuilt only when the set
    /// actually differs: doing it on every knob turn would restart the weather fetch on each
    /// keystroke.</summary>
    private void RebuildLiveSources()
    {
        var defs = new List<SourceDef>(_model.Expanded().Layout.Sources);
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
        // A rebuilt set starts with no providers, so the ones already on screen have to be put
        // back or a bound component would fall back to its default on the next source edit.
        _live.SetProviders(Providers.Records);
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
        // Where it lands is read before the edit: the expansion is the model's, cached per change.
        var region = Arranger.Column(_model.Signature.Width, _model.Signature.Height);
        var at = Placement.Spawn(region, Targets.All(_model).Select(t => t.Bounds).ToList(), template.Width, template.Height);
        string? added = null;
        _model.Edit($"Add {template.Name}", l => added = Copies.Add(l, template, at.X, at.Y));
        if (added is not null) SelectCopy(added);
    }

    private void Remove(string copyId)
    {
        _model.Edit("Remove widget", l => Copies.Remove(l, copyId));
        _model.ClearSelection();
    }

    /// <summary>Delete takes out everything selected, widgets and loose components alike, in one
    /// undo entry. One at a time would be a surprise now that three can be selected at once.</summary>
    private void RemoveSelection()
    {
        // Deeper, the selection is parts: hidden on the copy, or gone from the widget. Mapping them
        // through the layout's targets would take out the whole copy they belong to.
        if (_model.Depth.Kind != DepthKind.Layout) { _model.Remove([.. _model.Selection]); return; }
        var targets = Targets.From(_model, _model.Selection);
        if (targets.Count == 0) return;
        _model.Edit(targets.Count > 1 ? "Remove widgets" : "Remove widget", l =>
        {
            foreach (var target in targets)
            {
                if (!target.IsWidget) l.Components.RemoveAll(c => c.Id == target.Id);
                else if (!Copies.Remove(l, target.Id))
                {
                    // A v1 stamped instance that did not migrate: its components are the widget.
                    l.Components.RemoveAll(c => c.Widget == target.Id);
                    l.Widgets?.Remove(target.Id);
                }
            }
        });
        _model.ClearSelection();
    }

    private void SelectCopy(string copyId)
    {
        _model.SetDepth(Depth.Layout);
        _model.Select([copyId]);
    }

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
            // Before the first write over a file opened as v1: the same backup `deskwall migrate`
            // makes, so the owner can roll back (plan D5).
            if (_backupBeforeApply && !created) ShellState.BackupV1(dest);
            _model.Save();
            _backupBeforeApply = false;
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

    /// <summary>Delete one of the owner's own template files. When that file is an override of a
    /// shipped key it is a reset, not a deletion: the widget stays in the gallery and the shipped
    /// version comes back, so the question has to say so.</summary>
    private void DeleteTemplate(WidgetTemplate template)
    {
        if (template.Path is null) return;
        // Copies are linked (plan D1): they follow the file, so deleting it is not harmless any more.
        var n = Counts().GetValueOrDefault(template.Key);
        var copies = n == 1 ? "1 copy on this wallpaper" : $"{n} copies on this wallpaper";
        var question = template.OverridesShipped
            ? $"Put '{template.Name}' back to the out-of-the-box version? Your edits to it are deleted"
              + (n > 0 ? $", and {copies} change back with it." : ".")
            : $"Delete the widget '{template.Name}'?" + (n > 0 ? $" {copies} will show as missing." : "");
        var answer = MessageBox.Show(this, question, "DeskWall", MessageBoxButton.YesNo, MessageBoxImage.Question);
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
        // Before anything reads the expansion: the copies of a saved widget redraw from its new file.
        _model.WidgetsChanged();
        Gallery.Load(_catalog);
        Knobs.Attach(_model);
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

    /// <summary>The in-process clipboard (Ctrl+C): <see cref="DesignerModel.CopyJson"/>, and how
    /// many times it has been pasted, so each paste lands one step further off the original.</summary>
    private string? _clip;
    private int _pastes;

    /// <summary>How far a paste or duplicate lands from what it came from, per paste.</summary>
    private const int PasteOffset = 16;

    private const string CopyCannotGainParts = "A copy cannot gain parts: Ctrl+Alt+K edits its widget.";

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Handled) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;   // with Alt down it arrives as a system key
        var mods = Keyboard.Modifiers;
        var ctrl = mods == ModifierKeys.Control;
        var focus = Keyboard.FocusedElement;
        // Not while a box is being typed into: Delete, Enter and Ctrl+Z belong to the text there.
        if (key == Key.S && ctrl) { Apply(); e.Handled = true; return; }
        if (focus is System.Windows.Controls.TextBox) return;
        // Enter presses a focused button; Esc ends a canvas drag (the canvas holds the mouse).
        var onButton = focus is System.Windows.Controls.Primitives.ButtonBase;
        var dragging = Mouse.Captured is not null;
        var onCanvas = focus is null || ReferenceEquals(focus, this) || Preview.IsKeyboardFocusWithin;
        var sel = _model.Selection.ToArray();
        switch (key)
        {
            case Key.Z when ctrl: _model.Undo(); break;
            case Key.Y when ctrl: _model.Redo(); break;
            case Key.Delete when mods == ModifierKeys.None: RemoveSelection(); break;
            case Key.Escape when mods == ModifierKeys.None && !dragging:
                if (!_model.Climb()) _model.ClearSelection();
                break;
            case Key.Enter when mods == ModifierKeys.None && !onButton:
                // The canvas takes the keys from here: the arrows nudge the parts just opened.
                if (_model.Descend()) Preview.Focus();
                break;
            // Only on the canvas: everywhere else Tab is how the keyboard moves between panels.
            case Key.Tab when onCanvas && (mods == ModifierKeys.None || mods == ModifierKeys.Shift):
                _model.SelectSibling(mods == ModifierKeys.Shift ? -1 : 1);
                break;
            case Key.A when ctrl: _model.SelectAll(); break;
            case Key.C when ctrl:
                if (_model.CopyJson(sel) is { } json) { _clip = json; _pastes = 0; }
                break;
            case Key.V when ctrl: Paste(); break;
            case Key.D when ctrl:
                var made = _model.Duplicate(sel, PasteOffset, PasteOffset);
                if (made.Count > 0) _model.Select(made);
                else if (_model.Depth.Kind == DepthKind.Copy && sel.Length > 0) SetStatus(CopyCannotGainParts);
                break;
            case Key.OemCloseBrackets when ctrl: _model.BringToFront(sel); break;
            case Key.OemOpenBrackets when ctrl: _model.SendToBack(sel); break;
            case Key.K when mods == (ModifierKeys.Control | ModifierKeys.Alt): _model.MakeOrEditWidget(); break;
            // The canvas has these too; up here they work wherever the focus is.
            case Key.D1 when mods == ModifierKeys.Shift: Preview.FitAll(); break;
            case Key.D2 when mods == ModifierKeys.Shift: Preview.FitSelection(); break;
            case Key.OemPlus or Key.Add when ctrl: Preview.ZoomIn(); break;
            case Key.OemMinus or Key.Subtract when ctrl: Preview.ZoomOut(); break;
            case Key.D0 or Key.NumPad0 when ctrl: Preview.ZoomTo(1); break;
            default: return;
        }
        e.Handled = true;
    }

    private void Paste()
    {
        if (_clip is null) return;
        var step = PasteOffset * (_pastes + 1);
        var made = _model.Paste(_clip, step, step);
        if (made.Count == 0)
        {
            if (_model.Depth.Kind == DepthKind.Copy) SetStatus(CopyCannotGainParts);
            return;
        }
        _pastes++;
        _model.Select(made);
    }

    // ---- the status line ------------------------------------------------------------------------------

    private DateTime? _appliedAt;

    /// <summary>Two facts, read from the machine, never from a channel of our own: when this layout
    /// last reached disk, and whether the process that paints it is running. The daemon's own last
    /// error, when it has one, replaces both - it is the only thing worth reading then.</summary>
    /// <summary>A panel said something. It holds the status line for a few seconds and then the
    /// periodic "applied / daemon" line takes it back: this bar is refreshed on a timer, so a
    /// message written straight into it would vanish inside a second.</summary>
    private void SetStatus(string message)
    {
        _transient = string.IsNullOrEmpty(message) ? null : message;
        _transientUntil = DateTime.UtcNow.AddSeconds(8);
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        if (_transient is { } note && DateTime.UtcNow < _transientUntil)
        {
            StatusText.Text = note;
            return;
        }
        _transient = null;
        var applied = _appliedAt is { } at ? $"Applied {at:HH:mm}"
            : _model.Path is { } p && File.Exists(p) ? $"Applied {File.GetLastWriteTime(p):HH:mm}"
            : "Not applied yet";

        // This home's daemon, not any deskwall process: a scratch designer must not report the
        // owner's live daemon as its own.
        var running = RuntimeInstance.FindDaemonWindow() != 0;

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
