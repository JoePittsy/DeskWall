using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using DeskWall.Core.Display;
using DeskWall.Core.Layout;
using DeskWall.Designer.Model;
using DeskWall.Designer.Views;
using Xunit;

/// <summary>Critique 3, P2-a: the picker previews live while dragged and makes one undo entry per
/// gesture (a drag, or a run of arrow keys), and offers the colours already in use.</summary>
public class ColorPickerTests
{
    private static DesignerModel Plain() => new(LayoutFile.Parse("""
        { "version": 2, "baseImage": "x.jpg", "sources": [],
          "components": [
            { "type": "text", "id": "a", "rect": [10, 10, 100, 20], "text": "a", "color": "#FFFF0000" },
            { "type": "text", "id": "b", "rect": [10, 40, 100, 20], "text": "b", "color": "#ff0000" },
            { "type": "text", "id": "c", "rect": [10, 70, 100, 20], "text": "c", "color": "#FF00FF00" } ] }
        """), new DisplaySignature("T", 1000, 800, 100), null);

    private static string Colour(DesignerModel m, string id) => ((TextDef)m.Find(id)!).Color.LiteralText!;

    private static void SetColour(DesignerModel m, string hex)
        => m.EditAtDepth("Set Text colour", l => ((TextDef)l.Components.Single(c => c.Id == "a")).Color = PropertyValue.Literal(hex));

    [Fact]
    public void Previews_Draw_But_Make_No_Undo_Entry_And_The_Commit_After_Them_Is_One()
    {
        var m = Plain();
        var before = m.ToJson();
        int changed = 0, previewed = 0;
        m.Changed += () => changed++;
        m.Previewed += () => previewed++;

        m.Transient(() => SetColour(m, "#FF111111"));
        m.Transient(() => SetColour(m, "#FF222222"));
        Assert.Equal("#FF222222", Colour(m, "a"));   // what the canvas draws
        Assert.False(m.CanUndo);
        Assert.Equal(0, changed);
        Assert.Equal(2, previewed);

        m.EndTransient();                            // the release: back to the start, then one commit
        SetColour(m, "#FF333333");
        Assert.Equal(1, changed);
        m.Undo();
        Assert.False(m.CanUndo);
        Assert.Equal(before, m.ToJson());
    }

    [Fact]
    public void A_Preview_Left_Behind_Is_Taken_Back_By_An_Undo_And_Keeps_The_Redo()
    {
        var m = Plain();
        SetColour(m, "#FF444444");
        m.Undo();
        var original = m.ToJson();
        m.Transient(() => SetColour(m, "#FF555555"));
        Assert.True(m.CanRedo);
        m.EndTransient();
        Assert.Equal(original, m.ToJson());
        Assert.True(m.CanRedo);
        m.Redo();
        Assert.Equal("#FF444444", Colour(m, "a"));
    }

    [Fact]
    public void Colours_In_Use_Are_Normalised_Deduplicated_Most_Used_First_And_At_Most_Eight()
    {
        // Three texts: each has the default shadow colour, two share red (written two ways), one is green.
        Assert.Equal(["#A0000000", "#FFFF0000", "#FF00FF00"], ColorModel.InUse(Plain().Layout.Components));
        var many = Enumerable.Range(0, 12).Select(i => new TextDef { Id = "t" + i, Rect = new DeskWall.Core.Rect(0, 0, 1, 1), Text = PropertyValue.Literal("x"), Color = PropertyValue.Literal($"#FF0000{i:X2}") });
        Assert.Equal(8, ColorModel.InUse(many).Count);
    }

    [Fact]
    [Trait("Category", "Desktop")]
    public void A_Held_Arrow_Key_Is_One_Final_Change_On_Key_Up_With_The_Colour_From_Before()
    {
        OnStaThread(() =>
        {
            var picker = new ColorPicker { Value = "#FF808080" };
            var window = new Window { Content = picker, Width = 300, Height = 400, Left = -2000, WindowStartupLocation = WindowStartupLocation.Manual };
            window.Show();
            try
            {
                var events = new List<ColorValueChangedEventArgs>();
                picker.ValueChanged += (_, e) => events.Add(e);
                var area = (UIElement)picker.FindName("Area");
                var source = PresentationSource.FromVisual(area)!;
                void Raise(RoutedEvent routed) => area.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Right) { RoutedEvent = routed });

                for (var i = 0; i < 3; i++) { Raise(Keyboard.PreviewKeyDownEvent); Raise(Keyboard.KeyDownEvent); }
                Assert.Equal(3, events.Count);
                Assert.All(events, e => Assert.False(e.IsFinal));
                Raise(Keyboard.PreviewKeyUpEvent);

                var final = Assert.Single(events, e => e.IsFinal);
                Assert.Same(events[^1], final);
                Assert.Equal("#FF808080", final.OldValue);
                Assert.Equal(picker.Value, final.NewValue);
                Assert.NotEqual("#FF808080", final.NewValue);

                // A swatch is one final change on its own.
                events.Clear();
                picker.Swatches = ["#FF00FF00"];
                var row = (System.Windows.Controls.Panel)picker.FindName("SwatchRow");
                var chip = (System.Windows.Controls.Button)row.Children[0];
                chip.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert.True(Assert.Single(events).IsFinal);
                Assert.Equal("#FF00FF00", picker.Value);
            }
            finally { window.Close(); }
        });
    }

    private static void OnStaThread(Action body)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}
