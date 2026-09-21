using System.Text;
using DeskWall.Core;
using DeskWall.Core.Display;
using DeskWall.Core.Layout;

namespace DeskWall.Designer.Model.Widgets;

/// <summary>The four parts the widget editor offers. <c>shortcut</c> and <c>repeater</c> are not
/// here on purpose: a desktop slot and an items binding are layout-level concerns, not a shape you
/// draw on a 172 px canvas.</summary>
public enum PartKind { Text, Image, Bar, Dial }

/// <summary>
/// One widget being edited: a <see cref="DesignerModel"/> over a widget-sized canvas, plus the
/// header fields and knobs a <see cref="WidgetTemplate"/> has and a layout does not.
/// <para>
/// The canvas side is deliberately the same code the layout window uses. A template already turns
/// into a <c>LayoutFile</c>, and the preview, selection, move, resize, align, undo and the
/// properties panel are all written against <c>DesignerModel</c> and the display signature, so a
/// widget document is just that model over a signature of ("widget", width, height, 100) with no
/// base image behind it.
/// </para>
/// </summary>
public sealed class WidgetDocument
{
    /// <summary>The smallest widget worth drawing, and the largest canvas any display here has.</summary>
    public const int MinSide = 8;
    public const int MaxWidth = 3440;
    public const int MaxHeight = 1440;

    /// <summary>A brand new widget is this wide and this tall: the shipped column's width, and
    /// enough height for one line of text.</summary>
    public const int NewWidth = 172;
    public const int NewHeight = 40;

    /// <summary>Knobs in file order, each either the editor's own or one passing through. One list
    /// rather than two, so <see cref="ToTemplate"/> writes them back in the order they were read
    /// and a duplicated widget's file does not reshuffle itself.</summary>
    private sealed record Slot(AdjustableTarget? Target, Knob? PassThrough);

    private readonly List<Slot> _knobs = new();

    private WidgetDocument(DesignerModel model, string name, string description)
    {
        Model = model;
        Name = name;
        Description = description;
        Model.Changed += Prune;
    }

    public DesignerModel Model { get; }
    public string Name { get; set; }
    public string Description { get; set; }
    /// <summary>"top" or "bottom": which end of a column the arranger stacks this from.</summary>
    public string Anchor { get; set; } = "top";
    public string? Requires { get; set; }

    /// <summary>The file being edited, or null for a widget that has never been saved.</summary>
    public string? Path { get; set; }

    /// <summary>The key the file being edited is stored under, so a rename knows which file to
    /// delete. Null for a new widget and for "Duplicate to mine".</summary>
    public string? EditingKey { get; set; }

    public IReadOnlyList<AdjustableTarget> Adjustables => _knobs.Where(k => k.Target is not null).Select(k => k.Target!).ToList();

    /// <summary>Knobs this editor cannot express (a composite, a <c>{token}</c> splice, a binding
    /// write). Shown read-only and written back exactly as they were read.</summary>
    public IReadOnlyList<Knob> PassThroughKnobs => _knobs.Where(k => k.PassThrough is not null).Select(k => k.PassThrough!).ToList();

    /// <summary>The file name (without ".json") this saves as: a slug of the name.</summary>
    public string Key => Slug(Name);

    public static WidgetDocument New()
    {
        var layout = new LayoutFile { BaseImage = "" };
        var model = new DesignerModel(layout, Signature(NewWidth, NewHeight), null);
        return new WidgetDocument(model, "New widget", "");
    }

    /// <summary>Open a template for editing. Everything is deep-copied: the catalog's instances are
    /// shared by the gallery and every placement, and an editor that wrote through to them would
    /// change widgets already on the wallpaper.</summary>
    public static WidgetDocument FromTemplate(WidgetTemplate template, string? path)
    {
        ArgumentNullException.ThrowIfNull(template);
        var layout = new LayoutFile
        {
            BaseImage = "",
            Sources = template.Sources.Select(WidgetJson.CloneSource).ToList(),
            Components = WidgetJson.CloneComponents(template.Components),
        };
        var model = new DesignerModel(layout, Signature(template.Width, template.Height), null);
        var doc = new WidgetDocument(model, template.Name, template.Description)
        {
            Anchor = template.Anchor,
            Requires = template.Requires,
            Path = path,
            EditingKey = path is null ? null : template.Key,
        };
        foreach (var knob in template.Knobs)
        {
            var target = Adjustable.FromKnob(knob, layout.Components);
            // A saved Drive knob's bindings key "{drive}", which resolves to nothing: put the
            // knob's own default letter back so the canvas draws this machine's real data. The
            // token goes back in at ToTemplate, so a save that follows writes the same file.
            if (target is { IsDrive: true }) Adjustable.SetDriveKey(layout.Components, target, knob.Default);
            doc._knobs.Add(new Slot(target, target is null ? knob : null));
        }
        return doc;
    }

    private static DisplaySignature Signature(int w, int h) => new("widget", w, h, 100);

    // ---- size ------------------------------------------------------------------------------

    public void Resize(int width, int height)
        => Model.ResizeCanvas(Math.Clamp(width, MinSide, MaxWidth), Math.Clamp(height, MinSide, MaxHeight));

