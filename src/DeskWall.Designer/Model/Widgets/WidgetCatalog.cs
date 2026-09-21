using System.IO;
using DeskWall.Core;

namespace DeskWall.Designer.Model.Widgets;

/// <summary>Where widget templates live and how they are loaded into the gallery.</summary>
public static class WidgetCatalog
{
    /// <summary>Loads every "*.json" in each directory, in the order given. A later directory's
    /// template with the same key replaces an earlier one (so the runtime dir wins over the
    /// shipped dir when both are passed shipped-then-runtime) but keeps the position the key was
    /// first seen at, so the gallery's order does not jump around just because a user template
    /// overrides a shipped one. A directory that does not exist is skipped, not an error.</summary>
    public static IReadOnlyList<WidgetTemplate> Load(params string[] dirs)
    {
        var order = new List<string>();
        var byKey = new Dictionary<string, WidgetTemplate>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var template = WidgetTemplate.Load(file);
                if (!byKey.ContainsKey(template.Key)) order.Add(template.Key);
                byKey[template.Key] = template;
            }
        }
        return order.Select(k => byKey[k]).ToList();
    }

    /// <summary>Shipped beside the designer exe, linked from the repo's widgets/ (csproj).</summary>
    public static string ShippedDir => Path.Combine(AppContext.BaseDirectory, "widgets");

    /// <summary>A user's own templates, read after the shipped ones so they can override a key.</summary>
    public static string UserDir => Paths.InRuntime("widgets");
}
