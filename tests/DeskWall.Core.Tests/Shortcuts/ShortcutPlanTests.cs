using DeskWall.Core;
using DeskWall.Core.Resolve;
using DeskWall.Core.Shortcuts;
using Xunit;

public class ShortcutPlanTests
{
    [Fact]
    public void IconPosition_Matches_The_Poc_Constants()
    {
        // POC shortcuts.ps1: item.x = cover.X + Pad - 0 ; item.y = cover.Y + cover.H - Pad - 13 - 40
        var cover = new Rect(3220, 142, 172, 258);
        var (x, y) = ShortcutPlan.IconPosition(cover, new ArrowRect(0, 40, 13));
        Assert.Equal(3225, x);
        Assert.Equal(142 + 258 - 5 - 13 - 40, y);
    }

    [Fact]
    public void Slot_Names_Are_NonBreaking_Spaces_And_RoundTrip()
    {
        Assert.Equal(" .lnk", ShortcutPlan.SlotFileName(0));
        Assert.Equal("    .lnk", ShortcutPlan.SlotFileName(3));
        Assert.Equal(3, ShortcutPlan.SlotFromFileName("    .lnk"));
        Assert.Null(ShortcutPlan.SlotFromFileName("Steam.lnk"));
        Assert.Null(ShortcutPlan.SlotFromFileName("  x.lnk"));
    }

    [Fact]
    public void Ordered_Sorts_By_Slot_And_Rejects_Duplicates()
    {
        var a = new ResolvedShortcut("b", new Rect(0, 0, 1, 1), 0, "x", "", 1);
        var b = new ResolvedShortcut("a", new Rect(0, 0, 1, 1), 0, "x", "", 0);
        Assert.Equal(["a", "b"], ShortcutPlan.Ordered([a, b]).Select(s => s.Id));
        Assert.Throws<InvalidOperationException>(() => ShortcutPlan.Ordered([a, a with { Id = "c" }]));
    }
}

public class CalibrationTests
{
    [Fact]
    public void Seed_Has_The_Poc_Value_And_Save_Load_RoundTrips()
    {
        var c = Calibration.Seed();
        Assert.Equal(new ArrowRect(0, 40, 13), c.Get(48, 100));
        Assert.Null(c.Get(32, 100));
        c.Set(32, 125, new ArrowRect(1, 27, 9));
        c.Save();
        var back = Calibration.Load();
        Assert.Equal(new ArrowRect(1, 27, 9), back.Get(32, 125));
        Assert.Equal(new ArrowRect(0, 40, 13), back.Get(48, 100));
    }
}
