namespace DeskWall.Core;

/// <summary>Physical pixels. Immutable.</summary>
public readonly record struct Rect(int X, int Y, int W, int H)
{
    public int Right => X + W;
    public int Bottom => Y + H;
    public bool Intersects(Rect o) => X < o.Right && o.X < Right && Y < o.Bottom && o.Y < Bottom;
    public Rect Offset(int dx, int dy) => new(X + dx, Y + dy, W, H);
    public Rect Scale(double sx, double sy) => new((int)Math.Round(X * sx), (int)Math.Round(Y * sy), (int)Math.Round(W * sx), (int)Math.Round(H * sy));
}

public enum Fit { Cover, Contain, Stretch }
public enum Axis { Vertical, Horizontal }
public enum Align { Left, Center, Right }
