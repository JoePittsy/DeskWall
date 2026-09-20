using DeskWall.Core;
using DeskWall.Core.Display;
using DeskWall.Core.Render;
using DeskWall.Core.Resolve;
using DeskWall.Core.Verify;
using Xunit;

namespace DeskWall.Core.Tests.Verify;

/// <summary>The pure half of `deskwall verify`: everything that turns two surfaces into a padding
/// measurement. The live half (minimise, capture, un-minimise) is exercised by the command itself on
/// the reference machine, not here.</summary>
public class VerifierTests
{
    private const int CoverW = 200, CoverH = 300, Arrow = 13, Pad = 5;

    private static readonly Color Flat = new(255, 128, 128, 128);

    private static ResolvedShortcut Slot(Rect cover)
        => new("games#0.play", cover, 1, "steam://rungameid/620", "Play Portal 2", 8);

    /// <summary>A composed frame and a screenshot of it, both flat so only what the test paints differs.</summary>
    private static (Surface Shot, Surface Composed) Pair()
    {
        var composed = Surface.Create(CoverW, CoverH);
        composed.Clear(Flat);
        var shot = Surface.Create(CoverW, CoverH);
        shot.Clear(Flat);
        return (shot, composed);
    }

    /// <summary>The overlay as the shell draws it: an <see cref="Arrow"/> px square whose left edge is
    /// <paramref name="left"/> px from the cover's left and whose bottom edge is <paramref name="bottom"/>
    /// px above the cover's bottom.</summary>
    private static void PaintArrow(Surface shot, Rect cover, int left, int bottom)
        => shot.FillRect(new Rect(cover.X + left, cover.Bottom - bottom - Arrow, Arrow, Arrow), Color.White);

    [Fact]
    public void Arrow_At_The_Wanted_Pad_Is_Ok()
    {
        var cover = new Rect(0, 0, CoverW, CoverH);
        var (shot, composed) = Pair();
        using (shot)
        using (composed)
        {
            PaintArrow(shot, cover, Pad, Pad);
            var c = Verifier.CheckSlot(shot, composed, Slot(cover), (5, 242), (5, 242), Pad, 60);
            Assert.True(c.Ok);
            Assert.Equal(Pad, c.LeftPad);
            Assert.Equal(Pad, c.BottomPad);
            Assert.Equal(new Rect(5, 282, Arrow, Arrow), c.ArrowBox);
            Assert.Null(c.Note);
        }
    }

    [Fact]
    public void Arrow_One_Px_To_The_Right_Is_Not_Ok_And_Says_So()
    {
        var cover = new Rect(0, 0, CoverW, CoverH);
        var (shot, composed) = Pair();
        using (shot)
        using (composed)
        {
            PaintArrow(shot, cover, Pad + 1, Pad);
            var c = Verifier.CheckSlot(shot, composed, Slot(cover), (5, 242), (6, 242), Pad, 60);
            Assert.False(c.Ok);
            Assert.Equal(6, c.LeftPad);
            Assert.Equal(5, c.BottomPad);
            Assert.Equal("PAD OFF BY (1,0)", c.Note);
        }
    }

    [Fact]
    public void No_Difference_At_All_Reads_As_A_Missing_Icon()
    {
        var cover = new Rect(0, 0, CoverW, CoverH);
        var (shot, composed) = Pair();
        using (shot)
        using (composed)
        {
            var c = Verifier.CheckSlot(shot, composed, Slot(cover), (5, 242), null, Pad, 60);
            Assert.False(c.Ok);
            Assert.Equal("NO ICON FOUND", c.Note);
            Assert.Null(c.ArrowBox);
            // -1, not 0: a pad of zero is a real (wrong) measurement and must not be confused with one
            // that was never taken.
            Assert.Equal(-1, c.LeftPad);
            Assert.Equal(-1, c.BottomPad);
        }
    }

    [Fact]
    public void A_JPEG_Noise_Blip_Under_The_Threshold_Does_Not_Widen_The_Arrow_Box()
    {
        var cover = new Rect(0, 0, CoverW, CoverH);
        var (shot, composed) = Pair();
        using (shot)
        using (composed)
        {
            PaintArrow(shot, cover, Pad, Pad);
            shot.FillRect(new Rect(180, 20, 1, 1), new Color(255, 160, 128, 128));   // 32 off, under 60
            var c = Verifier.CheckSlot(shot, composed, Slot(cover), (5, 242), (5, 242), Pad, 60);
            Assert.True(c.Ok);
            Assert.Equal(new Rect(5, 282, Arrow, Arrow), c.ArrowBox);
        }
    }

