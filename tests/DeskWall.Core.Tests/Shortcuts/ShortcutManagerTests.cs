using DeskWall.Core;
using DeskWall.Core.Render;
using DeskWall.Core.Resolve;
using DeskWall.Core.Shortcuts;
using Xunit;

namespace DeskWall.Core.Tests.Shortcuts;

/// <summary>The tests tagged Category=Desktop touch the live desktop. They use slots 60..62, which no
/// layout uses, delete them in a finally and restore the desktop folder flags Reconcile turns off; they
/// never go near slots 0..3, which the v0 PowerShell tool still owns. Each returns without asserting
/// when there is no interactive desktop (Session 0, a locked workstation, CI) rather than failing.</summary>
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

    /// <summary>The Critical finding: uninstall must not take the v0 POC's slots with it. Owned is
    /// {60}; slot 61 is a slot file on the same desktop that somebody else wrote.</summary>
    [Fact]
    public void RemoveOwned_Deletes_Only_The_Slots_We_Own()
    {
        var dir = Path.Combine(Path.GetTempPath(), "deskwall-tests", "rm-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var ownedFile = Paths.InRuntime("shortcuts-owned.json");
        try
        {
            File.WriteAllText(ownedFile, "{\"Slots\":{\"60\":\"0123456789ABCDEF\"}}");
            File.WriteAllText(Path.Combine(dir, ShortcutPlan.SlotFileName(60)), "");
            File.WriteAllText(Path.Combine(dir, ShortcutPlan.SlotFileName(61)), "");
            File.WriteAllText(Path.Combine(dir, "Steam.lnk"), "");
            File.WriteAllText(Path.Combine(dir, "notes.txt"), "");

            var removed = new ShortcutManager(Calibration.Seed(), desktopDir: () => dir).RemoveOwned();

            Assert.Equal(1, removed);
            Assert.False(File.Exists(Path.Combine(dir, ShortcutPlan.SlotFileName(60))));
            Assert.True(File.Exists(Path.Combine(dir, ShortcutPlan.SlotFileName(61))));   // not ours
            Assert.True(File.Exists(Path.Combine(dir, "Steam.lnk")));
            Assert.True(File.Exists(Path.Combine(dir, "notes.txt")));
            Assert.False(File.Exists(ownedFile));   // and we no longer claim anything
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
            if (File.Exists(ownedFile)) File.Delete(ownedFile);
        }
    }

    /// <summary>A slot whose write throws must stay in shortcuts-owned.json, or RemoveStale deletes
    /// the .lnk that is still on the desktop and still wanted. The write is made to fail by holding
    /// the target file open with FileShare.None - an Explorer restart looks like this from here.</summary>
    [Fact]
    [Trait("Category", "Desktop")]
    public void A_Failed_Write_Keeps_The_Slot_Owned_And_Reports_SlotFailed()
    {
        if (!DesktopView.IsAvailable()) return;   // no interactive desktop: nothing to reconcile against
        var dir = Path.Combine(Path.GetTempPath(), "deskwall-tests", "fail-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var ownedFile = Paths.InRuntime("shortcuts-owned.json");
        var slotPath = Path.Combine(dir, ShortcutPlan.SlotFileName(60));
        try
        {
            File.WriteAllText(ownedFile, "{\"Slots\":{\"60\":\"0123456789ABCDEF\"}}");
            using (var hold = new FileStream(slotPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                var m = new ShortcutManager(Calibration.Seed(), desktopDir: () => dir);
                var outcome = m.Reconcile([S(60, 142)], 100);

                Assert.Equal(0, outcome.Written);
                Assert.Equal(0, outcome.Removed);          // the slot we failed to write is NOT deleted
                Assert.True(outcome.SlotFailed);           // so the tick will not store the fingerprint
                Assert.True(File.Exists(slotPath));
            }
            Assert.Contains("\"60\"", File.ReadAllText(ownedFile));
        }
        finally
        {
            DesktopFlags.Restore();
            Directory.Delete(dir, recursive: true);
            if (File.Exists(ownedFile)) File.Delete(ownedFile);
        }
    }

    [Fact]
    [Trait("Category", "Desktop")]
    public void Reconcile_On_The_Real_Desktop_Places_Two_Slots_And_Removes_A_Stale_One()
    {
        if (!DesktopView.IsAvailable()) return;   // Session 0 or a locked workstation: skip, do not fail
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
            // Reconcile turns auto-arrange and snap-to-grid off. Put the user's flags back: the save
            // file lives in the test DESKWALL_HOME, which is thrown away, so nothing else would.
            DesktopFlags.Restore();
        }
    }
}