    /// <summary>Shrink-wrap the widget round what is on it: every part moves so the bounding box
    /// starts at (0, 0) and the canvas becomes that box. One undo entry, because half of it undone
    /// is a widget whose parts hang off its own edge.</summary>
    public void FitToParts()
    {
        var components = Model.Layout.Components;
        if (components.Count == 0) return;
        var minX = components.Min(c => c.Rect.X);
        var minY = components.Min(c => c.Rect.Y);
        var w = Math.Clamp(components.Max(c => c.Rect.Right) - minX, MinSide, MaxWidth);
        var h = Math.Clamp(components.Max(c => c.Rect.Bottom) - minY, MinSide, MaxHeight);
        Model.Edit("Fit to parts", l =>
        {
            foreach (var c in l.Components) c.Rect = c.Rect.Offset(-minX, -minY);
            Model.SetSignatureSize(w, h);
        });
    }

    // ---- parts -----------------------------------------------------------------------------

    /// <summary>Add a part of this kind and hand it back so the caller can select it. Each one
    /// lands a step further down and right than the last, because a pile of parts at (0, 0) looks
    /// like one part and the second click looks like it did nothing.</summary>
    public ComponentDef AddPart(PartKind kind)
    {
        var step = 8 * Model.Layout.Components.Count;
        var def = NewPart(kind, step);
        Model.Add(def);
        return def;
    }

