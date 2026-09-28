using System.IO;
using DeskWall.Designer.Model.Widgets;

namespace DeskWall.Designer.Tests.Widgets;

/// <summary>The repo itself, for the tests that have to read what is actually committed rather
/// than a fixture built in code: the shipped widget templates and the starter layouts generated
/// from them.</summary>
internal static class TestRepo
{
    public static string Root
    {
        get
        {
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "DeskWall.slnx"))) return d.FullName;
            throw new InvalidOperationException("repo root not found");
        }
    }

    public static string WidgetsDir => Path.Combine(Root, "widgets");

    /// <summary>Loaded fresh each call: a template is never mutated, but a test that managed to
    /// would otherwise poison every test after it.</summary>
    public static IReadOnlyList<WidgetTemplate> Widgets() => WidgetCatalog.Load(WidgetsDir);
}
