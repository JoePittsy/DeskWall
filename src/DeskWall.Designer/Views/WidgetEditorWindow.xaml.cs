using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DeskWall.Core.Layout;
using DeskWall.Core.Sources;
using DeskWall.Core.Values;
using DeskWall.Designer.Model;
using DeskWall.Designer.Model.Widgets;

namespace DeskWall.Designer.Views;

/// <summary>
/// Build your own widget out of parts.
/// <para>
/// Job: get from "I want a widget that shows X" to a working template in the gallery, with the
/// values it will draw visible while building, without reading the layout format. Four columns of
/// answers to four questions: what is it called and how big is it (the header), what is on it (the
/// parts), where does the data come from (the sources), and what does the selected part look like
/// and what may its user change (the properties and the knob list).
/// </para>
/// <para>
/// The canvas in the middle is the layout window's, over a document the size of the widget. That
/// is not a shortcut: selection, drag, resize, align, nudge, z-order, duplicate and undo are one
/// implementation, so the two editors cannot drift into behaving differently.
/// </para>
/// <para>
/// Deliberately left out: a toolbar, a zoom control beyond the canvas's own, colour or theme
/// controls, a widget-level preview card, a separate "new knob" verb (a knob is a toggle on the
/// thing it adjusts, where the value it will carry is already on screen).
/// </para>
/// </summary>
public partial class WidgetEditorWindow : Window
{
    private readonly WidgetDocument _document;
    private readonly PreviewRenderer _renderer;

    private LiveSources? _live;
    private string _sourcesKey = "";
    private string? _savedJson;
    private bool _allowClose;

    public WidgetEditorWindow(WidgetDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        InitializeComponent();
        _document = document;
        _renderer = new PreviewRenderer(() => _live?.Tree() ?? ValueTree.Empty);

        foreach (var (label, value) in new[] { ("Top", "top"), ("Bottom", "bottom") })
            AnchorBox.Items.Add(new ComboBoxItem { Content = label, Tag = value });

        // Only a file that is already on disk has a saved state to compare against; a brand new
        // widget is dirty from the first keystroke, which is true.
        _savedJson = document.Path is null ? null : WidgetTemplateWriter.ToJson(document.ToTemplate());

        Preview.ShowEmptyHint = false;
        Preview.Attach(_document.Model, _renderer);
        Properties.Attach(_document.Model);
        Properties.IsAdjustable = _document.IsAdjustable;
        Properties.ToggleAdjustable = OnToggleAdjustable;
        Sources.Attach(_document);
        Sources.Status += ShowStatus;
        Sources.AdjustablesChanged += BuildKnobs;

        _document.Model.Changed += OnModelChanged;

        NameBox.TextChanged += (_, _) => { _document.Name = NameBox.Text; RefreshChrome(); };
        DescriptionBox.TextChanged += (_, _) => { _document.Description = DescriptionBox.Text; RefreshChrome(); };
        AnchorBox.SelectionChanged += (_, _) =>
        {
            if (AnchorBox.SelectedItem is ComboBoxItem { Tag: string anchor }) { _document.Anchor = anchor; RefreshChrome(); }
        };
        CommitOnEnterOrLeave(WidthBox, () => CommitSize());
        CommitOnEnterOrLeave(HeightBox, () => CommitSize());

        NameBox.Text = _document.Name;
        DescriptionBox.Text = _document.Description;
        ShowSize();
        SelectAnchor();

        RebuildLiveSources();
        BuildKnobs();
        RefreshChrome();
        // A document with a key but no file is a shipped widget opened copy-on-write. Say so on
        // the way in: the owner is about to edit something he cannot see a file for, and the
        // rule he needs to know is that saving does not change the one that ships.
        ShowStatus(_document.Path is { } path ? $"Editing {path}"
            : _document.EditingKey is null ? ""
            : "Editing the out-of-the-box widget. Saving keeps your version in your own widgets folder; the original stays as it is.");
    }

