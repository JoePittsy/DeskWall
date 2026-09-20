namespace DeskWall.Core.Render;

public enum TextEffect { None, Shadow, Outline, Plate }

/// <summary>Resolved text appearance. Defaults match the spec: soft shadow, 6 px blur.</summary>
public sealed record TextStyle(
    string Font = "Segoe UI",
    float Size = 16f,
    int Weight = 400,
    Color Color = default,
    Align Align = Align.Left,
    TextEffect Effect = TextEffect.Shadow,
    float EffectRadius = 6f,
    Color EffectColor = default)
{
    public static TextStyle Default => new(Color: Color.White, EffectColor: new Color(160, 0, 0, 0));
}
