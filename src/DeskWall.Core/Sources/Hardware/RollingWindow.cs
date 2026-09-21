namespace DeskWall.Core.Sources.Hardware;

/// <summary>Fixed-capacity ring of doubles. Allocates once; Add/Average/Latest never allocate.
/// Not thread-safe on its own: HardwareSource guards every window under one lock.</summary>
public sealed class RollingWindow(int capacity)
{
    private readonly double[] _slots = new double[Math.Max(1, capacity)];
    private int _next;

    public int Count { get; private set; }

    public void Add(double value)
    {
        _slots[_next] = value;
        _next = (_next + 1) % _slots.Length;
        if (Count < _slots.Length) Count++;
    }

    public double? Average
    {
        get
        {
            if (Count == 0) return null;
            double sum = 0;
            for (var i = 0; i < Count; i++) sum += _slots[i];
            return sum / Count;
        }
    }

    public double? Latest => Count == 0 ? null : _slots[(_next - 1 + _slots.Length) % _slots.Length];
}
