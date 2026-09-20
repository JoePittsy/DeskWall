using DeskWall.Core.Resolve;

namespace DeskWall.Core.Shortcuts;

/// <summary>Where the shell draws the shortcut-arrow overlay relative to an item's position, for one
/// icon size and display scale. Measured by the calibrator; the POC's constants are the seed.</summary>
public sealed record ArrowRect(int Dx, int Dy, int Size);

/// <summary>Pure geometry. Item position = where the shell puts the icon's top-left; the arrow overlay
/// sits at item + (Dx, Dy) with side Size; we want the arrow Pad px from the cover's bottom-left.</summary>
public static class ShortcutPlan
{
    public const int DefaultPad = 5;
    private const char Nbsp = ' ';

    /// <summary>item.x = rect.X + pad - arrow.Dx ; item.y = rect.Bottom - pad - arrow.Size - arrow.Dy</summary>
    public static (int X, int Y) IconPosition(Rect cover, ArrowRect arrow, int pad = DefaultPad)
        => (cover.X + pad - arrow.Dx, cover.Bottom - pad - arrow.Size - arrow.Dy);

    /// <summary>Slot file name: (slot + 1) non-breaking spaces + ".lnk" (slot is 0-based).</summary>
    public static string SlotFileName(int slot)
    {
        if (slot is < 0 or > 63) throw new ArgumentOutOfRangeException(nameof(slot), "slots are 0..63");
        return new string(Nbsp, slot + 1) + ".lnk";
    }

    public static int? SlotFromFileName(string fileName)
    {
        if (!fileName.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return null;
        var stem = fileName[..^4];
        if (stem.Length == 0 || stem.Any(ch => ch != Nbsp)) return null;
        return stem.Length - 1;
    }

    /// <summary>Stable order: by Slot, then Id. Duplicated slots throw (layout error).</summary>
    public static IReadOnlyList<ResolvedShortcut> Ordered(IEnumerable<ResolvedShortcut> shortcuts)
    {
        var list = shortcuts.OrderBy(s => s.Slot).ThenBy(s => s.Id, StringComparer.Ordinal).ToList();
        for (var i = 1; i < list.Count; i++)
            if (list[i].Slot == list[i - 1].Slot)
                throw new InvalidOperationException($"shortcuts '{list[i - 1].Id}' and '{list[i].Id}' both use slot {list[i].Slot}");
        return list;
    }
}
