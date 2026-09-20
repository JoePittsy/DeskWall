using DeskWall.Core;
using DeskWall.Core.Render;
using DeskWall.Core.Resolve;
using DeskWall.Core.Shortcuts;
using Xunit;

namespace DeskWall.Core.Tests.Shortcuts;

/// <summary>One test here touches the live desktop. It uses slots 60..62, which no layout uses, and
/// deletes them in a finally; it never goes near slots 0..3, which the v0 PowerShell tool still owns.</summary>
public class ShortcutManagerTests
{
    private static ResolvedShortcut S(int slot, int y, string target = "steam://rungameid/620")
        => new($"s{slot}", new Rect(3220, y, 172, 258), 1, target, $"Play {slot}", slot);

    [Fact]
    public void Fingerprint_Changes_With_Rect_Target_Tooltip_Slot_Or_Scale()
    {
        var m = new ShortcutManager(Calibration.Seed());
        var a = m.Fingerprint([S(0, 142)], 100);
        Assert.Equal(a, m.Fingerprint([S(0, 142)], 100));
        Assert.NotEqual(a, m.Fingerprint([S(0, 143)], 100));
        Assert.NotEqual(a, m.Fingerprint([S(0, 142, "steam://rungameid/730")], 100));
        Assert.NotEqual(a, m.Fingerprint([S(1, 142)], 100));
        Assert.NotEqual(a, m.Fingerprint([S(0, 142)], 125));
        Assert.NotEqual(a, m.Fingerprint([S(0, 142) with { Tooltip = "something else" }], 100));
        // The arrow is part of it: a recalibration must re-place every icon.
        var recalibrated = Calibration.Seed();
        recalibrated.Set(48, 100, new ArrowRect(0, 41, 13));
        Assert.NotEqual(a, new ShortcutManager(recalibrated).Fingerprint([S(0, 142)], 100));
        // So is the pad.
        Assert.NotEqual(a, new ShortcutManager(Calibration.Seed(), pad: 6).Fingerprint([S(0, 142)], 100));
    }

    [Fact]
    public void RemoveAll_Deletes_Slot_Files_And_Nothing_Else()
    {
        var dir = Path.Combine(Path.GetTempPath(), "deskwall-tests", "rm-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, ShortcutPlan.SlotFileName(0)), "");
            File.WriteAllText(Path.Combine(dir, ShortcutPlan.SlotFileName(2)), "");
            File.WriteAllText(Path.Combine(dir, "Steam.lnk"), "");
            File.WriteAllText(Path.Combine(dir, "notes.txt"), "");

            var removed = new ShortcutManager(Calibration.Seed(), desktopDir: () => dir).RemoveAll();

            Assert.Equal(2, removed);
            Assert.False(File.Exists(Path.Combine(dir, ShortcutPlan.SlotFileName(0))));
            Assert.False(File.Exists(Path.Combine(dir, ShortcutPlan.SlotFileName(2))));
            Assert.True(File.Exists(Path.Combine(dir, "Steam.lnk")));
            Assert.True(File.Exists(Path.Combine(dir, "notes.txt")));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Reconcile_On_The_Real_Desktop_Places_Two_Slots_And_Removes_A_Stale_One()
    {
        var desktop = ShortcutFiles.DesktopDir();
        var slots = new[] { 60, 61, 62 };
        string Slot(int s) => Path.Combine(desktop, ShortcutPlan.SlotFileName(s));
        foreach (var s in slots) if (File.Exists(Slot(s))) File.Delete(Slot(s));   // a crashed earlier run
        try
        {
            var m = new ShortcutManager(Calibration.Load());
            var first = m.Reconcile([S(60, 142), S(61, 414), S(62, 686)], 100);
            Assert.Equal(3, first.Written);
            Assert.Equal(3, first.Positioned);
            Assert.Empty(first.Warnings);

            var second = m.Reconcile([S(60, 142), S(61, 414)], 100);
            Assert.Equal(1, second.Removed);
            Assert.False(File.Exists(Slot(62)));
            // Nothing about the two survivors changed, so nothing is rewritten: the .lnk on disk must
            // read back as the spec we would write. Churn here would mean a rewrite every reconcile.
            Assert.Equal(0, second.Written);
            Assert.Equal(2, second.Positioned);

            var arrow = Calibration.Load().Get(DesktopView.IconSize(), 100) ?? Calibration.Seed().Get(48, 100)!;
            var want = ShortcutPlan.IconPosition(new Rect(3220, 414, 172, 258), arrow);
            var got = DesktopView.GetPosition(Slot(61))!.Value;
            Assert.Equal(want, got);
        }
        finally
        {
            foreach (var s in slots) if (File.Exists(Slot(s))) File.Delete(Slot(s));
        }
    }
}
