using DeskWall.Core;
using DeskWall.Core.Layout;
using DeskWall.Designer.Model.Widgets;
using Xunit;
using CoreWidgets = DeskWall.Core.Widgets;

namespace DeskWall.Designer.Tests.Widgets;

/// <summary>The Core port of the knob <c>sets</c> grammar against the designer code it was ported
/// from: for every shipped widget and every choice of every choice knob (plus one non-default value
/// for each other knob), stamping with <see cref="WidgetInstance"/> and expanding the matching copy
/// with <see cref="CoreWidgets.WidgetExpander"/> give the same sources and components. Deleted in
/// Task 2.1, when the designer's own widget code goes and this would compare Core with Core.</summary>
public class ExpanderParityTests
{
    private const string Defaults = "";

    public static TheoryData<string, string, string> Cases()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var t in TestRepo.Widgets())
        {
            data.Add(t.Key, Defaults, Defaults);
            foreach (var k in t.Knobs)
            {
                if (k.Type == KnobType.Choice)
                    foreach (var choice in k.Choices ?? []) data.Add(t.Key, k.Id, choice);
                else
                    data.Add(t.Key, k.Id, Sample(k.Type));
            }
        }
        return data;
    }

    private static string Sample(KnobType type) => type switch
    {
        KnobType.Number => "0.5",
        KnobType.Color => "#FF112233",
        KnobType.Drive => "D",
        KnobType.Town => "York||53.9591||-1.0815",
        _ => "sample text",
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Designer_Stamping_Equals_Core_Expansion(string key, string knobId, string value)
    {
        var designer = TestRepo.Widgets().Single(t => t.Key == key);
        var core = CoreWidgets.WidgetTemplate.Load(designer.Path!);

        var stamped = new LayoutFile { BaseImage = "" };
        var id = WidgetInstance.Add(stamped, designer, new Rect(3220, 134, 0, 0));
        var copy = new WidgetCopy { Id = id, Widget = key, X = 3220, Y = 134 };
        if (knobId != Defaults)
        {
            WidgetInstance.SetKnob(stamped, designer, id, knobId, value);
            copy.Knobs[knobId] = value;
        }

        var e = CoreWidgets.WidgetExpander.Expand(new LayoutFile { Version = 2, BaseImage = "", Copies = [copy] }, k => k == key ? core : null);

        Assert.Empty(e.Problems);
        Assert.Equal(Json(stamped), Json(e.Layout));
    }

    private static string Json(LayoutFile l) => new LayoutFile { BaseImage = "", Sources = l.Sources, Components = l.Components }.ToJson();
}
