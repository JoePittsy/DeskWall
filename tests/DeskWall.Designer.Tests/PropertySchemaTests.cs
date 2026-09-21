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
