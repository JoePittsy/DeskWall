using DeskWall.Core;
using DeskWall.Core.Bindings;
using DeskWall.Core.Layout;
using DeskWall.Designer.Model;
using Xunit;

public class PropertySchemaTests
{
    private static readonly Rect R = new(0, 0, 100, 20);

    public static IEnumerable<object[]> AllTypes()
    {
        yield return [new TextDef { Id = "t", Rect = R, Text = PropertyValue.Literal("hi") }, "Text"];
        yield return [new ImageDef { Id = "i", Rect = R, Source = PropertyValue.Literal("a.png") }, "Source"];
        yield return [new BarDef { Id = "b", Rect = R, Fraction = PropertyValue.Literal(0.5) }, "Fraction"];
        yield return [new ShortcutDef { Id = "s", Rect = R, Target = PropertyValue.Literal("a.exe") }, "Target"];
        yield return [new RepeaterDef { Id = "r", Rect = R, Items = PropertyValue.Literal(""), Template = new List<ComponentDef>() }, "Items"];
        yield return [new DialDef { Id = "d", Rect = R, Fraction = PropertyValue.Literal(0.5) }, "Fraction"];
    }

    [Theory]
    [MemberData(nameof(AllTypes))]
    public void Every_Type_Has_A_NonEmpty_Schema(ComponentDef def, string firstPropToCheck)
    {
        var props = PropertySchema.For(def);
        Assert.NotEmpty(props);
        Assert.Contains(props, p => p.Name == firstPropToCheck);
    }

    [Theory]
    [MemberData(nameof(AllTypes))]
    public void Set_Then_Get_RoundTrips_A_Literal_And_A_Binding(ComponentDef def, string propName)
    {
        var prop = PropertySchema.For(def).Single(p => p.Name == propName);

        prop.Set(def, PropertyValue.Literal("literal-value"));
        var afterLiteral = prop.Get(def);
        Assert.NotNull(afterLiteral);
        Assert.False(afterLiteral!.IsBound);
        Assert.Equal("literal-value", afterLiteral.LiteralText);

        var binding = Binding.Parse("disks.drives[C].freeGB");
        prop.Set(def, PropertyValue.Bound(binding));
        var afterBinding = prop.Get(def);
        Assert.NotNull(afterBinding);
        Assert.True(afterBinding!.IsBound);
        Assert.Equal(binding.ToString(), afterBinding.Binding!.ToString());
    }

    [Fact]
    public void Scalar_Adapters_RoundTrip_Literal_And_Ignore_A_Binding()
    {
        var def = new ShortcutDef { Id = "s", Rect = R, Target = PropertyValue.Literal("a.exe"), Slot = 2 };
        var slot = PropertySchema.For(def).Single(p => p.Name == "Slot");

        Assert.Equal("2", slot.Get(def)!.LiteralText);
        slot.Set(def, PropertyValue.Literal(3.0));
        Assert.Equal(3, def.Slot);

        // Slot has nowhere to store a binding; Set on a bound value must not throw and must leave
        // the field unchanged.
        slot.Set(def, PropertyValue.Bound(Binding.Parse("time.now")));
        Assert.Equal(3, def.Slot);
    }

    // ---- "a number, or let the renderer decide" --------------------------------------------------

    /// <summary>The two properties Core lets you leave to it: a text shadow's radius follows the
    /// font size, a repeater cell's height follows its first image. Both are the string "auto" in
    /// the JSON, so both need the editor that does not show the owner that word.</summary>
    [Theory]
    [InlineData("EffectRadius")]
    [InlineData("CellHeight")]
    public void The_Auto_Capable_Sizes_Get_The_Auto_Number_Editor(string name)
    {
        ComponentDef def = name == "EffectRadius"
            ? new TextDef { Id = "t", Rect = R, Text = PropertyValue.Literal("x") }
            : new RepeaterDef { Id = "r", Rect = R, Items = PropertyValue.Literal(""), Template = [] };
        Assert.Equal(PropertySchema.Editor.AutoNumber, PropertySchema.For(def).Single(p => p.Name == name).Editor);
    }

    [Fact]
    public void An_Auto_Value_Shows_As_An_Empty_Box_And_A_Number_Shows_Itself()
    {
        Assert.Equal("", PropertySchema.AutoNumberText(PropertyValue.Literal("auto")));
        Assert.Equal("", PropertySchema.AutoNumberText(PropertyValue.Literal("AUTO")));
        Assert.Equal("6", PropertySchema.AutoNumberText(PropertyValue.Literal(6)));
        // A bound value is shown by the bound display, not by this box.
        Assert.Equal("", PropertySchema.AutoNumberText(PropertyValue.Bound(Binding.Parse("cfg.radius"))));
    }

    [Theory]
    [InlineData("", "6", "auto")]            // cleared: hand it back to the renderer
    [InlineData("   ", "6", "auto")]
    [InlineData("auto", "6", "auto")]        // typed out in full, same answer
    [InlineData("12", "auto", "12")]
    [InlineData("0.5", "auto", "0.5")]
    [InlineData("12px", "6", "6")]           // a typo keeps what was there
    [InlineData("nonsense", "auto", "auto")]
    public void What_Is_Typed_Into_An_Auto_Number_Box_Becomes(string typed, string current, string expected)
        => Assert.Equal(expected, PropertySchema.AutoNumberLiteral(typed, current));

    [Fact]
    public void Geometry_Sets_Rect_Fields()
    {
        var def = new TextDef { Id = "t", Rect = new Rect(1, 2, 3, 4), Text = PropertyValue.Literal("x"), Z = 5 };

        foreach (var (name, get, set) in PropertySchema.Geometry)
        {
            set(def, get(def) + 10);
        }

        Assert.Equal(new Rect(11, 12, 13, 14), def.Rect);
        Assert.Equal(15, def.Z);
    }
}
