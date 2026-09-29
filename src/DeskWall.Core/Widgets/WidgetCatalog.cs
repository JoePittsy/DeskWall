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

    /// <summary>Every path a widget with <paramref name="key"/> could be loaded from, in the
    /// user dir only (the only folder the daemon watches, plan D4).</summary>
    public static IReadOnlyList<string> CandidatePaths(string key) => [Path.Combine(UserDir, key + ".json")];
}
