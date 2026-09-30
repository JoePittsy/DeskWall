namespace DeskWall.Core.Tick;

public sealed class TickTimings
{
    public long ResolveMs, DrawMs, EncodeMs, ApplyMs, ShortcutsMs, TotalMs;
    public double CpuMs;
    public bool Skipped;
    public int Redrawn;
    /// <summary>Draw milliseconds per component id, filled only when <see cref="TickRunner.MeasureLayers"/> is set.</summary>
    public Dictionary<string, double>? LayerMs;

    public string ToTable() =>
        "stage      ms\n" +
        $"resolve    {ResolveMs}\n" +
        $"draw       {DrawMs}\n" +
        $"encode     {EncodeMs}\n" +
        $"apply      {ApplyMs}\n" +
        $"shortcuts  {ShortcutsMs}\n" +
        $"total      {TotalMs}   cpu {CpuMs:N0}   redrawn {Redrawn}{(Skipped ? "   SKIPPED" : "")}";

    /// <summary>The per-layer draw costs, dearest first; empty when they were not measured.</summary>
    public string LayerTable()
    {
        if (LayerMs is not { Count: > 0 } ms) return "";
        var sb = new System.Text.StringBuilder("\nlayer                     draw ms");
        foreach (var (id, v) in ms.OrderByDescending(kv => kv.Value))
            sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"\n  {id,-24}{v,7:0.0}");
        return sb.ToString();
    }
}
