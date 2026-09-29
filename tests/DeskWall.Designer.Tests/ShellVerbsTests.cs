using DeskWall.Core;
using DeskWall.Core.Display;
using DeskWall.Core.Layout;
using DeskWall.Designer.Model;
using Xunit;

/// <summary>Task 3.5: the verbs behind the main window's keys (Enter, Esc, Tab, Ctrl+A/C/V/D,
/// Ctrl+[ and ], Ctrl+Alt+K), on the model, without the window. Uses "dial", never "clock" (see
/// DesignerModelDepthTests).</summary>
public class ShellVerbsTests
{
    private static DesignerModel TwoDials() => DesignerModelDepthTests.Model("""
        { "id": "dial-1", "widget": "dial", "x": 100, "y": 50, "knobs": {}, "overrides": { "components.label.color": "#FFFF8000" } },
        { "id": "dial-2", "widget": "dial", "x": 300, "y": 50 }
        """);

    private static DesignerModel Loose() => new(LayoutFile.Parse("""
        { "version": 2, "baseImage": "x.jpg", "sources": [],
          "components": [
            { "type": "text", "id": "a", "rect": [10, 10, 40, 20], "text": "a" },
            { "type": "text", "id": "b", "rect": [60, 10, 40, 20], "text": "b" } ],
          "copies": [ { "id": "dial-1", "widget": "dial", "x": 300, "y": 50 } ] }
        """), new DisplaySignature("T", 1000, 800, 100), null);

    private static WidgetCopy Copy(DesignerModel m, string id) => m.Layout.Copies!.Single(c => c.Id == id);

    private static int MaxZ(DesignerModel m, string copyId) => m.Expanded().Layout.Components.Where(c => c.Widget == copyId).Max(c => c.Z);

    private static int MinZ(DesignerModel m, string copyId) => m.Expanded().Layout.Components.Where(c => c.Widget == copyId).Min(c => c.Z);

    [Fact]
    public void Enter_Goes_Down_And_Esc_Climbs_One_Depth_Before_Clearing()
    {
        var m = TwoDials();
        m.Select(["dial-1"]);

        Assert.True(m.Descend());
        Assert.Equal(Depth.Copy("dial-1", "dial"), m.Depth);
        Assert.Empty(m.Selection);

        m.Select(["dial-1.label"]);
        Assert.True(m.Descend());
        Assert.Equal(Depth.Widget("dial", "dial-1"), m.Depth);
        Assert.Equal(["dial-1.label"], m.Selection);   // the same part, one depth down
        Assert.False(m.Descend());                       // nowhere below a widget

        Assert.True(m.Climb());
        Assert.Equal(Depth.Copy("dial-1", "dial"), m.Depth);
        Assert.Equal(["dial-1.label"], m.Selection);

        Assert.True(m.Climb());
        Assert.Equal(Depth.Layout, m.Depth);
        Assert.Equal(["dial-1"], m.Selection);           // the copy it came out of

        Assert.False(m.Climb());                         // the window clears the selection now
    }

    [Fact]
    public void Enter_On_A_Selected_Part_Opens_Its_Copy_With_The_Part_Selected()
    {
        var m = TwoDials();
        m.Select(["dial-2.value"]);
        Assert.True(m.Descend());
        Assert.Equal(Depth.Copy("dial-2", "dial"), m.Depth);
        Assert.Equal(["dial-2.value"], m.Selection);
    }

    [Fact]
    public void Esc_From_A_Widget_With_No_Copy_Goes_Straight_To_Layout()
    {
        var m = TwoDials();
        m.SetDepth(Depth.Widget("dial", null));
        Assert.True(m.Climb());
        Assert.Equal(Depth.Layout, m.Depth);
    }

    [Fact]
    public void Duplicating_A_Copy_Makes_A_New_Copy_With_A_New_Id_As_One_Undo_Entry()
    {
        var m = TwoDials();
        var made = Assert.Single(m.Duplicate(["dial-1"], 16, 16));
        Assert.Equal("dial-3", made);
        var dup = Copy(m, "dial-3");
        Assert.Equal(("dial", 116, 66), (dup.Widget, dup.X, dup.Y));
        Assert.Equal("#FFFF8000", dup.Overrides["components.label.color"].LiteralText);   // overrides come too
        Assert.Equal(3, m.Layout.Copies!.Count);
        Assert.Empty(m.Layout.Components);   // a copy, not its expanded parts
        m.Undo();
        Assert.Equal(2, m.Layout.Copies!.Count);
    }

