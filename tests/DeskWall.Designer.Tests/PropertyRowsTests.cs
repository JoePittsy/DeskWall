using System.Globalization;
using DeskWall.Core;
using DeskWall.Core.Bindings;
using DeskWall.Core.Layout;
using DeskWall.Designer.Model;
using Xunit;

public class PropertyRowsTests
{
    private static readonly Rect R = new(0, 0, 100, 20);

    public static TheoryData<ComponentDef> AllTypes() => new(Defs());

    private static ComponentDef[] Defs() =>
    [
        new TextDef { Id = "t", Rect = R, Text = PropertyValue.Literal("hi") },
        new ImageDef { Id = "i", Rect = R, Source = PropertyValue.Literal("a.png") },
        new BarDef { Id = "b", Rect = R, Fraction = PropertyValue.Literal(0.5) },
        new DialDef { Id = "d", Rect = R, Fraction = PropertyValue.Literal(0.5) },
        new LineDef { Id = "l", Rect = R, Values = PropertyValue.Literal("") },
        new ShortcutDef { Id = "s", Rect = R, Target = PropertyValue.Literal("a.exe") },
        new RepeaterDef { Id = "r", Rect = R, Items = PropertyValue.Literal(""), Template = new List<ComponentDef>() },
    ];

    [Fact]
    public void AllTypes_Covers_Every_ComponentDef_Type()
    {
        var covered = Defs().Select(d => d.GetType()).ToHashSet();
        var all = typeof(ComponentDef).Assembly.GetTypes().Where(t => t.IsSubclassOf(typeof(ComponentDef)) && !t.IsAbstract);
        Assert.All(all, t => Assert.Contains(t, covered));
    }

    [Theory]
    [MemberData(nameof(AllTypes))]
    public void Every_Schema_Property_Has_Exactly_One_Row(ComponentDef def)
    {
        var rows = PropertyRows.For(def);
        var schema = PropertySchema.For(def);
        Assert.Equal(schema.Count, rows.Count);
        Assert.All(schema, p => Assert.Single(rows, r => ReferenceEquals(r.Prop, p)));
    }

    [Theory]
    [MemberData(nameof(AllTypes))]
    public void Rows_Are_In_Group_Order_And_Keep_Schema_Order_Within_A_Group(ComponentDef def)
    {
        var rows = PropertyRows.For(def);
        var schema = PropertySchema.For(def).ToList();
        for (var i = 1; i < rows.Count; i++)
        {
            Assert.True(rows[i - 1].Group <= rows[i].Group);
            if (rows[i - 1].Group == rows[i].Group)
                Assert.True(schema.IndexOf(rows[i - 1].Prop) < schema.IndexOf(rows[i].Prop));
        }
    }

    [Theory]
    [MemberData(nameof(AllTypes))]
    public void Labels_Are_Sentence_Case_And_Not_Schema_Names(ComponentDef def)
    {
        foreach (var row in PropertyRows.For(def))
        {
            Assert.False(string.IsNullOrWhiteSpace(row.Label));
            Assert.True(char.IsUpper(row.Label[0]), row.Label);
            Assert.Equal(row.Label[1..], row.Label[1..].ToLowerInvariant());
            Assert.DoesNotContain("Color", row.Label); // UK English
        }
    }

    [Fact]
    public void Named_Labels_And_Groups()
    {
        var dial = PropertyRows.For(new DialDef { Id = "d", Rect = R, Fraction = PropertyValue.Literal(0.5) });
        Assert.Equal("Warn at", dial.Single(r => r.Name == "Threshold").Label);
        Assert.Equal("Stroke width", dial.Single(r => r.Name == "Thickness").Label);
        Assert.Equal(PropertyRows.Group.Arc, dial.Single(r => r.Name == "Sweep").Group);
        Assert.Equal("Width", PropertyRows.Label("W"));
    }

    [Fact]
    public void Exactly_Threshold_Opacity_And_Fraction_Are_Percentages()
    {
        var percent = Defs().SelectMany(PropertyRows.For).Where(r => r.Percent).Select(r => r.Name).ToHashSet();
        Assert.Equal(new HashSet<string> { "Threshold", "Opacity", "Fraction" }, percent);
    }

    [Fact]
    public void Percent_RoundTrips_Every_Value_To_A_Tenth_Of_A_Percent()
    {
        for (var permille = -1000; permille <= 3000; permille++)
        {
            var fraction = permille / 1000.0;
            var literal = PropertyValue.Literal(fraction);
            var shown = PropertyRows.PercentText(literal);
            Assert.Equal((permille / 10m).ToString("0.#", CultureInfo.InvariantCulture), shown);
            var back = PropertyRows.FromPercentText(shown);
            Assert.Equal(literal.LiteralText, back!.LiteralText);
        }
    }

    [Theory]
    [InlineData("90", "0.9")]
    [InlineData("90%", "0.9")]
    [InlineData(" 12.5 % ", "0.125")]
    [InlineData("100", "1")]
    [InlineData("33.33", "0.333")]
    public void FromPercentText_Accepts_What_People_Type(string typed, string stored)
        => Assert.Equal(stored, PropertyRows.FromPercentText(typed)!.LiteralText);

    [Fact]
    public void Non_Numbers_And_Bindings_Are_Left_Alone()
    {
        Assert.Null(PropertyRows.FromPercentText("ninety"));
        Assert.Null(PropertyRows.FromPercentText(""));
        Assert.Null(PropertyRows.PercentText(PropertyValue.Literal("auto")));
        Assert.Null(PropertyRows.PercentText(PropertyValue.Bound(Binding.Parse("disks.drives[C].usedFraction"))));
    }
    /// <summary>Critique 2, P3: "Warn at" on a copy reads 90 %, as the property row does.</summary>
    [Fact]
    public void A_Number_Knob_That_Sets_A_Percentage_Property_Is_A_Percentage()
    {
        var warn = new DeskWall.Core.Widgets.Knob("warnAt", "Warn at", DeskWall.Core.Widgets.KnobType.Number, "0.9", ["components.dial.threshold"], null, 0, 1);
        var size = warn with { Sets = ["components.label.size"] };
        var spliced = warn with { Sets = ["components.dial.threshold=bind:x"] };
        var choice = warn with { Type = DeskWall.Core.Widgets.KnobType.Choice };
        Assert.True(PropertyRows.IsPercentKnob(warn));
        Assert.False(PropertyRows.IsPercentKnob(size));
        Assert.False(PropertyRows.IsPercentKnob(spliced));
        Assert.False(PropertyRows.IsPercentKnob(choice));
    }
}
