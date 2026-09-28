using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using DeskWall.Designer.Model;

namespace DeskWall.Designer.Views;

/// <summary>
/// The eight things worth doing to more than one selected widget: line them up on an edge or a
/// centre line of the selection's own bounding box, or even out the space between them.
/// <para>
/// Job: make "those three should line up" one click instead of six drags and a squint. It is over
/// the canvas rather than in the top bar because it acts on what is under it, and because the top
/// bar's element budget is already spent on the four verbs. It exists only while two or more
/// things are selected, and the two distribute buttons only come alive at three - a control that
/// can only disappoint is worse than no control.
/// </para>
/// <para>
/// Drawn, not lettered. "Left" and "Right" side by side read as a sentence; a bar with two blocks
/// pushed against it reads as what it does, and eight of them fit in a strip rather than a
/// ribbon. Each icon is a guide line where the edge lands plus two bars of unequal length, so it
/// says both which edge and that things move onto it.
/// </para>
/// </summary>
internal sealed class AlignBar : Border
{
    private const double IconBox = 16;
    private readonly List<Button> _distribute = [];

    public AlignBar()
    {
        CornerRadius = new CornerRadius(6);
        Padding = new Thickness(4);
        BorderThickness = new Thickness(1);
        SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorBaseAltBrush");
        SetResourceReference(BorderBrushProperty, "ControlStrokeColorDefaultBrush");

        var strip = new StackPanel { Orientation = Orientation.Horizontal };
        strip.Children.Add(Make(AlignOp.Left, "Align left"));
        strip.Children.Add(Make(AlignOp.CentreX, "Align horizontal centres"));
        strip.Children.Add(Make(AlignOp.Right, "Align right"));
        strip.Children.Add(Divider());
        strip.Children.Add(Make(AlignOp.Top, "Align top"));
        strip.Children.Add(Make(AlignOp.MiddleY, "Align vertical middles"));
        strip.Children.Add(Make(AlignOp.Bottom, "Align bottom"));
        strip.Children.Add(Divider());
        strip.Children.Add(Track(Make(DistributeAxis.Horizontal, "Distribute horizontally")));
        strip.Children.Add(Track(Make(DistributeAxis.Vertical, "Distribute vertically")));
        Child = strip;
    }

    /// <summary>The owner asked for the selection to be lined up on one of its own edges.</summary>
    public event Action<AlignOp>? Aligned;

    /// <summary>The owner asked for the space between the selected things to be evened out.</summary>
    public event Action<DistributeAxis>? Distributed;

    /// <summary>Two things can be aligned but cannot have an uneven gap between them, so the two
    /// distribute buttons go grey until there are three.</summary>
    public void SetSelectionCount(int count)
    {
        foreach (var button in _distribute) button.IsEnabled = count >= 3;
    }

    private Button Track(Button button) { _distribute.Add(button); return button; }

    private Button Make(AlignOp op, string label) => MakeButton(Icon(op), label, () => Aligned?.Invoke(op));

    private Button Make(DistributeAxis axis, string label) => MakeButton(Icon(axis), label, () => Distributed?.Invoke(axis));

    private static Button MakeButton(Geometry icon, string label, Action click)
    {
        var glyph = new Path { Data = icon, Width = IconBox, Height = IconBox, Stretch = Stretch.None };
        glyph.SetResourceReference(Shape.FillProperty, "TextFillColorPrimaryBrush");
        var button = new Button
        {
            Content = glyph,
            Width = 30,
            Height = 28,
            Padding = new Thickness(0),
            Margin = new Thickness(1, 0, 1, 0),
            ToolTip = label,
        };
        AutomationProperties.SetName(button, label);
        button.Click += (_, _) => click();
        return button;
    }

    private static UIElement Divider()
    {
        var line = new Border { Width = 1, Margin = new Thickness(4, 5, 4, 5) };
        line.SetResourceReference(BackgroundProperty, "DividerStrokeColorDefaultBrush");
        return line;
    }

    // ---- the icons ---------------------------------------------------------------------------

    private static Geometry Icon(AlignOp op)
    {
        const double guide = 1.5, edge = 1;
        var g = new GeometryGroup();
        switch (op)
        {
            case AlignOp.Left:
                g.Children.Add(Bar(0, 1, guide, 14, 0));
                g.Children.Add(Bar(3, 3, 11, 4, edge));
                g.Children.Add(Bar(3, 9, 7, 4, edge));
                break;
            case AlignOp.CentreX:
                g.Children.Add(Bar((IconBox - guide) / 2, 1, guide, 14, 0));
                g.Children.Add(Bar(2.5, 3, 11, 4, edge));
                g.Children.Add(Bar(4.5, 9, 7, 4, edge));
                break;
            case AlignOp.Right:
                g.Children.Add(Bar(IconBox - guide, 1, guide, 14, 0));
                g.Children.Add(Bar(2, 3, 11, 4, edge));
                g.Children.Add(Bar(6, 9, 7, 4, edge));
                break;
            case AlignOp.Top:
                g.Children.Add(Bar(1, 0, 14, guide, 0));
                g.Children.Add(Bar(3, 3, 4, 11, edge));
                g.Children.Add(Bar(9, 3, 4, 7, edge));
                break;
            case AlignOp.MiddleY:
                g.Children.Add(Bar(1, (IconBox - guide) / 2, 14, guide, 0));
                g.Children.Add(Bar(3, 2.5, 4, 11, edge));
                g.Children.Add(Bar(9, 4.5, 4, 7, edge));
                break;
            case AlignOp.Bottom:
                g.Children.Add(Bar(1, IconBox - guide, 14, guide, 0));
                g.Children.Add(Bar(3, 2, 4, 11, edge));
                g.Children.Add(Bar(9, 6, 4, 7, edge));
                break;
            default:
                break;
        }
        g.Freeze();
        return g;
    }

    /// <summary>Three bars with equal space between them, which is the whole of what distribute
    /// promises.</summary>
    private static Geometry Icon(DistributeAxis axis)
    {
        var g = new GeometryGroup();
        for (var i = 0; i < 3; i++)
        {
            var at = 1 + i * 5.5;
            g.Children.Add(axis == DistributeAxis.Horizontal
                ? Bar(at, 3, 3.5, 10, 1)
                : Bar(3, at, 10, 3.5, 1));
        }
        g.Freeze();
        return g;
    }

    private static RectangleGeometry Bar(double x, double y, double w, double h, double radius)
        => new(new Rect(x, y, w, h), radius, radius);
}
