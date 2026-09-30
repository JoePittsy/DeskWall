using DeskWall.Core.Values;
using Windows.Win32;
namespace DeskWall.Core.Sources;

/// <summary>Windows power status; unknown charge and absent batteries publish no percentage.</summary>
public sealed class BatterySource(string name, TimeSpan every) : PeriodicSource(name, every)
{
    public override ValueTask<RecordValue> RefreshAsync(CancellationToken ct)
    {
        if (!PInvoke.GetSystemPowerStatus(out var s)) throw new System.ComponentModel.Win32Exception();
        var d = new Dictionary<string, Value>
        {
            ["charging"] = new BoolValue(s.BatteryFlag != 255 && (s.BatteryFlag & 8) != 0),
            ["onBattery"] = new BoolValue(s.ACLineStatus == 0),
            ["minutesLeft"] = new NumberValue(s.BatteryLifeTime == uint.MaxValue ? -1 : s.BatteryLifeTime / 60),
        };
        if (s.BatteryFlag != 255 && (s.BatteryFlag & 128) == 0 && s.BatteryLifePercent <= 100)
        {
            d["percent"] = new NumberValue(s.BatteryLifePercent);
            d["fraction"] = new NumberValue(s.BatteryLifePercent / 100.0);
            d["opacity"] = new NumberValue(s.ACLineStatus == 1 && s.BatteryLifePercent >= 95 ? 0.2 : 1);
        }
        return new(new RecordValue(d));
    }
}
