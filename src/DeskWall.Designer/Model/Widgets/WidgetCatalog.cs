using System.IO;
using DeskWall.Core;

namespace DeskWall.Designer.Model;

/// <summary>Every widget the gallery can offer: the set that ships beside the exe, plus anything the
/// owner has dropped in the runtime dir's <c>widgets\</c> folder, which wins on a key clash so a
/// local edit of a shipped widget is possible without touching the install.</summary>
public static class WidgetCatalog
{
    public static string ShippedDir => Path.Combine(AppContext.BaseDirectory, "widgets");

    public static string UserDir => Paths.InRuntime("widgets");

    /// <summary>Load in order; a later directory's key replaces an earlier one's. A directory that
    /// is not there is not an error (a dev build may have no user widgets at all); a file that will
    /// not parse is skipped rather than costing the owner the whole gallery.</summary>
    public static IReadOnlyList<WidgetTemplate> Load(params string[] dirs)
    {
        var byKey = new Dictionary<string, WidgetTemplate>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                WidgetTemplate t;
                try { t = WidgetTemplate.Load(file); }
                catch (Exception ex) when (ex is FormatException or IOException) { continue; }
                if (!byKey.ContainsKey(t.Key)) order.Add(t.Key);
                byKey[t.Key] = t;
            }
        }
        return order.Select(k => byKey[k]).ToList();
    }
}
