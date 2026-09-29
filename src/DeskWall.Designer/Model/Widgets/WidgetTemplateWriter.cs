using System.Text.Json;
using DeskWall.Core.Widgets;

namespace DeskWall.Designer.Model.Widgets;

/// <summary>
/// A widget template as the JSON of its <c>widgets/&lt;key&gt;.json</c> file: what
/// <see cref="DesignerModel.Save"/> writes for each widget in the overlay, and the overlay's undo
/// snapshot.
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
}
