using DeskWall.Core.Values;

namespace DeskWall.Core.Sources;

/// <summary>Fixed, ready drives. Bytes as numbers plus GB conveniences.</summary>
public sealed class DisksSource(string name, TimeSpan every, Func<IReadOnlyList<DriveInfo>>? drives = null)
    : PeriodicSource(name, every)
{
    private readonly Func<IReadOnlyList<DriveInfo>> _drives = drives ?? (() => DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).ToList());

    public override ValueTask<RecordValue> RefreshAsync(CancellationToken ct)
    {
        var items = new List<RecordValue>();
        foreach (var d in _drives())
        {
            double free = d.AvailableFreeSpace, total = d.TotalSize;
            items.Add(new RecordValue(new Dictionary<string, Value>
            {
                ["letter"] = new TextValue(d.Name.TrimEnd('\\', ':')),
                ["label"] = new TextValue(d.VolumeLabel),
                ["free"] = new NumberValue(free),
                ["total"] = new NumberValue(total),
                ["freeGB"] = new NumberValue(Math.Round(free / 1e9)),
                ["totalGB"] = new NumberValue(Math.Round(total / 1e9)),
                ["usedFraction"] = new NumberValue(total <= 0 ? 0 : (total - free) / total),
            }));
        }
        return new(new RecordValue(new Dictionary<string, Value> { ["drives"] = new ListValue(items, "letter") }));
    }
}
