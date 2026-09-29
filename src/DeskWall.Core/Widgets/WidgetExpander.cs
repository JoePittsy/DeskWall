using DeskWall.Core.Layout;

namespace DeskWall.Core.Widgets;

public enum ExpandProblemKind { MissingWidget, BrokenWidget, OrphanOverride, OrphanKnob }

/// <summary>One thing the expander skipped. <paramref name="Detail"/> is the widget key for
/// Missing/Broken (plus the load error for Broken), else the override key or knob id.</summary>
public sealed record ExpandProblem(ExpandProblemKind Kind, string CopyId, string Detail);

/// <param name="Layout">The expanded layout: no copies, only ordinary sources and components.</param>
/// <param name="WidgetKeys">Every widget key the copies referenced, found or not, once each.</param>
public sealed record Expansion(LayoutFile Layout, IReadOnlyList<ExpandProblem> Problems, IReadOnlyList<string> WidgetKeys);

/// <summary>Turns a v2 layout's <see cref="LayoutFile.Copies"/> into ordinary components before
/// resolve (<c>docs/layout-format.md</c> "Copies"). Runs at load, never per tick.</summary>
public static class WidgetExpander
{
    /// <param name="find">Widget key to its template; null when no file has that key. May throw
    /// for a file that fails to load, which becomes <see cref="ExpandProblemKind.BrokenWidget"/>.</param>
    public static Expansion Expand(LayoutFile layout, Func<string, WidgetTemplate?> find)
    {
        if (layout.Copies is null or { Count: 0 }) return new Expansion(layout, [], []);
        throw new NotImplementedException("Task 1.3");
    }
}
