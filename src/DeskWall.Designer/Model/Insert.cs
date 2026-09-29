using DeskWall.Core;
using DeskWall.Core.Layout;
using DeskWall.Core.Widgets;
using DeskWall.Designer.Model.Widgets;

namespace DeskWall.Designer.Model;

/// <summary>What the Insert panel puts on the canvas when something is dropped on it, or inserted
/// from the keyboard at the canvas centre: a part, a copy of a widget, and the text typed into a new
/// text part. Values go through <see cref="DropPlan"/>; this is the rest.</summary>
public static class Insert
{
    /// <summary>The local sources the Data panel runs besides the open document's own, so the owner
    /// can drag a value no source in the layout produces yet and the drop adds it
    /// (<see cref="DropPlan"/>). Local and cheap only: nothing that needs a URL, a key or a command
    /// line to mean anything. <c>disks</c> keeps the shipped Drives widget's five minutes.</summary>
    public static readonly IReadOnlyList<SourceDef> DefaultSources =
    [
        new() { Name = "time", Type = "time" },
        new() { Name = "hardware", Type = "hardware" },
        new() { Name = "disks", Type = "disks", EverySeconds = 300 },
        new() { Name = "system", Type = "system" },
    ];

    /// <summary>The sources the Data panel runs: at widget depth the widget's own first, then the
    /// layout's own, then <see cref="DefaultSources"/>; the first of a name wins. Not the expansion's
    /// sources: a copy's widget brings its own, and a renamed clash (<c>hardware2</c>) would be added
    /// to the layout under that name by a drop.</summary>
    public static IReadOnlyList<SourceDef> DataSources(DesignerModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var own = model.Depth.Kind == DepthKind.Widget ? model.Parts.Sources.Concat(model.Layout.Sources) : model.Layout.Sources;
        var result = new List<SourceDef>();
        foreach (var s in own.Concat(DefaultSources))
            if (!result.Exists(r => string.Equals(r.Name, s.Name, StringComparison.OrdinalIgnoreCase))) result.Add(s);
        return result;
    }

    /// <summary>The frontmost part under a canvas point, as <see cref="DesignerModel.Parts"/> names
    /// it (a copy's part at layout depth too, so a value dropped on it binds that part), or null.</summary>
    public static string? PartAt(DesignerModel model, double x, double y)
    {
        ArgumentNullException.ThrowIfNull(model);
        return model.Parts.Components
            .Select((c, i) => (c, i))
            .Where(p => x >= p.c.Rect.X && x < p.c.Rect.Right && y >= p.c.Rect.Y && y < p.c.Rect.Bottom)
            .OrderBy(p => p.c.Z).ThenBy(p => p.i)
            .Select(p => p.c.Id)
            .LastOrDefault();
    }

    /// <summary>Whether a part can be added at the current depth. Not at copy depth: a copy's
    /// overrides cannot add a part (Ctrl+Alt+K edits its widget).</summary>
    public static bool CanAddPart(DesignerModel model) => model.Depth.Kind != DepthKind.Copy;

    /// <summary>A new part of <paramref name="kind"/> centred on (x, y), as one undo entry: a loose
    /// component at layout depth, a part of the widget at widget depth. Returns its id, or null at copy
    /// depth. Text is centred in its box, so the word lands where it was dropped.</summary>
    public static string? Part(DesignerModel model, PartKind kind, int x, int y)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!CanAddPart(model)) return null;
        var def = WidgetDocument.NewPart(kind);
        if (def is TextDef text) text.Align = PropertyValue.Literal("center");
        def.Rect = def.Rect with { X = x - def.Rect.W / 2, Y = y - def.Rect.H / 2 };
        // The same id rule as DropPlan: at widget depth prefixed with the origin copy, which the lens
        // strips, so the new part cannot collide with one of the template's.
        var prefix = model.Depth is { Kind: DepthKind.Widget, CopyId: { } origin } ? origin + "." : "";
        var id = "";
        model.EditAtDepth("Add " + kind.ToString().ToLowerInvariant(), l =>
        {
            id = prefix + def.Id;
            for (var n = 2; l.Components.Any(c => c.Id == id); n++) id = $"{prefix}{def.Id}-{n}";
            def.Id = id;
            l.Components.Add(def);
        });
        return id;
    }

    /// <summary>A copy of <paramref name="template"/> centred on (x, y), as one undo entry. Returns
    /// the copy's id, or null away from layout depth (widgets do not nest).</summary>
    public static string? Widget(DesignerModel model, WidgetTemplate template, int x, int y)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(template);
        if (model.Depth.Kind != DepthKind.Layout) return null;
        string? id = null;
        model.Edit($"Add {template.Name}", l => id = Copies.Add(l, template, x - template.Width / 2, y - template.Height / 2));
        return id;
    }

    /// <summary>What was typed into a text part on the canvas, as its literal text. One undo entry;
    /// none when nothing changed.</summary>
    public static void SetText(DesignerModel model, string partId, string text)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.Find(partId) is not TextDef { Text: var current } || (!current.IsBound && current.LiteralText == text)) return;
        model.EditAtDepth("Edit text", l =>
        {
            if (l.Components.FirstOrDefault(c => c.Id == partId) is TextDef t) t.Text = PropertyValue.Literal(text);
        });
    }
}
