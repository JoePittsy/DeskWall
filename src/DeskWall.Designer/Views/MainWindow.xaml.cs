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
/// minute, without seeing a coordinate or a binding. Three panes and a verb: drag in from Insert
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
/// same thing with no text); a menu bar for the keyboard verbs above (the canvas's right-click menu
/// carries them with their keys, and F1 lists every key), one-step forward and backward, and the
/// system clipboard; panel collapse keys; a
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
    /// <summary>The display in front of the owner, which a switch moves (#73); the model's signature
    /// can be another display's, when this one resolves by closest match.</summary>
    private readonly DisplaySignature _display;
    private readonly PreviewRenderer _renderer;
    private readonly DispatcherTimer _status = new() { Interval = TimeSpan.FromSeconds(5) };

    private Settings _settings;
    private IReadOnlyList<WidgetTemplate> _catalog;
    private DesignerModel _model = null!;
    private LiveSources? _live;
    private string? _transient;
    private DateTime _transientUntil;
    private bool _allowClose;

    /// <summary>The copy Edit placed because the widget had none on this layout (<see cref="EditWidget"/>);
    /// it goes again when the canvas is back at layout depth.</summary>
    private string? _scratchCopy;

    /// <summary>The open document is the in-memory v2 migration of a v1 file, and the first Apply
    /// has not happened yet: it writes <c>&lt;file&gt;.v1.json</c> before overwriting anything.</summary>
    private bool _backupBeforeApply;

    public MainWindow(LayoutStore store, Settings settings, DisplaySignature signature, LayoutResolution? resolution)
    {
        InitializeComponent();
        _store = store;
        _display = signature;
        _settings = settings;
        _catalog = WidgetCatalog.Load(WidgetCatalog.ShippedDir, WidgetCatalog.UserDir);
        _renderer = new PreviewRenderer(() => _live?.Tree() ?? ValueTree.Empty);

        Insert.AddRequested += t => Add(t);
        Insert.NewRequested += NewWidget;
        Insert.EditRequested += EditWidget;
        Insert.DuplicateRequested += DuplicateWidget;
        Insert.DeleteRequested += DeleteTemplate;
        Properties.RemoveRequested += Remove;
        Sources.Status += SetStatus;
        // The shell owns depth: the canvas asks, and the one path (the model) answers, so a
        // double-click, Enter, Esc and Ctrl+Alt+K cannot disagree about where they end up.
        Preview.DepthRequested += Preview.GoToDepth;
        Preview.EditWidgetRequested += copyId =>
        {
            if (Copies.Find(_model.Layout, copyId) is { } copy) Preview.GoToDepth(Depth.Widget(copy.Widget, copy.Id));
        };
        Layers.TemplateChildActivated += Properties.ShowTemplateChild;
        Providers.Status += SetStatus;
        Preview.Status += SetStatus;
        // The records go into LiveSources, not into a second tree of their own: the binding
        // chips, the preview and the Insert panel's Data rows all read that one, so a provider
        // that is not in it is one the user cannot bind.
        Providers.ProvidersChanged += records => _live?.SetProviders(records);

        Preview.ContextMenu = new System.Windows.Controls.ContextMenu();
        System.Windows.Automation.AutomationProperties.SetName(Preview.ContextMenu, "Canvas actions");
        Preview.ContextMenuOpening += OnCanvasMenuOpening;
        FillShortcutSheet();

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
    /// made here and the Insert panel is the first thing seen, which is the whole first-run story.</summary>
    private void Open(DisplaySignature signature, LayoutResolution? resolution)
    {
        if (_model is not null) { _model.Changed -= OnModelChanged; _model.DepthChanged -= OnDepthChanged; _model.Notice -= SetStatus; }

        var target = ShellState.OpenFrom(resolution, signature, LoadAuthored, DefaultBaseImage,
            WidgetCatalog.Finder(WidgetCatalog.ShippedDir, WidgetCatalog.UserDir));
        _model = new DesignerModel(target.Layout, target.Signature, target.Path);
        _model.Changed += OnModelChanged;
        _model.DepthChanged += OnDepthChanged;
        _model.Notice += SetStatus;
        _backupBeforeApply = target.Migrated;

        ShellState.CopyAssets(Path.Combine(AppContext.BaseDirectory, "assets", "weather"));

        Preview.Attach(_model, _renderer);
        Properties.Attach(_model);
        Sources.Attach(_model);
        Layers.Attach(_model);
        Insert.Attach(_model, Preview);
        Insert.Load(_catalog);

        RebuildLiveSources();
        RefreshChrome();
        ShowSourcesAtWidgetDepth();
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

    /// <summary>Widget depth runs the widget's own sources (a widget with no copy in the layout has
    /// them nowhere else) and shows them in the right column. Back at layout depth, a copy Edit placed
    /// only to have somewhere to edit the widget goes again.</summary>
    private void OnDepthChanged()
    {
        RebuildLiveSources();
        RefreshBreadcrumb();
        ShowSourcesAtWidgetDepth();
        if (_scratchCopy is { } id && _model.Depth.Kind == DepthKind.Layout)
        {
            _scratchCopy = null;
            // After the depth change has finished: this can be raised from inside an undo.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_model.Depth.Kind != DepthKind.Layout || Copies.Find(_model.Layout, id) is null) return;
                _model.Edit("Put the widget away", l => Copies.Remove(l, id));
                if (_model.Selection.Contains(id)) _model.ClearSelection();
            }));
        }
    }

    private void ShowSourcesAtWidgetDepth()
        => SourcesHost.Visibility = _model.Depth.Kind == DepthKind.Widget ? Visibility.Visible : Visibility.Collapsed;

    private void RefreshChrome()
    {
        LayoutNameText.Text = LayoutLabel();
        LayoutPicker.ToolTip = _model.Path;
        DisplayText.Text = ShellState.DisplayLabel(_model.Signature.Key);
        UndoButton.IsEnabled = _model.CanUndo;
        RedoButton.IsEnabled = _model.CanRedo;
        ApplyButton.IsEnabled = _model.Path is null || _model.Dirty;
        Insert.SetCounts(Counts());
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

        var banner = DepthText.Banner(_model);
        var changed = banner != DepthBannerText.Text;
        DepthBannerText.Text = banner ?? "";
        DepthBanner.Visibility = banner is null ? Visibility.Collapsed : Visibility.Visible;
        if (changed && banner is not null)
            System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(DepthBannerText)
                ?.RaiseAutomationEvent(System.Windows.Automation.Peers.AutomationEvents.LiveRegionChanged);
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

    /// <summary>The one running set of sources in the window, read by the canvas, the properties
    /// panel, the sources panel and the Insert panel's Data rows, so there is one hardware sampler
    /// however many panels show a value. In order, the first of a name winning: at widget depth the
    /// widget's own; the expansion's (the layout's own, then every copy's, merged the way the daemon
    /// merges them); the Insert panel's local defaults (<see cref="Insert.DefaultSources"/>); one of
    /// each source a catalogue widget wants, so adding a widget draws at once.
    /// <para>One instance for the window's life, changed in place (<see cref="LiveSources.Update"/>):
    /// a source whose name, type and settings are unchanged keeps running whatever the order, so a
    /// depth change (which reorders the list) never restarts the hardware sampler, and a knob turn
    /// never restarts the weather fetch.</para></summary>
    private void RebuildLiveSources()
    {
        var defs = new List<SourceDef>();
        IEnumerable<SourceDef> widget = _model.Depth.Kind == DepthKind.Widget ? _model.Parts.Sources : [];
        foreach (var source in widget.Concat(_model.Expanded().Layout.Sources).Concat(DeskWall.Designer.Model.Insert.DefaultSources).Concat(_catalog.SelectMany(t => t.Sources)))
            if (!defs.Any(d => string.Equals(d.Name, source.Name, StringComparison.OrdinalIgnoreCase)))
                defs.Add(source);

        if (_live is null)
        {
            _live = new LiveSources(defs, Secrets.Default(), SystemClock.Instance);
            _live.Updated += OnLiveUpdated;
            Properties.Live = _live;
            Insert.Live = _live;
            Sources.Live = _live;
            _live.SetProviders(Providers.Records);
        }
        else if (!_live.Update(defs)) return;
        _renderer.Request(_model);
    }

    /// <summary>A source published: the preview redraws.</summary>
    private void OnLiveUpdated() => Dispatcher.BeginInvoke(new Action(() =>
    {
        _renderer.Request(_model);
    }));

    // ---- the four things that change a layout ------------------------------------------------------

    /// <summary>A widget card clicked in Insert lands in the first gap in the right-hand margin (or
    /// just left of it), selected and flashing so it is found, and is free to drag from that moment
    /// (<see cref="Placement.Spawn"/>). Nothing is arranged, before or after: the canvas has no
    /// column any more, and where a widget sits is the owner's answer.
    /// <para>The margin rather than the middle of the canvas because windows sit centred on the
    /// ultrawide and leave roughly 440 px either side (CLAUDE.md); dropping a new widget behind a
    /// browser window would look like nothing had happened.</para></summary>
    private string? Add(WidgetTemplate template)
    {
        var at = SpawnAt(template.Width, template.Height);
        string? added = null;
        _model.Edit($"Add {template.Name}", l => added = Copies.Add(l, template, at.X, at.Y));
        if (added is not null)
        {
            SelectCopy(added);
            Preview.Flash(added);
        }
        return added;
    }

    /// <summary>Where a new copy of this size lands: the first gap in the right-hand margin.
    /// Read before the edit: the expansion is the model's, cached per change.</summary>
    private (int X, int Y) SpawnAt(int width, int height)
    {
        var region = Arranger.Column(_model.Signature.Width, _model.Signature.Height);
        var at = Placement.Spawn(region, Targets.All(_model).Select(t => t.Bounds).ToList(), width, height);
        return (at.X, at.Y);
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
        // typed is not in the document that gets written. Focus goes back afterwards (to the canvas
        // when what had it is gone, as a committed knob's row is rebuilt): with none, WPF routes no
        // key anywhere and Esc could not climb.
        var focused = CommitFocus(Preview);
        try { return Save(); }
        finally { RestoreFocus(focused, Preview); }
    }

    /// <summary>Move the focus to <paramref name="canvas"/>, so the box that had it raises LostFocus
    /// and commits, and return what had it. Not <c>Keyboard.ClearFocus</c>: that leaves the logical
    /// focus where it was, and a TextBox's LostFocus is the logical one, so nothing committed.</summary>
    internal static IInputElement? CommitFocus(UIElement canvas)
    {
        var focused = Keyboard.FocusedElement;
        if (!canvas.Focus()) Keyboard.ClearFocus();
        return focused;
    }

    /// <summary>Focus <paramref name="element"/> again if it is still on screen, else <paramref name="fallback"/>.</summary>
    internal static void RestoreFocus(IInputElement? element, UIElement fallback)
    {
        if (element is UIElement { IsVisible: true, Focusable: true } u && PresentationSource.FromVisual(u) is not null && u.Focus()) return;
        fallback.Focus();
    }

    private bool Save()
    {
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
        catch (InvalidOperationException ex) when (ex.InnerException is FormatException)
        {
            // A widget that would not load again: nothing was written, and the sentence says which.
            if (created) _model.Path = null;
            SetStatus(ex.Message);
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            if (created) _model.Path = null;
            MessageBox.Show(this, $"Could not write {dest}: {ex.Message}", "DeskWall",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        _appliedAt = DateTime.Now;
        // A widget written just now is on disk: its card reads "edited" and offers Reset.
        if (_model.WidgetEdits.Count > 0) ReloadCatalog();
        Remember(s => { s.LastSignatureKey = _model.Signature.Key; s.LastLayoutPath = _model.Path; });
        RefreshChrome();
        return true;
    }

    // ---- switching the layout in use (#73) -------------------------------------------------------

    /// <summary>The layout name drops a menu: every other layout in the library
    /// (<see cref="LayoutLibrary"/>), then "Other file..." for one that is not in it yet, such as the
    /// repo's <c>layouts\vapor.json</c>. Built on every opening, so a file the owner has just copied
    /// into the folder is there.</summary>
    private void LayoutPicker_Click(object sender, RoutedEventArgs e)
    {
        var menu = new System.Windows.Controls.ContextMenu
        {
            PlacementTarget = LayoutPicker,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
        };
        System.Windows.Automation.AutomationProperties.SetName(menu, "Switch layout");
        // The window's store was loaded at start; `deskwall theme` may have registered a file since.
        _store.Reload();
        foreach (var choice in ShellState.SwitchTargets(LayoutLibrary.List(_store, LayoutLibrary.DefaultDir), _model.Path))
        {
            // A TextBlock, not a string: a designer-made file is named after the display signature,
            // which is 90 characters of underscores and GUID, and a string header turns its first
            // underscore into an access key.
            var item = new System.Windows.Controls.MenuItem
            {
                Header = new System.Windows.Controls.TextBlock { Text = choice.Name, MaxWidth = 480, TextTrimming = TextTrimming.CharacterEllipsis },
                ToolTip = choice.Path,
            };
            System.Windows.Automation.AutomationProperties.SetName(item, choice.Name);
            item.Click += (_, _) => SwitchTo(choice.Path);
            menu.Items.Add(item);
        }
        if (menu.Items.Count > 0) menu.Items.Add(new System.Windows.Controls.Separator());
        var other = new System.Windows.Controls.MenuItem { Header = "Other file..." };
        other.Click += (_, _) => SwitchToOtherFile();
        menu.Items.Add(other);
        menu.IsOpen = true;
    }

    private void SwitchToOtherFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Switch to a layout", Filter = "Layout (*.json)|*.json" };
        if (dialog.ShowDialog(this) == true) SwitchTo(dialog.FileName);
    }

    /// <summary>The one switch the CLI uses too: every display sharing this one's layout moves to
    /// <paramref name="path"/> in one write of layouts.json, which the daemon's watcher picks up.
    /// The designer then opens what the daemon will now paint, as it does at start.</summary>
    private void SwitchTo(string path)
    {
        if (!ConfirmDiscard($"Apply the changes to {LayoutLabel()} before switching?")) return;
        LayoutSwitch done;
        try { done = LayoutLibrary.Use(_store, _display, path, LayoutLibrary.DefaultDir); }
        catch (LayoutSwitchException ex) { SetStatus(ex.Message); return; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { SetStatus($"Could not switch: {ex.Message}"); return; }
        _appliedAt = null;
        _scratchCopy = null;
        Open(_display, _store.Resolve(_display));
        SetStatus(ShellState.SwitchedText(done, RuntimeInstance.FindDaemonWindow() != 0));
    }

    // ---- the Insert panel's widget menu ---------------------------------------------------------
    // Every widget is edited on this canvas, at widget depth (brief section 3); Apply writes it
    // (a shipped one forks into the user's widgets folder, plan D2).

    /// <summary>"New widget": an empty frame in the right-hand margin, at widget depth.</summary>
    private void NewWidget()
    {
        var at = SpawnAt(Lens.NewWidth, Lens.NewHeight);
        Lens.NewWidget(_model, at.X, at.Y);
        Preview.Focus();
        SetStatus("A new widget: drag parts and values into its frame. Apply saves it.");
    }

    /// <summary>"Edit": widget depth on the first copy of it in this layout (the depth banner says
    /// what that reaches and whether Apply forks a shipped widget, as it does on every route in). A widget with no copy
    /// here has nowhere on the canvas to be drawn, so one is placed as a click on its card would
    /// place it, and taken away again when the canvas is back at layout depth; the widget edits
    /// stay, and Apply saves them.</summary>
    private void EditWidget(WidgetTemplate template)
    {
        var copy = _model.Layout.Copies?.Find(c => string.Equals(c.Widget, template.Key, StringComparison.OrdinalIgnoreCase));
        var id = copy?.Id;
        if (id is null)
        {
            if ((id = Add(template)) is null) return;
            _scratchCopy = id;
            SetStatus($"'{template.Name}' is not on this layout: it is shown here while you edit it, and goes when you leave the widget.");
        }
        _model.SetDepth(Depth.Widget(template.Key, id));
        _model.Select([]);
        Preview.Focus();
    }

    /// <summary>"Duplicate": the widget under a new key, with one copy of it, at widget depth.</summary>
    private void DuplicateWidget(WidgetTemplate template)
    {
        var at = SpawnAt(template.Width, template.Height);
        Lens.DuplicateWidget(_model, template, at.X, at.Y);
        Preview.Focus();
        SetStatus($"'{template.Name} copy' is a widget of its own. Apply saves it.");
    }

    /// <summary>Delete one of the owner's own widget files. When that file is a fork of a shipped
    /// key it is a reset, not a deletion: the widget stays in Insert and the shipped version comes
    /// back, so the question has to say so. Copies are linked by key (plan D1), in every layout on
    /// this machine, so the question says what happens to them.</summary>
    private void DeleteTemplate(WidgetTemplate template)
    {
        if (template.Path is null) return;
        var n = Counts().GetValueOrDefault(template.Key);
        var here = n == 1 ? "1 is on this layout" : $"{n} are on this layout";
        var question = template.OverridesShipped
            ? $"Put '{template.Name}' back to the out-of-the-box version? Your edits to it are deleted, and every copy of it in every layout on this machine changes back with it ({here}). Each copy's own changes stay."
            : $"Delete the widget '{template.Name}'? Every copy of it in every layout on this machine will show as missing ({here}).";
        var answer = MessageBox.Show(this, question, "DeskWall", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        try { File.Delete(template.Path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"Could not delete {template.Path}: {ex.Message}", "DeskWall",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        // Its unapplied edits too, or the copies would go on drawing them.
        _model.ForgetWidget(template.Key);
        ReloadCatalog();
    }

    /// <summary>A widget file changed on disk (Apply wrote one, or one was deleted). Everything that
    /// reads the catalog is rebuilt from it: the Insert panel's cards, the properties panel and the
    /// running sources, which include one of each catalog source.</summary>
    private void ReloadCatalog()
    {
        _catalog = WidgetCatalog.Load(WidgetCatalog.ShippedDir, WidgetCatalog.UserDir);
        // Before anything reads the expansion: the copies of a saved widget redraw from its new file.
        _model.WidgetsChanged();
        Insert.Load(_catalog);
        Properties.Attach(_model);
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
        if (key == Key.F1 && mods == ModifierKeys.None || key == Key.OemQuestion && mods == ModifierKeys.Shift)
        {
            ToggleShortcuts();
            e.Handled = true;
            return;
        }
        if (key == Key.Escape && ShortcutSheet.Visibility == Visibility.Visible) { ToggleShortcuts(); e.Handled = true; return; }
        // Nor inside a side panel: Delete on a property combo, Enter on a knob or Esc in the colour
        // picker belong to that control, not to the canvas selection. Layers is the exception: it
        // lists what is on the canvas, so its Delete, Enter and Esc are the canvas's.
        if (key is Key.Delete or Key.Enter or Key.Escape && InSidePanel()) return;
        // Enter presses a focused button; Esc ends a canvas drag (the canvas holds the mouse).
        var onButton = focus is System.Windows.Controls.Primitives.ButtonBase;
        var dragging = Mouse.Captured is not null;
        var onCanvas = focus is null || ReferenceEquals(focus, this) || Preview.IsKeyboardFocusWithin;
        var sel = _model.Selection.ToArray();
        switch (key)
        {
            case Key.Z when ctrl: _model.Undo(); break;
            case Key.Y when ctrl: _model.Redo(); break;
            case Key.Delete when mods == ModifierKeys.None:
                if (!Layers.RemoveFocusedOrphan()) RemoveSelection();
                break;
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
            case Key.C when ctrl: CopySelection(); break;
            case Key.V when ctrl: Paste(); break;
            case Key.D when ctrl: DuplicateSelection(); break;
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

    private bool InSidePanel()
        => Properties.IsKeyboardFocusWithin || Sources.IsKeyboardFocusWithin || Insert.IsKeyboardFocusWithin || Providers.IsKeyboardFocusWithin;

    private void CopySelection()
    {
        if (_model.CopyJson(_model.Selection) is { } json) { _clip = json; _pastes = 0; }
    }

    private void DuplicateSelection()
    {
        var sel = _model.Selection.ToArray();
        var made = _model.Duplicate(sel, PasteOffset, PasteOffset);
        if (made.Count > 0) _model.Select(made);
        else if (_model.Depth.Kind == DepthKind.Copy && sel.Length > 0) SetStatus(CopyCannotGainParts);
    }

    // ---- the canvas menu and the shortcut sheet ------------------------------------------------------

    /// <summary>Right-click on the canvas, or the Menu key / Shift+F10 with the canvas focused: the
    /// keyboard verbs, each with its key, so the menu teaches them. Rebuilt on every opening, so what
    /// is enabled (and whether the first item makes or edits a widget) matches the selection now.</summary>
    private void OnCanvasMenuOpening(object sender, System.Windows.Controls.ContextMenuEventArgs e)
    {
        var menu = Preview.ContextMenu!;
        menu.Items.Clear();
        var any = _model.Selection.Count > 0;
        void Item(string header, string keys, bool enabled, Action act)
        {
            var item = new System.Windows.Controls.MenuItem { Header = header, InputGestureText = keys, IsEnabled = enabled };
            item.Click += (_, _) => { act(); Preview.Focus(); };
            menu.Items.Add(item);
        }
        var copy = _model.EditableCopy();
        // Loose parts in the selection make a widget, even beside copies (Ctrl+A): the same rule as the key.
        var loose = _model.LooseSelection.Count > 0;
        if (copy is not null && !loose) Item("Edit widget", Shortcuts.MakeOrEdit, true, () => _model.MakeOrEditWidget());
        else Item("Make widget", Shortcuts.MakeOrEdit, loose, () => _model.MakeOrEditWidget());
        Item("Edit parts", Shortcuts.EditParts, _model.Depth.Kind == DepthKind.Layout && copy is not null, () => _model.Descend());
        menu.Items.Add(new System.Windows.Controls.Separator());
        Item("Duplicate", Shortcuts.Duplicate, any, DuplicateSelection);
        Item("Copy", Shortcuts.Copy, any, CopySelection);
        Item("Paste", Shortcuts.Paste, _clip is not null, Paste);
        menu.Items.Add(new System.Windows.Controls.Separator());
        Item("Bring to front", Shortcuts.Front, any, () => _model.BringToFront([.. _model.Selection]));
        Item("Send to back", Shortcuts.Back, any, () => _model.SendToBack([.. _model.Selection]));
        Item("Delete", Shortcuts.Delete, any, RemoveSelection);
        menu.Items.Add(new System.Windows.Controls.Separator());
        Item("Zoom to selection", Shortcuts.ZoomToSelection, true, Preview.FitSelection);
        Item("Keyboard shortcuts", Shortcuts.Sheet, true, ToggleShortcuts);

        // From the keyboard the menu opens at the selection, not at the canvas's top-left corner.
        if (e.CursorLeft < 0 && Preview.SelectionAnchor() is { } at)
        {
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Relative;
            menu.PlacementTarget = Preview;
            menu.HorizontalOffset = at.X;
            menu.VerticalOffset = at.Y;
        }
        else
        {
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            menu.HorizontalOffset = menu.VerticalOffset = 0;
        }
    }

    private void FillShortcutSheet()
    {
        foreach (var group in Shortcuts.All)
        {
            var header = new System.Windows.Controls.TextBlock { Text = group.Name, Margin = new Thickness(0, 8, 0, 4) };
            header.SetResourceReference(StyleProperty, "Caption");
            ShortcutList.Children.Add(header);
            foreach (var entry in group.Entries)
            {
                var row = new System.Windows.Controls.Grid { Margin = new Thickness(0, 0, 0, 4) };
                row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(136) });
                row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition());
                var keys = new System.Windows.Controls.TextBlock { Text = entry.Keys, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 8, 0) };
                keys.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
                var what = new System.Windows.Controls.TextBlock { Text = entry.What, TextWrapping = TextWrapping.Wrap };
                what.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
                System.Windows.Controls.Grid.SetColumn(what, 1);
                row.Children.Add(keys);
                row.Children.Add(what);
                System.Windows.Automation.AutomationProperties.SetName(row, $"{entry.Keys}: {entry.What}");
                ShortcutList.Children.Add(row);
            }
        }
    }

    private void ToggleShortcuts()
        => ShortcutSheet.Visibility = ShortcutSheet.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    private void CloseShortcuts_Click(object sender, RoutedEventArgs e) { ShortcutSheet.Visibility = Visibility.Collapsed; Preview.Focus(); }

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
        // A panel's sentence is news; the periodic "applied / daemon" refresh is not announced.
        if (_transient is not null)
            System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(StatusText)
                ?.RaiseAutomationEvent(System.Windows.Automation.Peers.AutomationEvents.LiveRegionChanged);
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

    /// <summary>The most of the left column Layers takes while it sizes to its rows.</summary>
    private const double LayersShare = 0.28;

    /// <summary>Insert keeps at least this much of the column, however far the splitter goes.</summary>
    private const double InsertFloor = InsertPanel.Floor + 10;   // and the Layers splitter above it

    /// <summary>Layers sizes to its rows, capped at a share of the column; once the splitter has been
    /// moved it is that height (remembered), still leaving Insert its floor.</summary>
    private void LeftColumn_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var h = LeftColumn.ActualHeight;
        if (h <= 0) return;
        if (_settings.LayersHeight is { } fixedAt)
        {
            Layers.MaxHeight = double.PositiveInfinity;
            LayersRow.Height = new GridLength(Math.Max(LayersRow.MinHeight, Math.Min(fixedAt, h - InsertFloor)));
        }
        // The panel itself is capped, not only its row: a panel taller than its row is clipped, and
        // its tree does not scroll to the rows cut off.
        else Layers.MaxHeight = Math.Max(LayersRow.MinHeight, Math.Min(h * LayersShare, h - InsertFloor));
        LayersRow.MaxHeight = Math.Max(LayersRow.MinHeight, h - InsertFloor);
    }

    /// <summary>A drag, or the arrow keys on the focused splitter: from here on Layers is the height
    /// the splitter gives it, not its rows'.</summary>
    private void LayersSplitter_DragStarted(object sender, RoutedEventArgs e)
        => Layers.MaxHeight = double.PositiveInfinity;

    private void LayersSplitter_DragCompleted(object sender, RoutedEventArgs e)
    {
        var height = LayersRow.ActualHeight;
        Remember(s => s.LayersHeight = height);
    }

    private void LayersSplitter_Key(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Up or Key.Down)) return;
        if (e.RoutedEvent == Keyboard.PreviewKeyDownEvent) LayersSplitter_DragStarted(sender, e);
        else LayersSplitter_DragCompleted(sender, e);
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
        if (!_allowClose && !ConfirmDiscard("Apply the changes before closing?")) { e.Cancel = true; return; }
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

    private bool ConfirmDiscard(string question)
    {
        if (!_model.Dirty) return true;
        var answer = MessageBox.Show(this, question, "DeskWall",
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