    /// <summary>The same blip with the threshold dropped below it does widen the box, which is what
    /// makes the previous test a measurement rather than a coincidence.</summary>
    [Fact]
    public void The_Same_Blip_Above_The_Threshold_Is_Counted()
    {
        var cover = new Rect(0, 0, CoverW, CoverH);
        var (shot, composed) = Pair();
        using (shot)
        using (composed)
        {
            PaintArrow(shot, cover, Pad, Pad);
            shot.FillRect(new Rect(180, 20, 1, 1), new Color(255, 160, 128, 128));
            var c = Verifier.CheckSlot(shot, composed, Slot(cover), (5, 242), (5, 242), Pad, 10);
            // The box now stretches from the blip to the arrow. Its left and bottom edges still happen
            // to be the arrow's, so the pads read 5/5: the box is the evidence, not the verdict.
            Assert.Equal(new Rect(5, 20, 176, 275), c.ArrowBox);
        }
    }

    /// <summary>Nothing inside the 2 px inset is compared: an arrow flush against the cover's edge is
    /// invisible to the diff, and reading it as pad 0 would be a lie. The cover is offset so the test
    /// also proves the pads are measured from the cover, not from the surface origin.</summary>
    [Fact]
    public void Pixels_Inside_The_Two_Px_Inset_Are_Not_Compared()
    {
        var cover = new Rect(10, 10, 180, 280);
        var (shot, composed) = Pair();
        using (shot)
        using (composed)
        {
            shot.FillRect(new Rect(cover.X, cover.Bottom - 2, 2, 2), Color.White);
            var flush = Verifier.CheckSlot(shot, composed, Slot(cover), (10, 272), null, Pad, 60);
            Assert.Equal("NO ICON FOUND", flush.Note);

            PaintArrow(shot, cover, Pad, Pad);
            var good = Verifier.CheckSlot(shot, composed, Slot(cover), (10, 272), null, Pad, 60);
            Assert.True(good.Ok);
            Assert.Equal(Pad, good.LeftPad);
            Assert.Equal(Pad, good.BottomPad);
        }
    }

    [Fact]
    public void Report_Text_Names_Every_Slot_And_Ends_With_The_Verdict()
    {
        var ok = new SlotCheck(8, "games#0.play", new Rect(3220, 142, 172, 258), (3225, 347), (3225, 347),
            new Rect(3225, 382, 13, 13), 5, 5, true, null);
        var bad = new SlotCheck(9, "games#1.play", new Rect(3220, 414, 172, 258), (3225, 619), null,
            null, -1, -1, false, "NO ICON FOUND");
        var sig = new DisplaySignature(@"\\?\DISPLAY#DELA1E7", 3440, 1440, 100);
        var report = new VerifyReport(sig, [ok, bad], "C:\\rt\\verify-desktop.png", "C:\\rt\\clock-now.png", false);

        var text = report.ToText();
        Assert.Contains(sig.Key, text, StringComparison.Ordinal);
        Assert.Contains("left pad 5, bottom pad 5  OK", text, StringComparison.Ordinal);
        Assert.Contains("left pad -, bottom pad -  NO ICON FOUND", text, StringComparison.Ordinal);
        Assert.EndsWith("RESULT: FAIL", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Report_Json_Round_Trips_The_Numbers_A_Reader_Needs()
    {
        var check = new SlotCheck(8, "games#0.play", new Rect(3220, 142, 172, 258), (3225, 347), (3225, 347),
            new Rect(3225, 382, 13, 13), 5, 5, true, null);
        var sig = new DisplaySignature(@"\\?\DISPLAY#DELA1E7", 3440, 1440, 100);
        var json = new VerifyReport(sig, [check], "C:\\rt\\verify-desktop.png", "", true).ToJson();

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.Equal(sig.Key, root.GetProperty("signature").GetString());
        var slot = root.GetProperty("slots")[0];
        Assert.Equal(8, slot.GetProperty("slot").GetInt32());
        Assert.Equal(new[] { 3220, 142, 172, 258 }, slot.GetProperty("cover").EnumerateArray().Select(e => e.GetInt32()).ToArray());
        Assert.Equal(new[] { 3225, 347 }, slot.GetProperty("wanted").EnumerateArray().Select(e => e.GetInt32()).ToArray());
        Assert.Equal(5, slot.GetProperty("leftPad").GetInt32());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, slot.GetProperty("note").ValueKind);
    }

    [Fact]
    public void Json_Null_Got_And_ArrowBox_Are_Null_Not_Absent()
    {
        var check = new SlotCheck(8, "games#0.play", new Rect(0, 0, 10, 10), (0, 0), null, null, -1, -1, false, "NO ICON FOUND");
        var sig = new DisplaySignature("D", 100, 100, 100);
        using var doc = System.Text.Json.JsonDocument.Parse(new VerifyReport(sig, [check], "s.png", "", false).ToJson());
        var slot = doc.RootElement.GetProperty("slots")[0];
        Assert.Equal(System.Text.Json.JsonValueKind.Null, slot.GetProperty("got").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, slot.GetProperty("arrowBox").ValueKind);
        Assert.Equal("NO ICON FOUND", slot.GetProperty("note").GetString());
    }
}
