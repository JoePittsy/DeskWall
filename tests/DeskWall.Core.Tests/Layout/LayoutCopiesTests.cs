using DeskWall.Core.Layout;
using DeskWall.Core.Widgets;
using Xunit;

namespace DeskWall.Core.Tests.Layout;

public class LayoutCopiesTests
{
    private static string RepoFile(params string[] parts)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "DeskWall.slnx"))) return Path.Combine([d.FullName, .. parts]);
        throw new InvalidOperationException("repo root not found");
    }

    private const string V2 = """
    {
      "version": 2, "baseImage": "wall.jpg",
      "sources": [ { "name": "time", "type": "time" } ],
      "components": [ { "type": "text", "id": "note", "rect": [0, 0, 10, 10], "text": "hi" } ],
      "copies": [
        { "id": "dial-2", "widget": "dial", "x": 3312, "y": 400, "z": 1,
          "knobs": { "metric": "GPU||hardware.gpu||hardware.gpuPct | \"{0}%\"||gpu" },
          "overrides": {
            "components.value.color": "#FFFFC000",
            "components.value.text": { "bind": "hardware.cpuPct | \"{0}%\"" },
            "components.label.hidden": true,
            "components.drives.letter.size": 15
          } }
      ]
    }
    """;

    [Fact]
    public void V2_Copies_Round_Trip()
    {
        var l = LayoutFile.Parse(V2);
        var c = Assert.Single(l.Copies!);
        Assert.Equal(("dial-2", "dial", 3312, 400, 1), (c.Id, c.Widget, c.X, c.Y, c.Z));
        Assert.Equal("GPU||hardware.gpu||hardware.gpuPct | \"{0}%\"||gpu", c.Knobs["metric"]);
        Assert.Equal("#FFFFC000", c.Overrides["components.value.color"].LiteralText);
        Assert.True(c.Overrides["components.value.text"].IsBound);
        Assert.Equal("true", c.Overrides["components.label.hidden"].LiteralText);
        Assert.Equal("15", c.Overrides["components.drives.letter.size"].LiteralText);

        var json = l.ToJson();
        Assert.Equal(json, LayoutFile.Parse(json).ToJson());
        Assert.DoesNotContain("\"widgets\"", json);
        Assert.DoesNotContain("\"widget\": null", json);
    }

    [Fact]
    public void V1_Writes_No_Copies_And_Expands_To_Itself()
    {
        var l = LayoutFile.Load(RepoFile("layouts", "column-system.json"));
        Assert.Null(l.Copies);
        Assert.DoesNotContain("\"copies\"", l.ToJson());

        var e = WidgetExpander.Expand(l, _ => throw new InvalidOperationException("v1 must not look up widgets"));
        Assert.Same(l, e.Layout);
        Assert.Empty(e.Problems);
        Assert.Empty(e.WidgetKeys);
    }
}