    [Fact]
    public void Paste_Offsets_Positions_And_Every_Paste_Gets_New_Ids()
    {
        var m = Loose();
        var json = m.CopyJson(["a", "dial-1"])!;
        var first = m.Paste(json, 16, 16);
        var second = m.Paste(json, 32, 32);
        Assert.Equal(["a-2", "dial-2"], first);
        Assert.Equal(["a-3", "dial-3"], second);
        Assert.Equal(new Rect(42, 42, 40, 20), m.Find("a-3")!.Rect);
        Assert.Equal((332, 82), (Copy(m, "dial-3").X, Copy(m, "dial-3").Y));
        m.Select(second);
        Assert.Equal(["a-3", "dial-3"], m.Selection);   // what the window selects after a paste
    }

    [Fact]
    public void A_Part_Copied_At_Layout_Depth_Pastes_Loose()
    {
        var m = TwoDials();
        var made = Assert.Single(m.Paste(m.CopyJson(["dial-1.label"])!, 16, 16));
        var pasted = m.Layout.Components.Single(c => c.Id == made);
        Assert.Null(pasted.Widget);
        Assert.Equal(2, m.Layout.Copies!.Count);
    }

    [Fact]
    public void Nothing_Pastes_At_Copy_Depth_And_Parts_Paste_Into_The_Widget_At_Widget_Depth()
    {
        var m = TwoDials();
        m.SetDepth(Depth.Copy("dial-1", "dial"));
        var json = m.CopyJson(["dial-1.label"])!;
        var undo = m.CanUndo;
        Assert.Empty(m.Paste(json, 16, 16));
        Assert.Equal(undo, m.CanUndo);

        m.SetDepth(Depth.Widget("dial", "dial-1"));
        var made = Assert.Single(m.Paste(json, 0, 20));
        Assert.Contains(m.WidgetEdits["dial"].Components, c => made.EndsWith(c.Id, StringComparison.Ordinal) && c.Id != "label");
    }

    [Fact]
    public void Bring_To_Front_And_Send_To_Back_Move_A_Copys_Z_Without_Overrides()
    {
        var m = TwoDials();
        m.BringToFront("dial-1");
        Assert.True(MinZ(m, "dial-1") > MaxZ(m, "dial-2"));
        Assert.NotEqual(0, Copy(m, "dial-1").Z);
        Assert.Single(Copy(m, "dial-1").Overrides);   // only the colour it already had

        m.SendToBack("dial-1");
        Assert.True(MaxZ(m, "dial-1") < MinZ(m, "dial-2"));
        Assert.Single(Copy(m, "dial-1").Overrides);

        m.SetZ("dial-2", 40);
        Assert.Equal(40, Copy(m, "dial-2").Z);
        Assert.Empty(Copy(m, "dial-2").Overrides);
    }

    [Fact]
    public void Bring_To_Front_At_Copy_Depth_Is_A_Z_Override_On_The_Part()
    {
        var m = TwoDials();
        m.SetDepth(Depth.Copy("dial-1", "dial"));
        var others = m.Parts.Components.Where(c => c.Id != "dial-1.dial").Max(c => c.Z);
        m.BringToFront("dial-1.dial");
        Assert.True(m.Find("dial-1.dial")!.Z > others);
        Assert.True(Copy(m, "dial-1").Overrides.ContainsKey("components.dial.z"));
        Assert.Equal(0, Copy(m, "dial-1").Z);
    }

    [Fact]
    public void Tab_Cycles_Siblings_At_The_Current_Depth_And_Ctrl_A_Takes_Them_All()
    {
        var m = TwoDials();
        m.SelectSibling(1);
        Assert.Equal(["dial-1"], m.Selection);
        m.SelectSibling(1);
        Assert.Equal(["dial-2"], m.Selection);
        m.SelectSibling(1);
        Assert.Equal(["dial-1"], m.Selection);   // wraps
        m.SelectSibling(-1);
        Assert.Equal(["dial-2"], m.Selection);

        m.SetDepth(Depth.Copy("dial-1", "dial"));
        m.SelectAll();
        Assert.Equal(3, m.Selection.Count);
        Assert.All(m.Selection, id => Assert.StartsWith("dial-1.", id, StringComparison.Ordinal));
    }

