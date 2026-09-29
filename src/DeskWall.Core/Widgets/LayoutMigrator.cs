using DeskWall.Core.Layout;

namespace DeskWall.Core.Widgets;

/// <param name="V2">The migrated layout (copies plus any loose components).</param>
/// <param name="Equivalent">Expanding <paramref name="V2"/> reproduces the v1 components, sources and paint order.</param>
/// <param name="Notes">Human-readable lines for <c>deskwall migrate --check</c>.</param>
public sealed record MigrationResult(LayoutFile V2, bool Equivalent, IReadOnlyList<string> Notes);

/// <summary>One-off v1 (stamped widget instances) to v2 (linked copies) conversion. Deleted in
/// Phase 6 with <see cref="LayoutFile.Widgets"/>.</summary>
public static class LayoutMigrator
{
    public static MigrationResult Migrate(LayoutFile v1, Func<string, WidgetTemplate?> find)
        => throw new NotImplementedException("Task 1.4");
}