    private static ComponentDef NewPart(PartKind kind, int step) => kind switch
    {
        PartKind.Text => new TextDef
        {
            Id = "text",
            Rect = new Rect(step, step, 120, 24),
            Text = PropertyValue.Literal("Text"),
            Size = PropertyValue.Literal(16),
        },
        PartKind.Image => new ImageDef
        {
            Id = "image",
            Rect = new Rect(step, step, 48, 48),
            Source = PropertyValue.Literal(""),
        },
        PartKind.Bar => new BarDef
        {
            Id = "bar",
            Rect = new Rect(step, step, 120, 6),
            Fraction = PropertyValue.Literal(0.5),
        },
        PartKind.Dial => new DialDef
        {
            Id = "dial",
            Rect = new Rect(step, step, 80, 80),
            Fraction = PropertyValue.Literal(0.5),
        },
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    // ---- sources ---------------------------------------------------------------------------

    public void AddSource(SourceDef def)
    {
        ArgumentNullException.ThrowIfNull(def);
        Model.Edit($"Add {def.Name}", l => l.Sources.Add(def));
    }

    public void ReplaceSource(string name, SourceDef def)
    {
        ArgumentNullException.ThrowIfNull(def);
        Model.Edit($"Edit {name}", l =>
        {
            var i = l.Sources.FindIndex(s => string.Equals(s.Name, name, StringComparison.Ordinal));
            if (i >= 0) l.Sources[i] = def; else l.Sources.Add(def);
        });
        // A rename takes its knobs with it; the knob's own id and label do not change.
        if (string.Equals(name, def.Name, StringComparison.Ordinal)) return;
        for (var i = 0; i < _knobs.Count; i++)
            if (_knobs[i].Target is { IsComponent: false } t && string.Equals(t.SourceName, name, StringComparison.Ordinal))
                _knobs[i] = new Slot(AdjustableTarget.ForSource(def.Name, t.SettingKey!, t.Label, t.Id), null);
    }

    /// <summary>Removing a source a part binds to is allowed: the canvas then shows the unresolved
    /// value exactly as the daemon would, which is more use than a refusal.</summary>
    public void RemoveSource(string name)
        => Model.Edit($"Remove {name}", l => l.Sources.RemoveAll(s => string.Equals(s.Name, name, StringComparison.Ordinal)));

    // ---- adjustables -------------------------------------------------------------------------

    public bool IsAdjustable(string componentId, string property)
        => _knobs.Any(k => k.Target?.Targets(componentId, property) == true);

    public bool IsSettingAdjustable(string sourceName, string settingKey)
        => _knobs.Any(k => k.Target?.TargetsSetting(sourceName, settingKey) == true);

    /// <summary>Expose a component property as a knob, or take it back off. Returns true when it is
    /// exposed afterwards.</summary>
    public bool ToggleAdjustable(string componentId, string property)
    {
        var existing = _knobs.FirstOrDefault(k => k.Target?.Targets(componentId, property) == true);
        if (existing is not null)
        {
            // A Drive knob holds several targets; taking one off leaves the knob for the rest.
            if (existing.Target is { IsDrive: true } drive && drive.DriveTargets.Count > 1) drive.RemoveDriveTarget(componentId, property);
            else _knobs.Remove(existing);
            return false;
        }
        if (Model.Find(componentId) is not { } def) return false;
        var prop = PropertySchema.For(def).FirstOrDefault(p => string.Equals(p.Name, property, StringComparison.OrdinalIgnoreCase));
        if (prop is null) return false;
        // One picker for the whole widget: a drive-keyed binding joins the Drive knob already
        // here rather than making a second one, so the bar and its caption cannot disagree.
        if (Adjustable.CanAdjustAsDrive(def, prop))
        {
            if (_knobs.Select(k => k.Target).OfType<AdjustableTarget>().FirstOrDefault(t => t.IsDrive) is { } existingDrive)
            {
                existingDrive.AddDriveTarget(componentId, prop.Name);
                return true;
            }
            var driveLabel = UniqueLabel("Drive", componentId);
            _knobs.Add(new Slot(AdjustableTarget.ForDrive(componentId, prop.Name, driveLabel, UniqueId(Slug(driveLabel))), null));
            return true;
        }
        if (!Adjustable.CanAdjust(def, prop)) return false;
        var label = UniqueLabel(prop.Name, componentId);
        _knobs.Add(new Slot(AdjustableTarget.ForComponent(componentId, prop.Name, label, UniqueId(Slug(label))), null));
        return true;
    }

    public bool ToggleSettingAdjustable(string sourceName, string settingKey)
    {
        var existing = _knobs.FirstOrDefault(k => k.Target?.TargetsSetting(sourceName, settingKey) == true);
        if (existing is not null) { _knobs.Remove(existing); return false; }
        var label = UniqueLabel(settingKey, sourceName);
        _knobs.Add(new Slot(AdjustableTarget.ForSource(sourceName, settingKey, label, UniqueId(Slug(label))), null));
        return true;
    }

    public void RemoveAdjustable(AdjustableTarget target)
        => _knobs.RemoveAll(k => ReferenceEquals(k.Target, target));

    /// <summary>The name on its own, or the name with the part it belongs to when another knob
    /// already answers to it: two parts both called "Text" in the knobs panel is two knobs nobody
    /// can tell apart.</summary>
    private string UniqueLabel(string name, string owner)
        => _knobs.Any(k => string.Equals(k.Target?.Label ?? k.PassThrough?.Label, name, StringComparison.OrdinalIgnoreCase))
            ? $"{name} ({owner})"
            : name;

    private string UniqueId(string baseId)
    {
        var id = baseId;
        var n = 2;
        while (_knobs.Any(k => string.Equals(k.Target?.Id ?? k.PassThrough?.Id, id, StringComparison.OrdinalIgnoreCase)))
            id = $"{baseId}-{n++}";
        return id;
    }

    /// <summary>Drop any knob whose target has gone or has become a binding. Runs on every model
    /// change, because a part can be deleted from the canvas with the Delete key, which knows
    /// nothing about knobs.</summary>
    private void Prune()
    {
        // A Drive knob loses the one target that went, not the whole knob: deleting a drive
        // widget's caption must not take the picker off its bar.
        foreach (var slot in _knobs)
            if (slot.Target is { IsDrive: true } drive) drive.KeepDriveTargets(Adjustable.LiveDriveTargets(this, drive));
        _knobs.RemoveAll(k => k.Target is not null && Adjustable.ToKnob(this, k.Target) is null);
    }

    // ---- out -----------------------------------------------------------------------------------

    /// <summary>The template this document currently describes. Knob defaults are read here, from
    /// the values on the canvas, so editing an exposed value moves its default with it and there is
    /// no second copy to fall out of step.</summary>
    public WidgetTemplate ToTemplate()
    {
        // Knobs first, from the document: a Drive knob's default is the real letter the canvas is
        // drawing. Only the copy below is then tokenised, so the widget on screen keeps drawing
        // real data and the file is portable. Tokenising is idempotent -- it rewrites whatever
        // key is there -- so saving twice writes the same file.
        var knobs = _knobs.Select(k => k.PassThrough ?? Adjustable.ToKnob(this, k.Target!)).OfType<Knob>().ToList();
        var components = WidgetJson.CloneComponents(Model.Layout.Components);
        foreach (var target in _knobs.Select(k => k.Target).OfType<AdjustableTarget>().Where(t => t.IsDrive))
            Adjustable.SetDriveKey(components, target, "{" + Adjustable.DriveToken + "}");

        return new WidgetTemplate
        {
            Name = Name,
            Key = Key,
            Description = Description,
            Width = Model.Signature.Width,
            Height = Model.Signature.Height,
            Anchor = Anchor,
            Requires = Requires,
            Sources = Model.Layout.Sources.Select(WidgetJson.CloneSource).ToList(),
            Components = components,
            Knobs = knobs,
        };
    }

    /// <summary>Write this widget into the user's own templates folder, refusing rather than
    /// writing anything the gallery could not read back. Returns the file it landed in.</summary>
    public string Save()
        => WidgetTemplateWriter.Save(this, WidgetCatalog.Load(WidgetCatalog.ShippedDir).Select(t => t.Key).ToList(), WidgetCatalog.UserDir);

    /// <summary>Lower case, runs of letters and digits joined by hyphens, and never empty: what a
    /// name becomes as a file name and as a knob id.</summary>
    public static string Slug(string text)
    {
        var sb = new StringBuilder();
        foreach (var ch in (text ?? "").ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(ch)) sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var slug = sb.ToString().Trim('-');
        return slug.Length == 0 ? "widget" : slug;
    }
}