    [Fact]
    public void Ctrl_Alt_K_Edits_A_Selected_Copy_And_Makes_A_Widget_From_Loose_Parts()
    {
        var m = Loose();
        m.Select(["dial-1"]);
        Assert.True(m.MakeOrEditWidget());
        Assert.Equal(Depth.Widget("dial", "dial-1"), m.Depth);

        m.SetDepth(Depth.Layout);
        m.Select(["a", "b"]);
        Assert.True(m.MakeOrEditWidget());
        Assert.Equal(DepthKind.Widget, m.Depth.Kind);
        Assert.Empty(m.Layout.Components);   // a and b are the new widget's parts now
        Assert.Equal(2, m.WidgetEdits[m.Depth.WidgetKey!].Components.Count);

        m.SetDepth(Depth.Layout);
        m.ClearSelection();
        Assert.False(m.MakeOrEditWidget());
    }
    /// <summary>Critique 2, P1: the properties panel offers Make widget for two or more loose parts,
    /// and only then (a copy in the selection makes Ctrl+Alt+K an Edit widget instead).</summary>
    [Fact]
    public void Make_Widget_Is_Offered_For_Two_Or_More_Loose_Parts_Only()
    {
        var m = Loose();
        m.Select(["a", "b"]);
        Assert.True(m.CanMakeWidget);
        Assert.Null(m.EditableCopy());

        m.Select(["a"]);
        Assert.False(m.CanMakeWidget);                 // one part: the part's own properties show
        m.Select(["a", "dial-1"]);
        Assert.False(m.CanMakeWidget);                 // a copy in it: Ctrl+Alt+K edits that widget
        Assert.Equal("dial-1", m.EditableCopy()!.Id);
        m.Select(["a", "dial-1.label"]);
        Assert.False(m.CanMakeWidget);                 // a copy's part counts as the copy
        Assert.Equal("dial-1", m.EditableCopy()!.Id);

        m.Select(["a", "b"]);
        m.SetDepth(Depth.Copy("dial-1", "dial"));
        m.Select(["dial-1.label", "dial-1.value"]);
        Assert.False(m.CanMakeWidget);                 // not below layout depth
        Assert.Equal("dial-1", m.EditableCopy()!.Id);
    }

    [Fact]
    public void Edit_Widget_Says_How_Far_It_Reaches_In_The_Reset_Dialogs_Words()
    {
        var m = TwoDials();
        Assert.Equal(2, DepthText.CopiesOf(m.Layout, "DIAL"));
        Assert.Equal("Edit widget: every copy of it in every layout on this machine follows (2 are on this layout).", DepthText.Follow(2));
        Assert.Equal("Edit widget: every copy of it in every layout on this machine follows (1 is on this layout).", DepthText.Follow(1));
    }
    /// <summary>Critique 2, P1: widget depth says so however it was reached, and the fork clause is
    /// there only for a shipped key with no fork yet.</summary>
    [Fact]
    public void The_Widget_Depth_Banner_Says_What_Follows_And_When_Apply_Forks()
    {
        var m = TwoDials();
        Assert.Null(DepthText.Banner(m, _ => true));            // layout depth: no banner

        m.Select(["dial-1"]);
        m.Descend();
        Assert.Null(DepthText.Banner(m, _ => true));            // copy depth: the breadcrumb says it
        m.Descend();                                            // Enter again: widget depth
        var name = Copies.TryFind(m.Finder(), "dial")!.Name;
        Assert.Equal($"Editing the {name} widget: 2 copies on this layout follow · Apply forks the shipped widget", DepthText.Banner(m, _ => true));
        Assert.Equal($"Editing the {name} widget: 2 copies on this layout follow", DepthText.Banner(m, _ => false));

        m.Climb(); m.Climb();
        m.Edit("Remove", l => Copies.Remove(l, "dial-2"));
        m.Select(["dial-1"]);
        Assert.True(m.MakeOrEditWidget());                      // Ctrl+Alt+K: the same banner
        Assert.Equal($"Editing the {name} widget: 1 copy on this layout follows", DepthText.Banner(m, _ => false));
    }
}
