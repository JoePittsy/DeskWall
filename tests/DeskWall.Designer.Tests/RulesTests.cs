using DeskWall.Core.Values;
using DeskWall.Designer.Model;
using Xunit;

namespace DeskWall.Designer.Tests;

/// <summary>The bind menu's rules round-trip through the format Core evaluates, and a format they
/// cannot show is not mistaken for rules (so Done keeps it rather than dropping it).</summary>
public class RulesTests
{
    [Fact]
    public void Step_And_Blend_Round_Trip_And_Evaluate()
    {
        var step = Rules.Write(RuleMode.Step, [new Rule(0.7, "#107C10"), new Rule(0.3, "#0078D4")], "#0078D4")!;
        Assert.Equal("?<0.3=#0078D4,<0.7=#107C10,*=#0078D4", step);
        Assert.Equal("#107C10", new NumberValue(0.5).ToText(step));
        var parsed = Rules.Parse(step)!.Value;
        Assert.Equal(RuleMode.Step, parsed.Mode);
        Assert.Equal([0.3, 0.7], parsed.Rows.Select(r => r.At));
        Assert.Equal("#0078D4", parsed.Otherwise);

        var blend = Rules.Write(RuleMode.Blend, [new Rule(0, "12"), new Rule(1, "48")], "")!;
        Assert.Equal("~0=12,1=48", blend);
        Assert.Equal("30", new NumberValue(0.5).ToText(blend));
        Assert.Equal(RuleMode.Blend, Rules.Parse(blend)!.Value.Mode);
    }

    [Theory]
    [InlineData("?true=#D13438,false=#EBFFFFFF")]
    [InlineData("?>=0.9=hot")]
    [InlineData("N0")]
    [InlineData(null)]
    public void Anything_Else_Is_Not_Rules(string? format) => Assert.Null(Rules.Parse(format));
}
