namespace DeskWall.Core.Widgets;

/// <summary>Where widget files live, and a lookup by key over one or more folders. Seam for
/// Phase 1 (plan Task 1.2 fills in the bodies; Task 1.5 builds against these signatures).</summary>
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
    public static Func<string, WidgetTemplate?> Finder(params string[] dirs) =>
        throw new NotImplementedException("Task 1.2");

    /// <summary>Every path a widget with <paramref name="key"/> could be loaded from, in the
    /// user dir only (the only folder the daemon watches, plan D4).</summary>
    public static IReadOnlyList<string> CandidatePaths(string key) => [Path.Combine(UserDir, key + ".json")];
}
