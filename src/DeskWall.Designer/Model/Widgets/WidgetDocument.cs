using System.Text;
using DeskWall.Core;
using DeskWall.Core.Display;
using DeskWall.Core.Layout;
using DeskWall.Core.Widgets;

namespace DeskWall.Designer.Model.Widgets;

/// <summary>The four parts the Insert panel offers. <c>shortcut</c> and <c>repeater</c> are not
/// here on purpose: a desktop slot and an items binding are layout-level concerns, not a shape you
/// draw on a 172 px canvas.</summary>
public enum PartKind { Text, Image, Bar, Dial, Line }

/// <summary>
/// One widget as an editable draft: its parts and sources as a <see cref="DesignerModel"/> over a
/// widget-sized canvas, plus the header fields and knobs a <see cref="WidgetTemplate"/> has and a
/// layout does not. The main window's widget-level edits (name, description, anchor, knobs, the
/// widget's own sources) go <see cref="FromTemplate"/>, change, <see cref="ToTemplate"/>, through
/// <see cref="Lens.EditWidget"/>: this is the one place that knows how a knob maps to what it sets
/// (<see cref="Adjustable"/>).
/// </summary>
public sealed class WidgetDocument
{
    /// <summary>Knobs in file order, each either the editor's own or one passing through. One list
    /// rather than two, so <see cref="ToTemplate"/> writes them back in the order they were read
    /// and a duplicated widget's file does not reshuffle itself.</summary>
    private sealed record Slot(AdjustableTarget? Target, Knob? PassThrough);

    private readonly List<Slot> _knobs = new();
    private readonly string _key;

    private WidgetDocument(DesignerModel model, string key, string name, string description)
    {
        Model = model;
        _key = key;
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

    public IReadOnlyList<AdjustableTarget> Adjustables => _knobs.Where(k => k.Target is not null).Select(k => k.Target!).ToList();

    /// <summary>Knobs this editor cannot express (a composite, a <c>{token}</c> splice, a binding
    /// write). Shown read-only and written back exactly as they were read.</summary>
    public IReadOnlyList<Knob> PassThroughKnobs => _knobs.Where(k => k.PassThrough is not null).Select(k => k.PassThrough!).ToList();

    /// <summary>The template's key: fixed once the widget exists, because placed copies link to a
    /// widget by key, so a rename changes <see cref="Name"/> only (plan D2). A template with no key
    /// (a test's) takes a slug of its name.</summary>
    public string Key => _key.Length > 0 ? _key : Slug(Name);

    /// <summary>Open a template for editing. Everything is deep-copied: the catalog's instances are
    /// shared by the Insert panel and every copy, and an edit that wrote through to them would
    /// change widgets already on the wallpaper.</summary>
    public static WidgetDocument FromTemplate(WidgetTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        var layout = new LayoutFile
        {
            BaseImage = "",
            Sources = template.Sources.Select(CloneSource).ToList(),
            Components = CloneComponents(template.Components),
        };
        var model = new DesignerModel(layout, Signature(template.Width, template.Height), null);
        var doc = new WidgetDocument(model, template.Key, template.Name, template.Description)
        {
            Anchor = template.Anchor,
            Requires = template.Requires,
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

    // ---- parts -----------------------------------------------------------------------------

    /// <summary>A new part of this kind, at (<paramref name="step"/>, <paramref name="step"/>): the
    /// Insert panel's parts.</summary>
    public static ComponentDef NewPart(PartKind kind, int step = 0) => kind switch
    {
        PartKind.Text => new TextDef
        {
            Id = "text",
            Rect = new Rect(step, step, 120, 24),
            Text = PropertyValue.Literal("Text"),
            Size = PropertyValue.Literal(16),
            Effect = PropertyValue.Literal("none"),
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
        PartKind.Line => new LineDef { Id = "line", Rect = new Rect(step, step, 320, 100), Values = PropertyValue.Bound(DeskWall.Core.Bindings.BindingParser.Parse("hardware.cpuHistory")) },
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
        var components = CloneComponents(Model.Layout.Components);
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
            Sources = Model.Layout.Sources.Select(CloneSource).ToList(),
            Components = components,
            Knobs = knobs,
        };
    }


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

    // Deep copies through the one component serializer, so every subtype's fields copy.
    private static List<ComponentDef> CloneComponents(IEnumerable<ComponentDef> defs)
        => LayoutFile.Parse(new LayoutFile { BaseImage = "", Components = [.. defs] }.ToJson()).Components;

    private static SourceDef CloneSource(SourceDef s) => new()
    {
        Name = s.Name,
        Type = s.Type,
        EverySeconds = s.EverySeconds,
        Settings = new Dictionary<string, string>(s.Settings),
    };
}
