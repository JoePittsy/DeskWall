using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DeskWall.Core.Bindings;
using DeskWall.Core.Layout;
using DeskWall.Core.Sources;
using DeskWall.Core.Values;
using DeskWall.Core.Widgets;
using DeskWall.Designer.Model;
using DeskWall.Designer.Model.Widgets;
using IOPath = System.IO.Path;

namespace DeskWall.Designer.Views;

/// <summary>
/// The right-hand panel: whatever is selected, at whatever depth, and nothing else.
/// <list type="bullet">
/// <item>Nothing selected at layout depth: the layout's own two settings (the photo, JPEG quality)
/// and whether each source is getting data.</item>
/// <item>A placed copy: its widget's knobs, Edit parts (copy depth) and Remove widget.</item>
/// <item>A part, at any depth: the copy's knobs first when it belongs to one, then its properties
/// grouped Content, Type, Colour, Arc and Geometry (<see cref="PropertyRows"/>), with human labels,
/// percentages, a colour picker, and a binding chip for anything bound.</item>
/// </list>
/// Every edit goes through <see cref="DesignerModel.EditAtDepth"/> (or the lens), one undo entry per
/// commit, so at copy depth it is an override on that copy; each overridden row carries a marker and
/// Reset / Push to widget in its menu. At widget depth the menu offers Expose as knob.
/// <para>Row actions: the Bind icon and the "..." menu show on hover; they are not tab stops. From
/// the keyboard the same actions are the row's context menu (the Menu key or Shift+F10 on the
/// row's control), so Tab stays one stop per value.</para>
/// <para>Leaves out: no id or rect editing outside Geometry, no tooltips that repeat a label, no
/// popups (the colour picker and the chip's search open inline, in the tab order).</para>
/// </summary>
public partial class PropertiesPanel : UserControl
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private const string Glyphs = "Segoe Fluent Icons, Segoe MDL2 Assets";
    private const string OverriddenNote = "Changed on this copy";

    private DesignerModel? _model;
    private LiveSources? _live;
    private string? _templateParentId;
    private string? _templateChildId;
    private readonly List<BindingChip> _chips = [];
    private string? _openColour;   // the row whose colour picker is open
    private string? _openChip;     // the row whose binding search is open
    private bool _layoutShown;
    private string _sourcesKey = "";

    public PropertiesPanel() => InitializeComponent();

    /// <summary>Remove widget was pressed for this copy id. The host removes it.</summary>
    public event Action<string>? RemoveRequested;

    /// <summary>Also the way to say "the widget files changed": it re-renders from scratch, and a
    /// copy's template is always looked up afresh through <see cref="DesignerModel.Finder"/>.</summary>
    public void Attach(DesignerModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (_model is not null)
        {
            _model.Changed -= Render;
            _model.SelectionChanged -= OnSelectionChanged;
            _model.DepthChanged -= OnDepthChanged;
        }
        _model = model;
        _model.Changed += Render;
        _model.SelectionChanged += OnSelectionChanged;
        _model.DepthChanged += OnDepthChanged;
        _templateParentId = _templateChildId = null;
        _openColour = _openChip = null;
        Render();
    }

    /// <summary>The running sources: the binding chips' values, and the no-selection view's "is this
    /// source working" list. Settable on its own because the host rebuilds them when the sources
    /// change.</summary>
    public LiveSources? Live
    {
        get => _live;
        set
        {
            if (_live is not null) _live.Updated -= OnLiveUpdated;
            _live = value;
            if (_live is not null) _live.Updated += OnLiveUpdated;
            Render();
        }
    }

    /// <summary>LayersPanel activated a repeater's template child, which the selection cannot name:
    /// show it until the selection changes. The pair of ids is re-resolved on every read and edit,
    /// never held as a ComponentDef across an undo.</summary>
    public void ShowTemplateChild(ComponentDef child, RepeaterDef parent)
    {
        ArgumentNullException.ThrowIfNull(child);
        ArgumentNullException.ThrowIfNull(parent);
        _templateParentId = parent.Id;
        _templateChildId = child.Id;
        Render();
    }

    private void OnSelectionChanged()
    {
        if (_model is { Selection.Count: > 0 }) { _templateParentId = null; _templateChildId = null; }
        _openColour = _openChip = null;
        Render();
    }

    private void OnDepthChanged()
    {
        _openColour = _openChip = null;
        Render();
    }

    private void OnLiveUpdated() => Dispatcher.BeginInvoke(new Action(() =>
    {
        if (_layoutShown) { if (SourcesKey() != _sourcesKey) Render(); return; }
        var tree = PickerRoot();
        foreach (var chip in _chips) chip.Refresh(tree);
    }));

    // ---- what is shown --------------------------------------------------------------------------

    private string? CurrentId() => _templateChildId ?? (_model is { Selection.Count: 1 } ? _model.Selection[0] : null);

    private ComponentLookup.Found? CurrentFound()
        => _model is not null && CurrentId() is { } id ? ComponentLookup.Find(_model.Parts, _templateParentId, id) : null;

    private WidgetCopy? Copy(string? copyId) => _model is null || copyId is null ? null : Copies.Find(_model.Layout, copyId);

    private WidgetTemplate? TemplateFor(WidgetCopy copy) => _model is null ? null : Copies.TryFind(_model.Finder(), copy.Widget);

    /// <summary>The copy whose overrides a part's rows show: the copy open at copy depth, or at layout
    /// depth the copy a projected part belongs to. None at widget depth or for a loose component.</summary>
    private string? MarkerCopy(ComponentDef top)
    {
        if (_model is null) return null;
        return _model.Depth.Kind switch
        {
            DepthKind.Copy => _model.Depth.CopyId,
            DepthKind.Layout when top.Widget is { Length: > 0 } w && Copy(w) is not null
                && !_model.Layout.Components.Exists(c => c.Id == top.Id) => w,
            _ => null,
        };
    }

    private void Render()
    {
        var focus = CaptureFocus();
        Root.Children.Clear();
        _chips.Clear();
        _layoutShown = false;
        if (_model is null) return;

        if (CurrentFound() is { } found) BuildPart(found);
        else if (_model.Selection.Count > 1)
        {
            Add(Header($"{_model.Selection.Count} selected"));
            if (_model.CanMakeWidget) BuildMakeWidget();
            else Add(Hint("Select one thing to change its properties."));
        }
        else if (_model is { Depth.Kind: DepthKind.Layout, Selection.Count: 1 } && Copy(_model.Selection[0]) is { } copy) BuildCopy(copy);
        else if (_model.Depth is { Kind: DepthKind.Copy } && Copy(_model.Depth.CopyId) is { } open)
        {
            Add(Header(TemplateFor(open)?.Name ?? open.Id));
            Add(Hint("Editing this copy: a change here is an override on it alone."));
            BuildKnobs(open);
            Add(PartPicker());
            Add(Hint("Select a part to change it."));
        }
        else if (_model.Depth is { Kind: DepthKind.Widget, WidgetKey: { } key }) BuildWidget(key);
        else BuildLayoutPanel();

        RestoreFocus(focus);
    }

    private void Add(UIElement e) => Root.Children.Add(e);

    // ---- loose parts, at layout depth ------------------------------------------------------------

    /// <summary>Two or more loose parts: what they are, and the verb that turns them into a widget
    /// (the same as Ctrl+Alt+K), instead of a dead end.</summary>
    private void BuildMakeWidget()
    {
        var model = _model!;
        var kinds = model.Selection.Select(id => TypeName(model.Find(id))).ToList();
        Add(Hint($"Placed by hand: {string.Join(", ", kinds)}. Make them one widget to place copies of it, each following your edits."));
        var make = ActionButton("Make widget", "selection:make-widget");
        make.SetResourceReference(StyleProperty, "AccentButtonStyle");
        make.ToolTip = "Make widget (Ctrl+Alt+K)";
        make.Click += (_, _) => model.MakeOrEditWidget();
        Add(make);
    }

    // ---- a copy, at layout depth -----------------------------------------------------------------

    private void BuildCopy(WidgetCopy copy)
    {
        var template = TemplateFor(copy);
        Add(Header(template?.Name ?? copy.Id));
        if (template is null)
            Add(Hint($"The widget '{copy.Widget}' this copy links to is missing or cannot be read, so it has nothing to show. Put the widget file back, or remove the copy."));
        else BuildKnobs(copy);

        var buttons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        if (template is not null)
        {
            var widget = ActionButton("Edit widget", "copy:edit-widget");
            widget.ToolTip = "Edit the widget itself; every copy that has not overridden a change follows (Ctrl+Alt+K)";
            widget.Click += (_, _) => _model?.MakeOrEditWidget();
            buttons.Children.Add(widget);
            var edit = ActionButton("Edit parts", "copy:edit");
            edit.ToolTip = "Change this copy's parts; each change is an override on this copy (Enter)";
            edit.Click += (_, _) => OpenParts(copy);
            buttons.Children.Add(edit);
        }
        var remove = ActionButton("Remove widget", "copy:remove");
        remove.Click += (_, _) => RemoveRequested?.Invoke(copy.Id);
        buttons.Children.Add(remove);
        Add(buttons);
        if (template is not null && _model is not null) Add(Hint(DepthText.Follow(DepthText.CopiesOf(_model.Layout, copy.Widget))));
    }

    /// <summary>What Details used to do: copy depth on this copy, with its first part selected, so a
    /// change is an override on this copy and never an edit to the widget.</summary>
    private void OpenParts(WidgetCopy copy)
    {
        if (_model is null) return;
        _model.SetDepth(Depth.Copy(copy.Id, copy.Widget));
        if (_model.Parts.Components.FirstOrDefault() is { } first) _model.Select([first.Id]);
    }

    /// <summary>The parts at copy or widget depth, by their id in the widget: the Details combo's job,
    /// for a part too small to hit on the canvas.</summary>
    private FrameworkElement PartPicker()
    {
        var prefix = _model!.Depth.CopyId is { } c ? c + "." : "";
        var combo = new ComboBox();
        foreach (var part in _model.Parts.Components)
            combo.Items.Add(new ComboBoxItem { Content = part.Id.StartsWith(prefix, StringComparison.Ordinal) ? part.Id[prefix.Length..] : part.Id, Tag = part.Id });
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => _model.Selection.Contains((string)i.Tag!));
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem { Tag: string id } && !_model.Selection.Contains(id)) _model.Select([id]);
        };
        Identify(combo, "Part", "part");
        combo.Margin = new Thickness(0);
        combo.HorizontalAlignment = HorizontalAlignment.Stretch;
        var row = Shell("part-row", "Part", combo, false, null, null, null);
        row.Margin = new Thickness(0, 12, 0, 6);
        return row;
    }

    // ---- knobs: the copy's own settings, first --------------------------------------------------

    private void BuildKnobs(WidgetCopy copy)
    {
        if (TemplateFor(copy) is not { } template) return;
        Add(GroupHeader("Knobs"));
        if (template.Knobs.Count == 0)
        {
            Add(Hint("Nothing to set: this widget shows the same thing for everyone."));
            return;
        }
        foreach (var knob in template.Knobs)
        {
            var rowId = "knob/" + knob.Id;
            var current = Copies.KnobValue(copy, knob);
            FrameworkElement? below = null;
            FrameworkElement editor = knob.Type switch
            {
                KnobType.Choice => KnobChoice(copy, template, knob, current, knob.Choices ?? []),
                KnobType.Drive => KnobChoice(copy, template, knob, current, FixedDrives()),
                KnobType.Color => ColourEditor(rowId, knob.Label, Display(current), hex => CommitKnob(copy, template, knob, hex), out below),
                KnobType.Town => KnobTown(copy, template, knob, current, out below),
                _ => KnobText(copy, template, knob, current),
            };
            Add(Shell(rowId, knob.Label, editor, overridden: false, bind: null, menu: null, below));
        }
    }

    private void CommitKnob(WidgetCopy copy, WidgetTemplate template, Knob knob, string value)
    {
        if (_model is null || Copy(copy.Id) is not { } now) return;
        if (string.Equals(Copies.KnobValue(now, knob), value, StringComparison.Ordinal)) return;
        _model.Edit($"Set {knob.Label}", l => Copies.SetKnob(l, template, copy.Id, knob.Id, value));
    }

    /// <summary>A choice's value may be a composite ("GPU temperature||hardware.gpuTempFraction||...")
    /// whose first part is the only thing a human should see; the whole composite goes back to
    /// SetKnob. Real ComboBoxItems with the label as Content, so a screen reader reads what is on
    /// screen.</summary>
    private ComboBox KnobChoice(WidgetCopy copy, WidgetTemplate template, Knob knob, string current, IReadOnlyList<string> choices)
    {
        var combo = new ComboBox();
        foreach (var choice in choices) combo.Items.Add(new ComboBoxItem { Content = Display(choice), Tag = choice });
        combo.SelectedItem =
            combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => string.Equals((string?)i.Tag, current, StringComparison.OrdinalIgnoreCase))
            ?? combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => string.Equals((string?)i.Content, Display(current), StringComparison.OrdinalIgnoreCase))
            ?? combo.Items.OfType<ComboBoxItem>().FirstOrDefault();
        combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is ComboBoxItem { Tag: string v }) CommitKnob(copy, template, knob, v); };
        return combo;
    }

    /// <summary>The part of a knob value a human is meant to see: part 0 of a "||" composite.</summary>
    private static string Display(string value)
    {
        var i = value.IndexOf("||", StringComparison.Ordinal);
        return i < 0 ? value : value[..i];
    }

    private FrameworkElement KnobText(WidgetCopy copy, WidgetTemplate template, Knob knob, string current)
    {
        if (PropertyRows.IsPercentKnob(knob)) return KnobPercent(copy, template, knob, current);
        var box = new TextBox { Text = Display(current) };
        void Do()
        {
            var text = box.Text.Trim();
            if (knob.Type == KnobType.Number)
            {
                var now = Copy(copy.Id) is { } c ? Copies.KnobValue(c, knob) : current;
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) { box.Text = Display(now); return; }
                n = Math.Clamp(n, knob.Min ?? double.MinValue, knob.Max ?? double.MaxValue);
                text = n.ToString("R", CultureInfo.InvariantCulture);
                box.Text = text;
            }
            CommitKnob(copy, template, knob, text);
        }
        OnCommit(box, Do);
        return box;
    }

    /// <summary>"Warn at 90 %", as the property row it stands for shows it; 0.9 in the file. The
    /// knob's min and max are fractions too, and bound what is typed.</summary>
    private FrameworkElement KnobPercent(WidgetCopy copy, WidgetTemplate template, Knob knob, string current)
    {
        string Shown(string v) => PropertyRows.PercentText(PropertyValue.Literal(v)) ?? v;
        var box = new TextBox { Text = Shown(current) };
        OnCommit(box, () =>
        {
            var now = Copy(copy.Id) is { } c ? Copies.KnobValue(c, knob) : current;
            if (PropertyRows.FromPercentText(box.Text)?.LiteralText is not { } literal
                || !double.TryParse(literal, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) { box.Text = Shown(now); return; }
            n = Math.Clamp(n, knob.Min ?? double.MinValue, knob.Max ?? double.MaxValue);
            var text = n.ToString("R", CultureInfo.InvariantCulture);
            box.Text = Shown(text);
            CommitKnob(copy, template, knob, text);
        });
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var sign = Label("%");
        sign.Width = double.NaN;
        sign.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(sign, 1);
        grid.Children.Add(box);
        grid.Children.Add(sign);
        return grid;
    }

    /// <summary>A town is typed, then looked up once. Until it resolves nothing is written: a
    /// half-applied latitude would put the weather somewhere off the coast of Africa.</summary>
    private TextBox KnobTown(WidgetCopy copy, WidgetTemplate template, Knob knob, string current, out FrameworkElement below)
    {
        var box = new TextBox { Text = Display(current) };
        var note = Hint("");
        note.Margin = new Thickness(0, 4, 0, 0);
        below = note;
        async void Resolve()
        {
            var town = box.Text.Trim();
            var now = Copy(copy.Id) is { } c ? Copies.KnobValue(c, knob) : current;
            if (town.Length == 0 || string.Equals(town, Display(now), StringComparison.OrdinalIgnoreCase)) return;
            note.Text = "looking up...";
            var hit = await Copies.ResolveTownAsync(town, Http).ConfigureAwait(true);
            if (hit is not { } p) { note.Text = $"'{town}' was not found."; return; }
            note.Text = string.Format(CultureInfo.InvariantCulture, "{0} ({1:0.00}, {2:0.00})", town, p.lat, p.lon);
            // "town||lat||lon": SetKnob never makes a network call, so the coordinates travel with
            // the name (docs/layout-format.md, "Widgets").
            CommitKnob(copy, template, knob, string.Format(CultureInfo.InvariantCulture, "{0}||{1}||{2}", town, p.lat, p.lon));
        }
        OnCommit(box, Resolve);
        return box;
    }

    private static IReadOnlyList<string> FixedDrives()
    {
        try { return DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed).Select(d => d.Name.TrimEnd('\\', ':')).ToList(); }
        catch (IOException) { return Array.Empty<string>(); }
    }

    // ---- the widget itself, at widget depth ------------------------------------------------------

    /// <summary>Widget depth with no part selected: the widget as a whole. What the retired widget
    /// editor's header and knob list did: its name and description (what the Insert card shows),
    /// its anchor, its size (read only: the frame hugs its parts), and its knobs. The key is not
    /// here: it is fixed when the widget is made (plan D2). Each change is one edit to the widget
    /// (<see cref="Lens.EditWidget"/>), which every copy follows, written by Apply.</summary>
    private void BuildWidget(string key)
    {
        var model = _model!;
        if (Copies.TryFind(model.Finder(), key) is not { } template)
        {
            Add(Header(key));
            Add(Hint($"The widget '{key}' is missing or cannot be read."));
            return;
        }
        Add(Header(template.Name));
        Add(Hint("Editing the widget: every copy that has not overridden a change follows it. Apply saves it."));

        Add(GroupHeader("Widget"));
        Add(Shell("widget/name", "Name", WidgetText(key, "widget:name", "Name", template.Name, (d, v) => d.Name = v), false, null, null, null));
        Add(Shell("widget/description", "Description", WidgetText(key, "widget:description", "Description", template.Description, (d, v) => d.Description = v), false, null, null, null));
        var anchor = new ComboBox();
        foreach (var (label, value) in new[] { ("Top", "top"), ("Bottom", "bottom") })
            anchor.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        anchor.SelectedItem = anchor.Items.OfType<ComboBoxItem>().FirstOrDefault(i => string.Equals((string)i.Tag!, template.Anchor, StringComparison.OrdinalIgnoreCase));
        anchor.SelectionChanged += (_, _) =>
        {
            if (anchor.SelectedItem is ComboBoxItem { Tag: string v }) Lens.EditWidget(model, key, "Set anchor", d => d.Anchor = v);
        };
        Identify(anchor, "Anchor", "widget:anchor");
        anchor.ToolTip = "Only for generated starter layouts: whether this widget stacks down from the top of the margin or up from its bottom. Where you drag it is where it stays.";
        Add(Shell("widget/anchor", "Anchor", anchor, false, null, null, null));
        var size = Label(string.Format(CultureInfo.InvariantCulture, "{0} \u00d7 {1}", template.Width, template.Height));
        size.ToolTip = "The frame hugs its parts: move or resize a part to change it";
        Add(Shell("widget/size", "Size", size, false, null, null, null));

        BuildWidgetKnobs(key);
        Add(PartPicker());
        Add(Hint("Select a part to change it. Its row menu exposes a property as a knob."));
    }

    /// <summary>A header box: blank is refused (Insert shows the name, and a widget file with no
    /// description does not load), so the box goes back to what the widget has.</summary>
    private TextBox WidgetText(string key, string id, string name, string value, Action<WidgetDocument, string> set)
    {
        var box = new TextBox { Text = value, TextWrapping = TextWrapping.Wrap };
        Identify(box, name, id);
        OnCommit(box, () =>
        {
            var text = box.Text.Trim();
            if (text.Length == 0) { box.Text = value; return; }
            if (_model is null) return;
            // The name carries the key with it until the first Apply (Lens.RenameWidget).
            if (id == "widget:name") Lens.RenameWidget(_model, key, text);
            else Lens.EditWidget(_model, key, $"Set {name.ToLowerInvariant()}", d => set(d, text));
        });
        return box;
    }

    /// <summary>The widget's knobs: what a copy may change. A knob is made from its property's row
    /// menu (Expose as knob) or a source setting's Knob toggle; here it is named, bounded and
    /// removed. One a person wrote by hand in the file (a composite, a token splice) is listed and
    /// kept as it is: nothing here could edit it without getting it wrong.</summary>
    private void BuildWidgetKnobs(string key)
    {
        if (WidgetDoc() is not { } doc) return;
        Add(GroupHeader("Knobs"));
        if (doc.Adjustables.Count == 0 && doc.PassThroughKnobs.Count == 0)
            Add(Hint("None yet. Expose a property as a knob from its row menu, or a source setting with its Knob toggle, to let each copy change it."));
        foreach (var target in doc.Adjustables)
        {
            var id = target.Id;
            AdjustableTarget Find(WidgetDocument d) => d.Adjustables.First(a => a.Id == id);
            var label = new TextBox { Text = target.Label };
            Identify(label, $"Knob {target.Label}, label", $"knob-label/{id}");
            OnCommit(label, () =>
            {
                var text = label.Text.Trim();
                if (text.Length == 0) { label.Text = target.Label; return; }
                if (_model is not null) Lens.EditWidget(_model, key, "Rename knob", d => Find(d).Label = text);
            });
            var remove = new Button { Content = "\u2715", Width = 28, Padding = new Thickness(0), Margin = new Thickness(6, 0, 0, 0), ToolTip = "Stop letting a copy change this" };
            Identify(remove, $"Remove the knob {target.Label}", $"knob-remove/{id}");
            remove.Click += (_, _) => { if (_model is not null) Lens.EditWidget(_model, key, $"Remove {target.Label} knob", d => d.RemoveAdjustable(Find(d))); };
            var line = new DockPanel();
            DockPanel.SetDock(remove, Dock.Right);
            line.Children.Add(remove);
            line.Children.Add(label);

            FrameworkElement? below = null;
            // A percentage knob ("Warn at") is bounded in percent too: 0 and 100, not 0 and 1.
            var asKnob = Adjustable.ToKnob(doc, target);
            var percent = asKnob is not null && PropertyRows.IsPercentKnob(asKnob);
            if (asKnob?.Type == KnobType.Number)
            {
                var range = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
                range.Children.Add(Bound("Min", target.Min, (d, v) => Find(d).Min = v));
                range.Children.Add(Bound("Max", target.Max, (d, v) => Find(d).Max = v));
                below = range;
            }
            Add(Shell($"knob-row/{id}", TargetLine(target), line, false, null, null, below));

            FrameworkElement Bound(string caption, double? value, Action<WidgetDocument, double?> set)
            {
                var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 0) };
                var text = Label(caption);
                text.Margin = new Thickness(0, 0, 6, 0);
                panel.Children.Add(text);
                string Shown(double? v) => v is not { } n ? ""
                    : percent ? PropertyRows.PercentText(PropertyValue.Literal(n)) ?? n.ToString("R", CultureInfo.InvariantCulture)
                    : n.ToString("R", CultureInfo.InvariantCulture);
                var box = new TextBox { Width = 64, Text = Shown(value) };
                Identify(box, $"Knob {target.Label}, {caption.ToLowerInvariant()}imum{(percent ? " in percent" : "")} (blank for none)", $"knob-{caption.ToLowerInvariant()}/{id}");
                OnCommit(box, () =>
                {
                    var t = box.Text.Trim();
                    double? v = t.Length == 0 ? null
                        : percent ? (double.TryParse(PropertyRows.FromPercentText(t)?.LiteralText, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : value)
                        : double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : value;
                    if (_model is not null) Lens.EditWidget(_model, key, $"Set {target.Label} {caption.ToLowerInvariant()}", d => set(d, v));
                });
                panel.Children.Add(box);
                if (percent)
                {
                    var sign = Label("%");
                    sign.Width = double.NaN;
                    sign.Margin = new Thickness(4, 0, 0, 0);
                    panel.Children.Add(sign);
                }
                return panel;
            }
        }
        foreach (var knob in doc.PassThroughKnobs)
        {
            var kept = Label(knob.Label);
            kept.ToolTip = "This knob was written straight into the widget file (it sets several things at once), so it is kept exactly as it is; each copy still changes it.";
            Add(Shell($"knob-row/{knob.Id}", "Kept as written", kept, false, null, null, null));
        }
    }

    /// <summary>What a knob writes, as the label of its row: the part (or source) it changes. A
    /// Drive knob writes several places, which is the point of it.</summary>
    private static string TargetLine(AdjustableTarget target)
        => target.IsDrive ? "Drive"
            : target.IsComponent ? $"{target.ComponentId} \u00b7 {PropertyRows.Label(target.Property!)}"
            : $"{target.SourceName} \u00b7 {target.SettingKey}";

    // ---- a part ---------------------------------------------------------------------------------

    /// <summary>How a part's rows reach the document and what they annotate.</summary>
    /// <param name="CopyId">The copy whose overrides are marked (copy depth, or a copy's part at
    /// layout depth); null otherwise.</param>
    /// <param name="KeyPrefix">The override key up to the property: <c>components.&lt;part&gt;.</c>, or
    /// <c>components.&lt;repeater&gt;.&lt;child&gt;.</c> for a template child.</param>
    /// <param name="Doc">At widget depth, the widget as a document, for Expose as knob.</param>
    /// <param name="LocalId">The part's id in the widget (top level only), for <paramref name="Doc"/>.</param>
    private sealed record RowContext(string? CopyId, IReadOnlyDictionary<string, PropertyValue> Overrides, string? KeyPrefix, WidgetDocument? Doc, string? LocalId);

    private void BuildPart(ComponentLookup.Found found)
    {
        var model = _model!;
        var def = found.Def;
        var top = found.Parent ?? def;
        var copyId = MarkerCopy(top);
        var copy = Copy(copyId);
        string Local(string id) => copyId is not null && id.StartsWith(copyId + ".", StringComparison.Ordinal) ? id[(copyId.Length + 1)..]
            : model.Depth is { Kind: DepthKind.Widget, CopyId: { } o } && id.StartsWith(o + ".", StringComparison.Ordinal) ? id[(o.Length + 1)..] : id;

        if (copy is not null)
        {
            Add(Header(TemplateFor(copy)?.Name ?? copy.Id));
            if (model.Depth.Kind == DepthKind.Copy) Add(Hint("Editing this copy: a change here is an override on it alone."));
            BuildKnobs(copy);
            if (model.Depth.Kind == DepthKind.Copy) Add(PartPicker());
            Add(SubHeader(TypeName(def) + " · " + (found.Parent is { } p0 ? Local(p0.Id) + " › " + def.Id : Local(def.Id))));
        }
        else if (model.Depth is { Kind: DepthKind.Widget, WidgetKey: { } wk })
        {
            Add(Header(Copies.TryFind(model.Finder(), wk)?.Name ?? wk));
            Add(Hint("Editing the widget: every copy that has not overridden a change follows it."));
            Add(PartPicker());
            Add(SubHeader(TypeName(def) + " · " + Local(def.Id)));
        }
        else
        {
            Add(Header(TypeName(def)));
            Add(Hint(found.Parent is { } p1 ? $"Repeated for every item of {p1.Id}." : "Placed by hand, so it belongs to no widget and has no knobs."));
        }

        var prefix = copyId is null ? null
            : found.Parent is { } parent ? $"components.{Local(parent.Id)}.{def.Id}." : $"components.{Local(def.Id)}.";
        var ctx = new RowContext(copyId, (IReadOnlyDictionary<string, PropertyValue>?)copy?.Overrides ?? new Dictionary<string, PropertyValue>(),
            prefix, found.Parent is null ? WidgetDoc() : null, found.Parent is null ? Local(def.Id) : null);

        var rows = PropertyRows.For(def);
        foreach (var group in Enum.GetValues<PropertyRows.Group>())
        {
            var inGroup = rows.Where(r => r.Group == group).ToList();
            if (inGroup.Count == 0 && group != PropertyRows.Group.Geometry) continue;
            Add(GroupHeader(group.ToString()));
            if (group == PropertyRows.Group.Geometry) foreach (var g in GeometryRows(def, ctx)) Add(g);
            foreach (var row in inGroup) Add(PropRow(def, row, ctx));
        }
    }

    /// <summary>At widget depth, the widget as a <see cref="WidgetDocument"/>: the one place that
    /// knows which properties may become knobs and how (<see cref="Adjustable.ToKnob"/>).</summary>
    private WidgetDocument? WidgetDoc()
    {
        if (_model is not { Depth: { Kind: DepthKind.Widget, WidgetKey: { } key } }) return null;
        return Copies.TryFind(_model.Finder(), key) is { } template ? WidgetDocument.FromTemplate(template) : null;
    }

    /// <summary>Expose a part's property as a knob of the widget, or take it back, as one edit to
    /// the overlay template: only its knob list changes, so its parts stay exactly as they are.
    /// The knob's default is the value now, so no copy changes on screen.</summary>
    private void ToggleKnob(WidgetDocument doc, string localId, string property, string label)
    {
        if (_model is not { Depth: { Kind: DepthKind.Widget, WidgetKey: { } key } }) return;
        if (Copies.TryFind(_model.Finder(), key) is not { } template) return;
        var exposed = doc.ToggleAdjustable(localId, property);
        var knobs = doc.ToTemplate().Knobs;
        var edited = new WidgetTemplate
        {
            Name = template.Name, Key = template.Key, Path = template.Path, Description = template.Description,
            Width = template.Width, Height = template.Height, Anchor = template.Anchor, Requires = template.Requires,
            Sources = template.Sources, Components = template.Components, Knobs = knobs,
        };
        _model.Edit(exposed ? $"Expose {label} as knob" : $"Remove {label} knob", (_, edits) => edits[template.Key] = edited);
    }

    private IEnumerable<FrameworkElement> GeometryRows(ComponentDef def, RowContext ctx)
    {
        var rect = ctx.KeyPrefix is null ? null : ctx.KeyPrefix + "rect";
        var z = ctx.KeyPrefix is null ? null : ctx.KeyPrefix + ComponentProperties.ZProp.Name.ToLowerInvariant();
        var geometry = PropertySchema.Geometry.ToDictionary(g => g.Name);
        FrameworkElement Pair(string a, string b)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            var first = GeometryBox(def, geometry[a]);
            var second = GeometryBox(def, geometry[b]);
            Grid.SetColumn(second, 2);
            grid.Children.Add(first);
            grid.Children.Add(second);
            return grid;
        }
        yield return GeometryShell(def, "Position", Pair("X", "Y"), rect, ctx);
        yield return GeometryShell(def, "Size", Pair("W", "H"), rect, ctx);
        yield return GeometryShell(def, PropertyRows.Label("Z"), GeometryBox(def, geometry["Z"]), z, ctx);
    }

    private FrameworkElement GeometryShell(ComponentDef def, string label, FrameworkElement editor, string? key, RowContext ctx)
    {
        var overridden = key is not null && ctx.Overrides.ContainsKey(key);
        return Shell(RowId(def, label), label, editor, overridden, bind: null, RowMenu(label, null, null, ctx.CopyId, overridden ? key : null, null), null);
    }

    private TextBox GeometryBox(ComponentDef def, (string Name, Func<ComponentDef, int> Get, Action<ComponentDef, int> Set) g)
    {
        var label = PropertyRows.Label(g.Name);
        var box = new TextBox { Text = g.Get(def).ToString(CultureInfo.InvariantCulture) };
        Identify(box, label, RowId(def, g.Name));
        OnCommit(box, () =>
        {
            if (!int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) { if (CurrentFound() is { } f0) box.Text = g.Get(f0.Def).ToString(CultureInfo.InvariantCulture); return; }
            if (CurrentFound() is { } f && g.Get(f.Def) == v) return;   // see SetLiteral
            EditCurrent($"Set {label}", (_, d) => g.Set(d, v));
        });
        return box;
    }

    private FrameworkElement PropRow(ComponentDef def, PropertyRows.Row row, RowContext ctx)
    {
        var prop = row.Prop;
        var value = prop.Get(def) ?? PropertyValue.Literal("");
        var rowId = RowId(def, prop.Name);
        var key = ctx.KeyPrefix is null ? null : ctx.KeyPrefix + char.ToLowerInvariant(prop.Name[0]) + prop.Name[1..];
        var overridden = key is not null && ctx.Overrides.ContainsKey(key);
        FrameworkElement? below = null;
        FrameworkElement editor;
        var bound = value.Binding;
        if (bound is not null || _openChip == rowId) editor = Chip(prop, row.Label, bound, rowId);
        else if (prop.Editor == PropertySchema.Editor.Binding)
        {
            // "items": "drives" parses as a literal, so an unbound Items is loadable and the resolver
            // refuses it at render time. Show what is there and say so, rather than dereference a
            // Binding that is null.
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock { Text = value.LiteralText ?? "", TextWrapping = TextWrapping.Wrap });
            stack.Children.Add(Hint($"{prop.Name.ToLowerInvariant()} must be a binding"));
            editor = stack;
        }
        else editor = LiteralEditor(row, value, rowId, out below);

        Action? bind = bound is null && _openChip != rowId ? () => { _openChip = rowId; Render(); } : null;
        Action? unbind = bound is not null && prop.Editor != PropertySchema.Editor.Binding
            ? () => EditCurrent($"Unbind {row.Label}", (_, d) => prop.Set(d, PropertyValue.Literal(""))) : null;
        (string, bool, Action)? expose = null;
        if (ctx.Doc is { } doc && ctx.LocalId is { } local && doc.Model.Find(local) is { } docPart)
        {
            var drive = Adjustable.CanAdjustAsDrive(docPart, prop);
            if (drive || Adjustable.CanAdjust(docPart, prop))
                expose = (drive ? "Expose as drive picker" : "Expose as knob", doc.IsAdjustable(local, prop.Name), () => ToggleKnob(doc, local, prop.Name, row.Label));
        }
        return Shell(rowId, row.Label, editor, overridden, bind, RowMenu(row.Label, bind, unbind, ctx.CopyId, overridden ? key : null, expose), below);
    }

    private BindingChip Chip(PropertySchema.Prop prop, string label, Binding? bound, string rowId)
    {
        var chip = new BindingChip { Editor = prop.Editor, PropertyLabel = label };
        var tree = PickerRoot();
        chip.Show(bound, Catalog(tree), tree);
        chip.Preview += (value, entry) =>
        {
            _model?.Transient(() => EditCurrent($"Preview {label}", (l, d) =>
            {
                prop.Set(d, value);
                if (entry.Source is { } source && !l.Sources.Exists(s => s.Name == source.Name)) l.Sources.Add(source);
            }));
        };
        chip.Chosen += (value, entry) =>
        {
            _model?.EndTransient();
            _openChip = null;
            var source = entry.Source;
            if (CurrentFound() is { } f && prop.Get(f.Def) is { } now && Overrides.Same(now, value)) { Render(); return; }
            EditCurrent($"Bind {label}", (l, d) =>
            {
                prop.Set(d, value);
                // The value's source comes along when it is not there yet (at widget depth, into the
                // widget), as a drop does. At copy depth the lens keeps overrides only.
                if (source is not null && !l.Sources.Exists(s => string.Equals(s.Name, source.Name, StringComparison.OrdinalIgnoreCase)))
                    l.Sources.Add(LayoutFile.Parse(new LayoutFile { BaseImage = "", Sources = [source] }.ToJson()).Sources[0]);
            });
        };
        chip.Unbound += () => { _openChip = null; EditCurrent($"Unbind {label}", (_, d) => prop.Set(d, PropertyValue.Literal(""))); };
        chip.Closed += () => { _model?.EndTransient(); if (_openChip == rowId) { _openChip = null; Render(); } };
        if (_openChip == rowId) chip.BeginEdit();
        AutomationProperties.SetAutomationId(chip, rowId);
        _chips.Add(chip);
        return chip;
    }

    /// <summary>The values a binding here can name, from the running sources. Inside a repeater's
    /// template that is its first item, so the paths are the item's own.</summary>
    private IReadOnlyList<ValueEntry> Catalog(RecordValue tree)
    {
        if (_model is null) return [];
        if (_templateParentId is not null) return ValueCatalog.From(tree, []);
        return ValueCatalog.From(tree, _model.Expanded().Layout.Sources.Concat(_model.Parts.Sources));
    }

    /// <summary>The tree a binding for the current selection resolves against. Inside a repeater
    /// template, that is the first item of the repeater's own bound list, not the whole tree.</summary>
    private RecordValue PickerRoot()
    {
        var full = _live?.Tree() ?? ValueTree.Empty;
        if (_templateParentId is null || _model is null) return full;
        if (ComponentLookup.FindRepeater(_model.Parts, _templateParentId) is { Items.Binding: { } items }
            && BindingResolver.Resolve(items, full) is ListValue { Items.Count: > 0 } list) return list.Items[0];
        return full;
    }

    private void EditCurrent(string label, Action<LayoutFile, ComponentDef> mutate)
    {
        if (_model is null || CurrentId() is not { } id) return;
        // Captured, not read inside the callback: the pair (parent, child) identifies a template
        // child; the child id alone matches the first one in any repeater.
        var parentId = _templateParentId;
        // Through the depth: at copy depth an override on the copy; on a layout with copies the
        // layout-depth lens; at widget depth the overlay template; otherwise the layout itself.
        _model.EditAtDepth(label, l => { if (ComponentLookup.Find(l, parentId, id) is { } f) mutate(l, f.Def); });
    }

    /// <summary>Commit a literal, unless the property already says exactly that: Enter commits and
    /// clears focus, whose LostFocus would commit the same value again as a second undo entry.</summary>
    private void SetLiteral(PropertySchema.Prop prop, string label, string text)
    {
        if (CurrentFound() is { } f && prop.Get(f.Def) is { Binding: null } current && current.LiteralText == text) return;
        EditCurrent($"Set {label}", (_, d) => prop.Set(d, PropertyValue.Literal(text)));
    }

    // ---- literal editors -------------------------------------------------------------------------

    private FrameworkElement LiteralEditor(PropertyRows.Row row, PropertyValue value, string rowId, out FrameworkElement? below)
    {
        below = null;
        var prop = row.Prop;
        if (row.Percent) return PercentEditor(row, value);
        return prop.Editor switch
        {
            PropertySchema.Editor.Enum => EnumEditor(row, value),
            PropertySchema.Editor.Font => FontEditor(row, value),
            PropertySchema.Editor.Color => ColourEditor(rowId, row.Label, value.LiteralText ?? "", hex => SetLiteral(prop, row.Label, hex), out below),
            PropertySchema.Editor.Path => PathEditor(row, value),
            PropertySchema.Editor.AutoNumber => AutoNumberEditor(row, value),
            _ => TextEditor(row, value),
        };
    }

    private TextBox TextEditor(PropertyRows.Row row, PropertyValue value)
    {
        var box = new TextBox { Text = value.LiteralText ?? "" };
        OnCommit(box, () => SetLiteral(row.Prop, row.Label, box.Text));
        return box;
    }

    /// <summary>90 on screen, 0.9 in the file (<see cref="PropertyRows.PercentText"/>). Anything
    /// that is not a number puts the box back.</summary>
    private FrameworkElement PercentEditor(PropertyRows.Row row, PropertyValue value)
    {
        var box = new TextBox { Text = PropertyRows.PercentText(value) ?? value.LiteralText ?? "" };
        OnCommit(box, () =>
        {
            var now = CurrentFound() is { } f ? row.Prop.Get(f.Def) ?? value : value;
            if (PropertyRows.FromPercentText(box.Text) is not { } literal) { box.Text = PropertyRows.PercentText(now) ?? now.LiteralText ?? ""; return; }
            box.Text = PropertyRows.PercentText(literal) ?? box.Text;
            SetLiteral(row.Prop, row.Label, literal.LiteralText ?? "");
        });
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var sign = Label("%");
        sign.Width = double.NaN;
        sign.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(sign, 1);
        grid.Children.Add(box);
        grid.Children.Add(sign);
        return grid;
    }

    /// <summary>A pixel size the renderer may work out for itself: an ordinary box with "auto" greyed
    /// behind it while empty. Clearing it writes the sentinel back; anything unparseable snaps back.</summary>
    private FrameworkElement AutoNumberEditor(PropertyRows.Row row, PropertyValue value)
    {
        var box = new TextBox { Text = PropertySchema.AutoNumberText(value) };
        var hint = Hint(PropertySchema.Auto);
        hint.Margin = new Thickness(11, 0, 0, 0);
        hint.VerticalAlignment = VerticalAlignment.Center;
        hint.IsHitTestVisible = false;
        void ShowHint() => hint.Visibility = box.Text.Trim().Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowHint();
        box.TextChanged += (_, _) => ShowHint();
        OnCommit(box, () =>
        {
            var current = CurrentFound() is { } found && row.Prop.Get(found.Def) is { Binding: null } live ? live.LiteralText : value.LiteralText;
            var literal = PropertySchema.AutoNumberLiteral(box.Text, current);
            box.Text = string.Equals(literal, PropertySchema.Auto, StringComparison.Ordinal) ? "" : literal;
            ShowHint();
            SetLiteral(row.Prop, row.Label, literal);
        });
        var grid = new Grid();
        grid.Children.Add(box);
        grid.Children.Add(hint);
        return grid;
    }

    private ComboBox EnumEditor(PropertyRows.Row row, PropertyValue value)
    {
        var choices = row.Prop.Choices ?? [];
        var combo = new ComboBox { ItemsSource = choices };
        combo.SelectedItem = choices.FirstOrDefault(c => string.Equals(c, value.LiteralText, StringComparison.OrdinalIgnoreCase)) ?? choices.FirstOrDefault();
        combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is string s) SetLiteral(row.Prop, row.Label, s); };
        return combo;
    }

    private ComboBox FontEditor(PropertyRows.Row row, PropertyValue value)
    {
        var families = Fonts.SystemFontFamilies.OrderBy(f => f.Source, StringComparer.OrdinalIgnoreCase).ToList();
        var combo = new ComboBox { ItemsSource = families, DisplayMemberPath = "Source" };
        combo.SelectedItem = families.FirstOrDefault(f => string.Equals(f.Source, value.LiteralText, StringComparison.OrdinalIgnoreCase));
        combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is FontFamily f) SetLiteral(row.Prop, row.Label, f.Source); };
        return combo;
    }

    private FrameworkElement PathEditor(PropertyRows.Row row, PropertyValue value)
    {
        var panel = new DockPanel();
        var browse = new Button { Content = "Browse...", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(8, 4, 8, 4) };
        AutomationProperties.SetName(browse, $"Browse for {row.Label}");
        DockPanel.SetDock(browse, Dock.Right);
        var box = new TextBox { Text = value.LiteralText ?? "" };
        OnCommit(box, () => SetLiteral(row.Prop, row.Label, box.Text.Trim()));
        browse.Click += (_, _) =>
        {
            var dir = IOPath.GetDirectoryName(box.Text);
            var dlg = new Microsoft.Win32.OpenFileDialog { InitialDirectory = !string.IsNullOrEmpty(dir) && Directory.Exists(dir) ? dir : null };
            if (dlg.ShowDialog(Window.GetWindow(this)) == true) { box.Text = dlg.FileName; SetLiteral(row.Prop, row.Label, dlg.FileName); }
        };
        panel.Children.Add(browse);
        panel.Children.Add(box);
        return panel;
    }

    /// <summary>A swatch over the checkerboard and the hex; pressing it opens a <see cref="ColorPicker"/>
    /// under the row. While a drag or a run of arrow keys is under way the canvas shows each colour
    /// as a render-only preview (<see cref="DesignerModel.Transient"/>); the release, or the key-up,
    /// is the one commit and the one undo entry, as a hex typed in or a swatch picked is.</summary>
    private FrameworkElement ColourEditor(string rowId, string label, string hex, Action<string> commit, out FrameworkElement? below)
    {
        below = null;
        var fill = new Border { CornerRadius = new CornerRadius(3) };
        if (ColorModel.TryParseHex(hex, out var c)) fill.Background = new SolidColorBrush(Color.FromArgb(c.A, c.R, c.G, c.B));
        var swatch = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1), Child = fill };
        swatch.SetResourceReference(Border.BackgroundProperty, "Checker");
        swatch.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
        var text = new TextBlock { Text = hex, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(swatch);
        content.Children.Add(text);
        var open = _openColour == rowId;
        // Open, the picker's own hex box says it (and takes typing): once is enough.
        text.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
        var toggle = new ToggleButton { Content = content, IsChecked = open, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(6, 4, 6, 4) };
        AutomationProperties.SetHelpText(toggle, hex);
        toggle.Click += (_, _) => { _openColour = toggle.IsChecked == true ? rowId : null; Render(); };
        if (open)
        {
            var picker = new ColorPicker { Margin = new Thickness(0, 8, 0, 8), Swatches = ColoursInUse() };
            if (ColorModel.TryParseHex(hex, out _)) picker.Value = hex;
            AutomationProperties.SetName(picker, label.EndsWith("colour", StringComparison.OrdinalIgnoreCase) ? $"{label} picker" : $"{label} colour picker");
            AutomationProperties.SetAutomationId(picker, rowId + ":picker");
            picker.ValueChanged += (_, e) =>
            {
                if (_model is null) return;
                if (!e.IsFinal) { _model.Transient(() => commit(e.NewValue)); return; }
                _model.EndTransient();
                commit(e.NewValue);
            };
            below = picker;
        }
        return toggle;
    }

    /// <summary>The colours on the canvas now: every copy's and loose part's (the expansion), and at
    /// widget depth the widget's own parts, which may not be placed anywhere.</summary>
    private IReadOnlyList<string> ColoursInUse()
        => _model is null ? [] : ColorModel.InUse(_model.Expanded().Layout.Components.Concat(_model.Depth.Kind == DepthKind.Widget ? _model.Parts.Components : []));

    /// <summary>Enter commits; leaving the box commits. The rebuild that follows puts the focus back
    /// in the same row (<see cref="RestoreFocus"/>).</summary>
    private void OnCommit(TextBox box, Action commit)
    {
        // A box a rebuild has already replaced says what the row said before it: a Reset from the
        // row menu would otherwise be undone by the old box losing the focus afterwards.
        void Guarded() { if (Root.IsAncestorOf(box)) commit(); }
        box.LostFocus += (_, _) => Guarded();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Guarded(); e.Handled = true; } };
    }

    // ---- the row ---------------------------------------------------------------------------------

    private static string RowId(ComponentDef def, string name) => def.Id + "/" + name;

    /// <summary>One row: the override marker, the label, the value, the Bind icon and the "..." menu.
    /// The icons show while the row is hovered (<c>RowIconButton</c> in App.xaml) and are not tab
    /// stops; the menu is also the row control's context menu, so the keyboard reaches every action.</summary>
    private FrameworkElement Shell(string rowId, string label, FrameworkElement editor, bool overridden, Action? bind, ContextMenu? menu, FrameworkElement? below)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 6), Background = Brushes.Transparent };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        // The two icon columns are always there, so every row's value ends at the same edge.
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        if (overridden)
        {
            var marker = new Border { ToolTip = OverriddenNote };
            marker.SetResourceReference(StyleProperty, "OverrideMarker");
            grid.Children.Add(marker);
        }

        var text = Label(label);
        // Level with the first line of the value, which stays put when the editor grows (an open
        // chip search, a two-box row).
        text.VerticalAlignment = VerticalAlignment.Top;
        text.Margin = new Thickness(0, 7, 8, 0);
        if (overridden) text.FontWeight = FontWeights.SemiBold;
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        Grid.SetColumn(editor, 2);
        // A chip takes the whole row: the pill is indented to the value column, and its search opens
        // under the label too, because the value column alone truncates every row of it.
        if (editor is BindingChip chip)
        {
            Grid.SetColumn(chip, 1);
            Grid.SetColumnSpan(chip, 4);
            chip.Chip.Margin = new Thickness(grid.ColumnDefinitions[1].Width.Value, 0, 0, 0);
        }
        grid.Children.Add(editor);
        if (editor is not BindingChip && Primary(editor) is { } primary)
        {
            if (string.IsNullOrEmpty(AutomationProperties.GetName(primary))) AutomationProperties.SetName(primary, label);
            if (string.IsNullOrEmpty(AutomationProperties.GetAutomationId(primary))) AutomationProperties.SetAutomationId(primary, rowId);
        }
        foreach (var control in Controls(editor))
        {
            if (menu is not null) control.ContextMenu = menu;
            if (!overridden) continue;
            var help = AutomationProperties.GetHelpText(control);
            AutomationProperties.SetHelpText(control, string.IsNullOrEmpty(help) ? OverriddenNote : OverriddenNote + ", " + help);
            AutomationProperties.SetItemStatus(control, "overridden");
        }

        if (bind is not null)
        {
            var b = IconButton("", $"Bind {label}", "Bind to a live value");
            b.Click += (_, _) => bind();
            Grid.SetColumn(b, 3);
            grid.Children.Add(b);
        }
        if (menu is not null)
        {
            var more = IconButton("", $"More for {label}", overridden ? OverriddenNote + ": Reset or Push to widget" : "More");
            // An overridden row keeps its menu in view: the marker says there is something to do.
            if (overridden) more.Opacity = 1;
            more.Click += (_, _) => { menu.PlacementTarget = more; menu.Placement = PlacementMode.Bottom; menu.IsOpen = true; };
            Grid.SetColumn(more, 4);
            grid.Children.Add(more);
        }
        if (below is not null)
        {
            Grid.SetRow(below, 1);
            Grid.SetColumn(below, 1);
            Grid.SetColumnSpan(below, 4);
            grid.Children.Add(below);
        }
        if (overridden) Grid.SetRowSpan(grid.Children[0], below is null ? 1 : 2);
        return grid;
    }

    private ContextMenu? RowMenu(string label, Action? bind, Action? unbind, string? copyId, string? overrideKey, (string Header, bool Checked, Action Toggle)? expose)
    {
        var menu = new ContextMenu();
        AutomationProperties.SetName(menu, $"{label} actions");
        void Item(string header, Action act, bool? check = null)
        {
            var item = new MenuItem { Header = header };
            if (check is { } c) { item.IsCheckable = true; item.IsChecked = c; }
            item.Click += (_, _) => act();
            menu.Items.Add(item);
        }
        if (bind is not null) Item("Bind to a live value...", bind);
        if (unbind is not null) Item("Unbind", unbind);
        if (copyId is not null && overrideKey is not null && _model is not null)
        {
            var model = _model;
            Item("Reset to widget", () => Lens.Reset(model, copyId, overrideKey));
            Item("Push to widget", () => Lens.PushToWidget(model, copyId, overrideKey));
        }
        if (expose is { } e) Item(e.Header, e.Toggle, e.Checked);
        return menu.Items.Count == 0 ? null : menu;
    }

    private static Button IconButton(string glyph, string name, string tip)
    {
        // The icon font goes on the glyph, not the button: a string tooltip inherits the button's font
        // and renders as boxes in an icon font.
        var b = new Button { Content = new TextBlock { Text = glyph, FontFamily = new FontFamily(Glyphs), FontSize = 14 }, ToolTip = tip, IsTabStop = false };
        b.SetResourceReference(StyleProperty, "RowIconButton");
        AutomationProperties.SetName(b, name);
        return b;
    }

    /// <summary>The control that takes the focus in an editor: itself, or its first focusable child.</summary>
    private static Control? Primary(FrameworkElement editor) => Controls(editor).OrderBy(c => c is Button ? 1 : 0).FirstOrDefault();

    private static IEnumerable<Control> Controls(DependencyObject root)
    {
        if (root is Control { Focusable: true } c) { yield return c; yield break; }
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var d in Controls(child)) yield return d;
    }

    private static void Identify(UIElement e, string name, string id)
    {
        AutomationProperties.SetName(e, name);
        AutomationProperties.SetAutomationId(e, id);
    }

    // ---- focus survives a rebuild -----------------------------------------------------------------
    // Every commit rebuilds the panel, which would throw the focus away: Tab out of a box, and the
    // box it moved to is gone. The focused control is found again by its AutomationId, and a named
    // part inside it (the colour picker's hue strip) by its name.

    private (string Id, string? Part)? CaptureFocus()
    {
        if (Keyboard.FocusedElement is not Visual focused || !Root.IsAncestorOf(focused)) return null;
        for (DependencyObject? d = focused; d is not null && !ReferenceEquals(d, Root); d = (d is Visual ? VisualTreeHelper.GetParent(d) : null) ?? LogicalTreeHelper.GetParent(d))
            if (d is UIElement u && AutomationProperties.GetAutomationId(u) is { Length: > 0 } id)
                return (id, ReferenceEquals(d, focused) ? null : (focused as FrameworkElement)?.Name);
        return null;
    }

    private void RestoreFocus((string Id, string? Part)? focus)
    {
        if (focus is not { } f) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (Find(Root, f.Id) is not FrameworkElement target) return;
            if (f.Part is { Length: > 0 } part && target.FindName(part) is IInputElement inner) inner.Focus();
            else if (Controls(target).FirstOrDefault(c => c.IsVisible) is { } control) control.Focus();
        }));
    }

    private static DependencyObject? Find(DependencyObject root, string id)
    {
        if (root is UIElement u && AutomationProperties.GetAutomationId(u) == id) return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            if (Find(child, id) is { } hit) return hit;
        return null;
    }

    // ---- nothing selected: the layout's own two settings, and the sources ------------------------

    private string SourcesKey()
    {
        if (_model is null) return "";
        var snaps = _live?.Snapshots ?? Array.Empty<SourceSnapshot>();
        return string.Join(";", _model.Expanded().Layout.Sources.Select(s =>
            s.Name + ":" + (snaps.FirstOrDefault(x => x.Name == s.Name) is { } sn
                ? (sn.LastError is not null ? "err" : sn.LastRefresh?.ToString("HH:mm:ss", CultureInfo.InvariantCulture) ?? "-")
                : "-")));
    }

    private void BuildLayoutPanel()
    {
        var model = _model!;
        _layoutShown = true;
        _sourcesKey = SourcesKey();
        Add(Header("Wallpaper"));

        var photoName = model.Layout.BaseImage.Length == 0 ? "(none)" : IOPath.GetFileName(model.Layout.BaseImage);
        var browse = ActionButton("Change...", "layout:photo");
        AutomationProperties.SetName(browse, $"Change photo, now {photoName}");
        // Under the name, not beside it: beside it left the name about 50 px ("imag...").
        browse.Margin = new Thickness(0, 4, 0, 0);
        browse.HorizontalAlignment = HorizontalAlignment.Left;
        browse.Click += (_, _) =>
        {
            var dir = IOPath.GetDirectoryName(model.Layout.BaseImage);
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp|All files|*.*",
                InitialDirectory = !string.IsNullOrEmpty(dir) && Directory.Exists(dir) ? dir : null,
            };
            if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
            model.Edit("Set photo", l => l.BaseImage = dlg.FileName);
        };
        var name = new TextBlock { Text = photoName, Margin = new Thickness(0, 7, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = model.Layout.BaseImage };
        name.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        var photo = new StackPanel();
        photo.Children.Add(name);
        photo.Children.Add(browse);
        Add(Shell("layout:photo-row", "Photo", photo, false, null, null, null));

        var readout = Label(model.Layout.JpegQuality.ToString(CultureInfo.InvariantCulture));
        readout.Width = 32;
        readout.TextAlignment = TextAlignment.Right;
        var slider = new Slider { Minimum = 60, Maximum = 100, Value = model.Layout.JpegQuality, IsSnapToTickEnabled = true, TickFrequency = 1, SmallChange = 1, LargeChange = 5, VerticalAlignment = VerticalAlignment.Center, MinWidth = 0 };
        slider.ValueChanged += (_, _) => readout.Text = ((int)slider.Value).ToString(CultureInfo.InvariantCulture);
        // One undo entry per drag or key press, not one per pixel of travel.
        void CommitQuality()
        {
            var v = (int)slider.Value;
            if (model.Layout.JpegQuality != v) model.Edit("Set JPEG quality", l => l.JpegQuality = v);
        }
        slider.PreviewMouseUp += (_, _) => CommitQuality();
        slider.KeyUp += (_, _) => CommitQuality();
        var quality = new DockPanel();
        DockPanel.SetDock(readout, Dock.Right);
        quality.Children.Add(readout);
        quality.Children.Add(slider);
        Add(Shell("layout:quality", "JPEG quality", quality, false, null, null, null));

        Add(GroupHeader("Sources"));
        // The expansion's: in v2 a copy's sources exist only there, and they are what runs.
        var sources = model.Expanded().Layout.Sources;
        if (sources.Count == 0) Add(Hint("None yet. Adding a widget adds whatever it needs."));
        var snaps = _live?.Snapshots ?? Array.Empty<SourceSnapshot>();
        foreach (var source in sources)
        {
            var snap = snaps.FirstOrDefault(s => string.Equals(s.Name, source.Name, StringComparison.OrdinalIgnoreCase));
            var row = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            var title = new TextBlock { Text = source.Name, FontSize = 13 };
            title.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
            row.Children.Add(title);
            var state = snap?.LastError is { } err ? $"{source.Type} - failing: {err}"
                : snap?.LastRefresh is { } at ? $"{source.Type} - updated {at.LocalDateTime:HH:mm:ss}"
                : $"{source.Type} - waiting for its first read";
            var status = Hint(state);
            status.Margin = new Thickness(0);
            if (snap?.LastError is not null) status.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCriticalBrush");
            row.Children.Add(status);
            AutomationProperties.SetName(row, source.Name + ": " + state);
            Add(row);
        }
        Add(Hint("Select a widget on the preview to change what it says."));
    }

    // ---- small parts ------------------------------------------------------------------------------

    /// <summary>What kind of thing this is, in the owner's words.</summary>
    private static string TypeName(ComponentDef? c) => c switch
    {
        TextDef => "Text",
        ImageDef => "Picture",
        BarDef => "Bar",
        DialDef => "Dial",
        ShortcutDef => "Shortcut",
        RepeaterDef => "Repeated group",
        _ => "Component",
    };

    private static Button ActionButton(string text, string id)
    {
        var b = new Button { Content = text, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 8) };
        Identify(b, text, id);
        return b;
    }

    private static TextBlock Styled(string text, string style)
    {
        var t = new TextBlock { Text = text };
        t.SetResourceReference(StyleProperty, style);
        return t;
    }

    private static TextBlock Header(string text)
    {
        var t = Styled(text, "PanelHeader");
        t.TextTrimming = TextTrimming.CharacterEllipsis;
        return t;
    }

    private static TextBlock SubHeader(string text)
    {
        var t = Styled(text, "SectionHeader");
        t.Margin = new Thickness(0, 12, 0, 4);
        t.TextTrimming = TextTrimming.CharacterEllipsis;
        return t;
    }

    private static TextBlock GroupHeader(string text) => Styled(text, "PropGroupHeader");

    private static TextBlock Label(string text)
    {
        var t = Styled(text, "PropLabel");
        t.VerticalAlignment = VerticalAlignment.Center;
        return t;
    }

    private static TextBlock Hint(string text)
    {
        var t = Styled(text, "Hint");
        t.TextWrapping = TextWrapping.Wrap;
        t.Margin = new Thickness(0, 0, 0, 8);
        return t;
    }
}
