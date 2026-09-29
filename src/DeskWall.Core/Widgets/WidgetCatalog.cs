namespace DeskWall.Core.Widgets;

/// <summary>Where widget files live, and a lookup by key over one or more folders.</summary>
public static class WidgetCatalog
{
    /// <summary>The widgets shipped beside the executable (replaced by every install).</summary>
    public static string ShippedDir => Path.Combine(AppContext.BaseDirectory, "widgets");

    /// <summary>The owner's own widgets and forks of shipped ones, in the runtime dir.</summary>
    public static string UserDir => Paths.InRuntime("widgets");

    /// <summary>A lookup by key over <paramref name="dirs"/>: a later folder wins over an earlier
    /// one for the same key, only requested keys are read, and results are cached for the life of
    /// the returned function. Returns null for an unknown key; throws for a file that fails to
    /// load, which <see cref="WidgetExpander"/> reports as <see cref="ExpandProblemKind.BrokenWidget"/>.</summary>
    public static Func<string, WidgetTemplate?> Finder(params string[] dirs)
    {
        var cache = new Dictionary<string, WidgetTemplate?>(StringComparer.OrdinalIgnoreCase);
        return key =>
        {
            lock (cache)
            {
                if (cache.TryGetValue(key, out var hit)) return hit;
                // A key comes from a layout file: never let one reach outside the widget folders.
                if (key.Length == 0 || key is "." or ".." || key.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;

                string? path = null;
                var shadows = false;
                foreach (var dir in dirs)
                {
                    var candidate = Path.Combine(dir, key + ".json");
                    if (!File.Exists(candidate)) continue;
                    shadows |= path is not null;
                    path = candidate;
                }
                // A load failure is not cached: the next finder (the next activation) tries again.
                var template = path is null ? null : WidgetTemplate.Load(path);
                if (template is not null) template.OverridesShipped = shadows;
                cache[key] = template;
                return template;
            }
        };
    }

    /// <summary>Every "*.json" in each of <paramref name="dirs"/>, for the designer's gallery. A later
    /// folder's widget replaces an earlier one's of the same key (and is marked
    /// <see cref="WidgetTemplate.OverridesShipped"/>) but keeps the position the key was first seen
    /// at. A folder that does not exist is skipped. Throws for a file that fails to load.</summary>
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
                if (byKey.ContainsKey(template.Key)) template.OverridesShipped = true;
                else order.Add(template.Key);
                byKey[template.Key] = template;
            }
        }
        return order.Select(k => byKey[k]).ToList();
    }

    /// <summary>Whether <paramref name="template"/> was loaded from <see cref="UserDir"/> (the
    /// owner's own file, editable in place) rather than beside the exe. Decided by folder, not key,
    /// because a user file may deliberately shadow a shipped key.</summary>
    public static bool IsUserTemplate(WidgetTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (template.Path is null) return false;
        var dir = Path.GetDirectoryName(Path.GetFullPath(template.Path));
        return dir is not null && string.Equals(dir.TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(UserDir).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Every path a widget with <paramref name="key"/> could be loaded from, in the
    /// user dir only (the only folder the daemon watches, plan D4).</summary>
    public static IReadOnlyList<string> CandidatePaths(string key) => [Path.Combine(UserDir, key + ".json")];
}
