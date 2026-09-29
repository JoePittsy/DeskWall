using DeskWall.Core.Values;
using DeskWall.Designer.Model;
using Xunit;

/// <summary>Task 4.1: every preset is a format Core's <see cref="Value.ToText"/> really applies,
/// never one it rejects and falls back to the raw value for.</summary>
public class FormatPresetsTests
{
    private static readonly Dictionary<ValueKind, Value> Examples = new()
    {
        [ValueKind.Fraction] = new NumberValue(0.274),
        [ValueKind.Number] = new NumberValue(64.37),
        [ValueKind.Timestamp] = new TimeValue(new DateTimeOffset(2026, 9, 29, 14, 5, 0, TimeSpan.FromHours(1))),
        [ValueKind.Bool] = new BoolValue(true),
        [ValueKind.Text] = new TextValue("JOES-PC"),
        [ValueKind.List] = new ListValue([], null),
    };

    public static TheoryData<ValueKind> Kinds() => [.. Enum.GetValues<ValueKind>()];

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Every_Preset_Formats_Rather_Than_Falling_Back(ValueKind kind)
    {
        var example = Examples[kind];
        var raw = example.ToText(null);
        foreach (var p in FormatPresets.For(kind))
        {
            var text = example.ToText(p.Format);
            Assert.False(string.IsNullOrEmpty(text), $"{kind} {p.Format}");
            if (p.Format is not null) Assert.NotEqual(raw, text);
            // Where the label is the example as it would draw, it has to be exactly that.
            if (kind is ValueKind.Fraction or ValueKind.Number or ValueKind.Timestamp) Assert.Equal(p.Label, text);
        }
    }

    [Fact]
    public void Every_Kind_That_Draws_As_Text_Has_A_Default_And_A_List_Has_None()
    {
        foreach (var kind in Enum.GetValues<ValueKind>())
            Assert.Equal(kind != ValueKind.List, FormatPresets.For(kind).Count > 0);
        Assert.Equal("{0:0%}", FormatPresets.For(ValueKind.Fraction)[0].Format);
        Assert.Equal("HH:mm", FormatPresets.For(ValueKind.Timestamp)[0].Format);
    }

    [Fact]
    public void A_Bool_Preset_Reads_False_Too()
        => Assert.Equal("No", new BoolValue(false).ToText(FormatPresets.For(ValueKind.Bool)[0].Format));
}
