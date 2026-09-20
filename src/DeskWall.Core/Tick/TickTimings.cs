namespace DeskWall.Core.Tick;

public sealed class TickTimings
{
    public long ResolveMs, DrawMs, EncodeMs, ApplyMs, ShortcutsMs, TotalMs;
    public double CpuMs;
    public bool Skipped;
    public int Redrawn;

    public string ToTable() =>
        "stage      ms\n" +
        $"resolve    {ResolveMs}\n" +
        $"draw       {DrawMs}\n" +
        $"encode     {EncodeMs}\n" +
        $"apply      {ApplyMs}\n" +
        $"shortcuts  {ShortcutsMs}\n" +
        $"total      {TotalMs}   cpu {CpuMs:N0}   redrawn {Redrawn}{(Skipped ? "   SKIPPED" : "")}";
}
