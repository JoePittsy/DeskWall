using DeskWall.Core;
using DeskWall.Designer.Model;
using Xunit;

public class ViewportTests
{
    private static readonly Viewport Pane = new(1, 0, 0, 1000, 600);

    [Fact]
    public void Fit_Centres_The_Content_With_A_Margin()
    {
        var v = Pane.Fit(new Rect(0, 0, 3440, 1440));
        Assert.Equal((1000 - 48) / 3440.0, v.Zoom, 9);
        var (x, y, w, h) = v.ToScreen(new Rect(0, 0, 3440, 1440));
        Assert.Equal(24, x, 6);
        Assert.Equal(500, x + w / 2, 6);
        Assert.Equal(300, y + h / 2, 6);
    }

    [Fact]
    public void Fit_Of_A_Small_Thing_Stops_At_The_Cap_And_The_Range()
    {
        Assert.Equal(4, Pane.Fit(new Rect(100, 100, 13, 13), maxZoom: 4).Zoom);
        Assert.Equal(Viewport.MaxZoom, Pane.Fit(new Rect(100, 100, 2, 2)).Zoom);
        Assert.Equal(Viewport.MinZoom, Pane.Fit(new Rect(0, 0, 100000, 100000)).Zoom);
    }

    [Fact]
    public void ZoomAbout_Keeps_The_Point_Under_The_Cursor()
    {
        var v = Pane.Fit(new Rect(0, 0, 3440, 1440));
        var before = v.ToCanvas(730, 210);
        var z = v.ZoomAbout(8, 730, 210);
        var after = z.ToCanvas(730, 210);
        Assert.Equal(8, z.Zoom);
        Assert.Equal(before.X, after.X, 6);
        Assert.Equal(before.Y, after.Y, 6);
    }

    [Fact]
    public void Zoom_Is_Clamped_To_An_Eighth_And_Sixteen()
    {
        Assert.Equal(16, Pane.ZoomAbout(100, 0, 0).Zoom);
        Assert.Equal(0.125, Pane.ZoomAbout(0.001, 0, 0).Zoom);
        Assert.Equal(1, Pane.ZoomAbout(double.NaN, 0, 0).Zoom);
    }

    [Fact]
    public void Step_Walks_The_Steps_And_Stops_At_The_Ends()
    {
        Assert.Equal(1.5, Pane.Step(zoomIn: true).Zoom);
        Assert.Equal(2.0 / 3, Pane.Step(zoomIn: false).Zoom, 9);
        Assert.Equal(3, (Pane with { Zoom = 2.4 }).Step(zoomIn: true).Zoom);
        Assert.Equal(2, (Pane with { Zoom = 2.4 }).Step(zoomIn: false).Zoom);
        Assert.Equal(16, (Pane with { Zoom = 16 }).Step(zoomIn: true).Zoom);
        Assert.Equal(0.125, (Pane with { Zoom = 0.125 }).Step(zoomIn: false).Zoom);
    }

    [Fact]
    public void ToCanvas_Inverts_ToScreen()
    {
        var v = new Viewport(8, -25000, -300, 1000, 600);
        var (x, y, _, _) = v.ToScreen(new Rect(3200, 60, 10, 10));
        var (cx, cy) = v.ToCanvas(x, y);
        Assert.Equal(3200, cx, 9);
        Assert.Equal(60, cy, 9);
    }

    [Fact]
    public void Clamp_Keeps_Part_Of_The_Canvas_In_The_Pane()
    {
        var far = new Viewport(8, 5000, -99999, 1000, 600).Clamp(3440, 1440);
        Assert.Equal(1000 - Viewport.KeepVisible, far.OriginX);                   // near edge no further than this
        Assert.Equal(Viewport.KeepVisible - 1440 * 8, far.OriginY);                // far edge at least this far in
        var inside = new Viewport(8, -20000, -3000, 1000, 600);
        Assert.Equal(inside, inside.Clamp(3440, 1440));                            // in range: untouched
    }

    [Fact]
    public void Resized_Keeps_The_Centre_Point()
    {
        var v = new Viewport(2, -100, -50, 1000, 600);
        var centre = v.ToCanvas(500, 300);
        var r = v.Resized(1400, 800);
        var after = r.ToCanvas(700, 400);
        Assert.Equal(centre.X, after.X, 9);
        Assert.Equal(centre.Y, after.Y, 9);
    }
}
