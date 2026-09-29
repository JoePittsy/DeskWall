using System.IO;
using System.Text.Json;
using DeskWall.Core.Widgets;

namespace DeskWall.Designer.Model.Widgets;

/// <summary>
/// Writing a widget template back to <c>widgets/&lt;key&gt;.json</c>.
/// <para>
/// The file is serialised through Core's source-generated <see cref="WidgetJsonContext"/>, the same
/// context <see cref="WidgetTemplate.Load"/> reads with, so there is one widget-file shape and one
/// component serializer, not a second place for a new property to be forgotten.
/// </para>
/// <para>
/// The contract is the round trip: <see cref="WidgetTemplate.Load"/> of what this writes is the
/// template that went in. <c>WidgetTemplateWriterTests</c> asserts it for every shipped widget.
/// </para>
/// </summary>
public static class WidgetTemplateWriter
{
    /// <summary>The doctrine limit <see cref="WidgetTemplate.Load"/> enforces on the way back in.
    /// Refused here too, with a sentence rather than a FormatException on the next launch.</summary>
    public const int MaxKnobs = 5;

    public static string ToJson(WidgetTemplate template)
        => JsonSerializer.Serialize(ToFile(template), WidgetJsonContext.Default.WidgetTemplateFile);

    private static WidgetTemplateFile ToFile(WidgetTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        return new WidgetTemplateFile
        {
            Name = template.Name,
            Description = template.Description,
            Size = [template.Width, template.Height],
            // Omitted at its default, so a plain widget's file has nothing in it that is not about
            // the widget.
            Anchor = string.Equals(template.Anchor, "top", StringComparison.Ordinal) ? null : template.Anchor,
            Requires = template.Requires,
            Sources = [.. template.Sources],
            Components = [.. template.Components],
            Knobs = template.Knobs.Select(k => new KnobFile
            {
                Id = k.Id,
                Label = k.Label,
                Type = k.Type.ToString().ToLowerInvariant(),
                Default = k.Default,
                Sets = [.. k.Sets],
                Choices = k.Choices?.ToList(),
                Min = k.Min,
                Max = k.Max,
            }).ToList(),
        };
    }

    /// <summary>Write the document into <paramref name="userDir"/> as <c>&lt;key&gt;.json</c> and
    /// return the path. Refuses rather than writes when the result would not load, or when a new
    /// widget would land on a key that already exists. The key never changes once a widget exists
    /// (plan D2), so a rename rewrites the same file and nothing is ever deleted: copies placed in
    /// any layout keep finding it.</summary>
    /// <param name="shippedKeys">the keys of the widgets that ship beside the exe. A user file of the
    /// same key shadows one, which is right for a shipped widget opened for editing and a trap for a
    /// new widget that happens to share its name.</param>
    public static string Save(WidgetDocument document, IReadOnlyCollection<string> shippedKeys, string userDir)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(shippedKeys);

        var template = document.ToTemplate();
        if (string.IsNullOrWhiteSpace(template.Name)) throw new InvalidOperationException("A widget needs a name.");
        if (string.IsNullOrWhiteSpace(template.Description))
            throw new InvalidOperationException("A widget needs a description: it is all the gallery card has to go on.");
        if (template.Knobs.Count > MaxKnobs)
            throw new InvalidOperationException($"A widget can have at most {MaxKnobs} adjustable settings; this one has {template.Knobs.Count}.");

        var key = document.Key;
        var path = Path.Combine(userDir, key + ".json");
        if (document.EditingKey is null)
        {
            if (shippedKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException($"A shipped widget is already called '{template.Name}'. Pick another name.");
            // Overwriting another of the owner's widgets would also change every copy linked to it.
            if (File.Exists(path))
                throw new InvalidOperationException($"One of your widgets is already called '{template.Name}'. Pick another name.");
        }

        Directory.CreateDirectory(userDir);
        File.WriteAllText(path, ToJson(template));

        document.Path = path;
        document.EditingKey = key;
        return path;
    }
}
