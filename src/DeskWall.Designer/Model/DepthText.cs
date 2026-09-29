using System.IO;
using DeskWall.Core.Layout;
using DeskWall.Core.Widgets;

namespace DeskWall.Designer.Model;

/// <summary>The sentences that say what editing a widget reaches: the line under Edit widget and the
/// widget-depth banner. One place, so they cannot drift from each other or from the Reset dialog's
/// "every copy of it in every layout on this machine" wording.</summary>
public static class DepthText
{
    /// <summary>Copies of <paramref name="key"/> in <paramref name="layout"/>.</summary>
    public static int CopiesOf(LayoutFile layout, string key)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return layout.Copies?.Count(c => string.Equals(c.Widget, key, StringComparison.OrdinalIgnoreCase)) ?? 0;
    }

    /// <summary>Under Edit widget: "Every copy of it in every layout on this machine follows (4 are on this layout)."</summary>
    public static string Follow(int here)
        => $"Edit widget: every copy of it in every layout on this machine follows ({(here == 1 ? "1 is" : $"{here} are")} on this layout).";

    /// <summary>A shipped key with no fork yet: the first Apply of an edit writes one (plan D2).</summary>
    public static bool ForksShipped(string key)
        => File.Exists(Path.Combine(WidgetCatalog.ShippedDir, key + ".json")) && !File.Exists(Path.Combine(WidgetCatalog.UserDir, key + ".json"));

    /// <summary>The widget-depth banner, or null at any other depth: "Editing the Hardware dial
    /// widget: 4 copies on this layout follow · Apply forks the shipped widget".</summary>
    public static string? Banner(DesignerModel model) => Banner(model, ForksShipped);

    /// <param name="forksShipped">Test seam for <see cref="ForksShipped"/>.</param>
    public static string? Banner(DesignerModel model, Func<string, bool> forksShipped)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(forksShipped);
        if (model.Depth is not { Kind: DepthKind.Widget, WidgetKey: { } key }) return null;
        var name = Copies.TryFind(model.Finder(), key)?.Name ?? key;
        var n = CopiesOf(model.Layout, key);
        var follow = n switch
        {
            0 => "it has no copy on this layout",
            1 => "1 copy on this layout follows",
            _ => $"{n} copies on this layout follow",
        };
        var text = $"Editing the {name} widget: {follow}";
        return forksShipped(key) ? text + " · Apply forks the shipped widget" : text;
    }
}