    /// <summary>The template was written. The shell reloads the catalog on this, so the gallery,
    /// the knobs panel and the live sources all see the new version.</summary>
    public event Action? Saved;

    public WidgetDocument Document => _document;

    // ---- the header ------------------------------------------------------------------------

    private static void CommitOnEnterOrLeave(TextBox box, Action commit)
    {
        box.LostFocus += (_, _) => commit();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { commit(); Keyboard.ClearFocus(); } };
    }

    /// <summary>Both boxes at once, because the two of them are one gesture and two undo entries
    /// for "make it 200 by 90" is one Ctrl+Z that leaves it 200 by 40.</summary>
    private void CommitSize()
    {
        var w = Parse(WidthBox.Text, _document.Model.Signature.Width);
        var h = Parse(HeightBox.Text, _document.Model.Signature.Height);
        _document.Resize(w, h);
        ShowSize();

        static int Parse(string text, int fallback)
            => int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : fallback;
    }

    private void ShowSize()
    {
        WidthBox.Text = _document.Model.Signature.Width.ToString(CultureInfo.InvariantCulture);
        HeightBox.Text = _document.Model.Signature.Height.ToString(CultureInfo.InvariantCulture);
    }

    private void SelectAnchor()
        => AnchorBox.SelectedItem = AnchorBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => string.Equals((string?)i.Tag, _document.Anchor, StringComparison.Ordinal))
            ?? AnchorBox.Items.OfType<ComboBoxItem>().FirstOrDefault();

    private void Fit_Click(object sender, RoutedEventArgs e)
    {
        Keyboard.ClearFocus();
        _document.FitToParts();
        ShowSize();
    }

    // ---- parts -----------------------------------------------------------------------------

    private void Part_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || !Enum.TryParse<PartKind>(tag, out var kind)) return;
        var added = _document.AddPart(kind);
        _document.Model.Select([added.Id]);
    }

    // ---- the model ---------------------------------------------------------------------------

    private void OnModelChanged()
    {
        RebuildLiveSources();
        BuildKnobs();
        ShowSize();
        RefreshChrome();
    }

    /// <summary>One running set of sources per document, shared by the canvas, the properties
    /// panel's binding picker and the value tree, so what the widget draws and what the tree shows
    /// are the same read. Rebuilt only when the definitions actually differ: doing it on every
    /// keystroke in the url box would restart the fetch on each letter.</summary>
    private void RebuildLiveSources()
    {
        var defs = _document.Model.Layout.Sources;
        var key = string.Join(";", defs.Select(s =>
            $"{s.Name}|{s.Type}|{s.EverySeconds}|{string.Join(",", s.Settings.Select(kv => kv.Key + "=" + kv.Value))}"));
        if (key == _sourcesKey && _live is not null) return;
        _sourcesKey = key;

        var previous = _live;
        if (previous is not null) previous.Updated -= OnLiveUpdated;
        _live = new LiveSources(defs.ToList(), Secrets.Default(), SystemClock.Instance);
        _live.Updated += OnLiveUpdated;
        Properties.Live = _live;
        Sources.Live = _live;
        previous?.Dispose();
        _renderer.Request(_document.Model);
    }

    private void OnLiveUpdated() => Dispatcher.BeginInvoke(new Action(() => _renderer.Request(_document.Model)));

    private void OnToggleAdjustable(string componentId, string property)
    {
        _document.ToggleAdjustable(componentId, property);
        BuildKnobs();
        RefreshChrome();
    }

    // ---- the knob list --------------------------------------------------------------------------

    /// <summary>What this widget lets whoever places it change: one row per knob, with the value
    /// it carries living on the part itself rather than being repeated here. A knob is removed
    /// from this list or from the toggle that made it; both are the same edit.</summary>
    private void BuildKnobs()
    {
        Knobs.Children.Clear();
        var adjustables = _document.Adjustables;
        var passing = _document.PassThroughKnobs;

        if (adjustables.Count == 0 && passing.Count == 0)
        {
            Knobs.Children.Add(Hint("Nothing yet. Turn on Knob beside a property, or beside a source's setting, to let this widget's user change it."));
            return;
        }

        foreach (var target in adjustables) Knobs.Children.Add(KnobRow(target));
        foreach (var knob in passing) Knobs.Children.Add(PassThroughRow(knob));
    }

    private FrameworkElement KnobRow(AdjustableTarget target)
    {
        var card = new Border { Style = (Style)FindResource("Card"), Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(10, 8, 10, 8) };
        var body = new StackPanel();
        card.Child = body;

        var top = new DockPanel();
        var remove = new Button
        {
            Content = "\u2715",
            Width = 24,
            Height = 24,
            Padding = new Thickness(0),
            Margin = new Thickness(8, 0, 0, 0),
            ToolTip = "Stop letting this be changed",
        };
        AutomationProperties.SetName(remove, "Remove this knob");
        DockPanel.SetDock(remove, Dock.Right);
        remove.Click += (_, _) => { _document.RemoveAdjustable(target); BuildKnobs(); RefreshChrome(); };
        top.Children.Add(remove);

        var label = new TextBox { Text = target.Label, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(0) };
        CommitOnEnterOrLeave(label, () =>
        {
            var text = label.Text.Trim();
            if (text.Length == 0) { label.Text = target.Label; return; }
            target.Label = text;
            RefreshChrome();
        });
        top.Children.Add(label);
        body.Children.Add(top);

        body.Children.Add(new TextBlock
        {
            Text = TargetLine(target),
            Style = (Style)FindResource("Hint"),
            Margin = new Thickness(0, 2, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        });

        if (KnobTypeOf(target) == KnobType.Number) body.Children.Add(RangeRow(target));
        return card;
    }

    /// <summary>What the knob writes. A Drive knob writes several places -- that is the point of
    /// it -- so the card lists them all rather than only the first.</summary>
    private static string TargetLine(AdjustableTarget target)
    {
        if (target.IsDrive) return string.Join(", ", target.DriveTargets.Select(t => $"{t.ComponentId} · {t.Property}"));
        return target.IsComponent ? $"{target.ComponentId} · {target.Property}" : $"{target.SourceName} · {target.SettingKey}";
    }

    private KnobType? KnobTypeOf(AdjustableTarget target) => Adjustable.ToKnob(_document, target)?.Type;

    /// <summary>The two bounds a number knob may have. Blank means no bound, which is the default
    /// and what most knobs want; a size that must stay between 8 and 96 is the exception worth a
    /// pair of boxes.</summary>
    private FrameworkElement RangeRow(AdjustableTarget target)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        row.Children.Add(Bound("Min", () => target.Min, v => target.Min = v));
        row.Children.Add(Bound("Max", () => target.Max, v => target.Max = v));
        return row;

        FrameworkElement Bound(string caption, Func<double?> get, Action<double?> set)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 0) };
            panel.Children.Add(new TextBlock { Text = caption, Style = (Style)FindResource("Caption"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            var box = new TextBox { Width = 56, Text = Text(get()) };
            CommitOnEnterOrLeave(box, () =>
            {
                var text = box.Text.Trim();
                set(text.Length == 0 ? null
                    : double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : get());
                box.Text = Text(get());
                RefreshChrome();
            });
            panel.Children.Add(box);
            return panel;
        }

        static string Text(double? value) => value?.ToString("R", CultureInfo.InvariantCulture) ?? "";
    }

    /// <summary>A knob the editor cannot express - a composite, a token splice, a binding write.
    /// Shown, so the owner knows it is there and that saving keeps it, and not editable, because
    /// the editor has no control that would get it right.</summary>
    private FrameworkElement PassThroughRow(Knob knob)
    {
        var card = new Border { Style = (Style)FindResource("Card"), Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(10, 8, 10, 8), Opacity = 0.65 };
        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = knob.Label, Style = (Style)FindResource("Body") });
        body.Children.Add(new TextBlock { Text = "Written by hand in the widget file; kept as it is.", Style = (Style)FindResource("Hint"), Margin = new Thickness(0, 2, 0, 0) });
        card.Child = body;
        return card;
    }

    private TextBlock Hint(string text) => new()
    {
        Text = text,
        Style = (Style)FindResource("Hint"),
    };

    // ---- chrome and status ---------------------------------------------------------------------

    private bool Dirty
    {
        get
        {
            if (_savedJson is null) return true;
            try { return WidgetTemplateWriter.ToJson(_document.ToTemplate()) != _savedJson; }
            catch (Exception) { return true; }
        }
    }

    private void RefreshChrome()
    {
        var name = string.IsNullOrWhiteSpace(_document.Name) ? "Untitled" : _document.Name;
        Title = $"{name}{(Dirty ? "*" : "")} - Widget editor";
    }

    private void ShowStatus(string text) => StatusText.Text = text;

    // ---- saving ------------------------------------------------------------------------------

    private void Save_Click(object sender, RoutedEventArgs e) => Save();

    private bool Save()
    {
        // A box commits on LostFocus, and Ctrl+S never moves focus, so without this the value
        // being typed is not in the template that gets written.
        Keyboard.ClearFocus();
        try
        {
            var path = _document.Save();
            _savedJson = WidgetTemplateWriter.ToJson(_document.ToTemplate());
            ShowStatus($"Saved to {path}");
            RefreshChrome();
            Saved?.Invoke();
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            ShowStatus(ex.Message);
            return false;
        }
    }

    // ---- keys and closing -------------------------------------------------------------------------

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Handled) return;
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        // Not while a box is being typed into: Delete and Ctrl+Z belong to the text there.
        var typing = Keyboard.FocusedElement is TextBox;
        switch (e.Key)
        {
            case Key.S when ctrl: Save(); e.Handled = true; break;
            case Key.Z when ctrl && !typing: _document.Model.Undo(); e.Handled = true; break;
            case Key.Y when ctrl && !typing: _document.Model.Redo(); e.Handled = true; break;
            case Key.D when ctrl && !typing: Duplicate(); e.Handled = true; break;
            case Key.Delete when !typing: RemoveSelection(); e.Handled = true; break;
            case Key.Escape when !typing: _document.Model.ClearSelection(); e.Handled = true; break;
            default: break;
        }
    }

    private void Duplicate()
    {
        var made = _document.Model.Duplicate(_document.Model.Selection, 8, 8);
        if (made.Count > 0) _document.Model.Select(made);
    }

    private void RemoveSelection()
    {
        if (_document.Model.Selection.Count == 0) return;
        _document.Model.Remove(_document.Model.Selection.ToList());
    }

    /// <summary>Called before this window is reused for another widget, and on close. True means
    /// "carry on"; false means the owner cancelled.</summary>
    public bool ConfirmDiscard()
    {
        if (!Dirty) return true;
        var answer = MessageBox.Show(this, "Save the changes before closing?", "DeskWall",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return answer switch
        {
            MessageBoxResult.Yes => Save(),
            MessageBoxResult.No => true,
            _ => false,
        };
    }

    /// <summary>Close without asking again, for a caller that has just asked
    /// (<see cref="ConfirmDiscard"/>) and is about to open the editor on something else.</summary>
    public void ForceClose()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!_allowClose && !ConfirmDiscard()) { e.Cancel = true; return; }
        _allowClose = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _document.Model.Changed -= OnModelChanged;
        _renderer.Dispose();
        if (_live is not null) _live.Updated -= OnLiveUpdated;
        _live?.Dispose();
    }
}
