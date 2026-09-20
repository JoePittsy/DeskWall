# DeskWall v1 Phase 3: Shortcut Manager Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Every `ResolvedShortcut` in a tick gets a labelless, transparent desktop shortcut placed so
the shell's arrow overlay sits a configured padding from the shortcut rect's bottom-left, exactly
as the POC did, with the arrow offset measured by `deskwall calibrate` instead of hard-coded.
Desktop preconditions (auto-arrange, snap-to-grid) are enforced and restored on uninstall.

**Architecture:** `DeskWall.Core.Shortcuts` owns four units: `BlankIcon` (the transparent ICO),
`ShortcutFiles` (the `.lnk` per slot, written through `IShellLinkW` + `IPersistFile`),
`DesktopView` (the POC's `DeskIcons.cs` ported to CsWin32: `IFolderView2` positioning, reading and
folder flags), and `Calibration` (the measured arrow rect per icon size and scale). `ShortcutManager`
composes them: given the resolved shortcuts, make the desktop match. `TickRunner` calls it in stage 7
when any shortcut's rect, target or tooltip changed.

**Tech Stack:** as Phase 1: CsWin32 with `allowMarshaling: false`, xUnit. No new packages.

**Spec:** `docs/superpowers/specs/2026-09-20-deskwall-v1-design.md` (section 7)
**Master plan:** `docs/superpowers/plans/2026-09-20-deskwall-v1-master.md`
**POC reference:** `poc/DeskIcons.cs`, `poc/shortcuts.ps1`, `poc/verify.ps1` (the working COM sequence and the measured constants: 48 px icons at 100 percent, arrow 13x13 at (item.x + 0, item.y + 40), snap-to-grid must be off)
**Phase 1 results:** `docs/superpowers/plans/2026-09-20-phase1-spike-results.md` (CsWin32 shapes)

## Global Constraints

- Everything in the master plan's Global Constraints.
- Desktop tests are **real**: they create shortcuts on the user's desktop. Every test that does so
  uses names that cannot collide with the user's files (a `DeskWallTest-<guid>` prefix, NOT the
  non-breaking-space names) and deletes them in `finally`. A test that leaves a file on the desktop
  is a defect. The real slot files (`\u00A0` x N `.lnk`) are created only by `ShortcutManager` in
  Task 6's live check and removed by `uninstall`.
- Nothing here draws. Shortcuts are invisible; the cover pixels come from the image component.
- Preconditions are enforced through `IFolderView2::SetCurrentFolderFlags`, never by editing
  Explorer's registry; previous flags are saved to `desktop-flags.json` in the runtime dir for
  `uninstall` to restore.
- CsWin32 facts from the Phase 1 results file apply (void COM methods throw; static entry points
  return `HRESULT`; `Com.EnsureInitialized()` first).
- Lane assignment (cap in the ledger before dispatch): Task 1 seam by the controller; `lane/p3-files`
  (Sonnet: Tasks 2, 3); `lane/p3-view` (Opus: Tasks 4, 5); Tasks 6, 7 by the controller.

## Interfaces shipped by Phase 1 that this phase consumes

```csharp
ResolvedShortcut(string Id, Rect Rect, int Z, string Target, string Tooltip, int Slot)   // Rect = the cover's rect on the canvas
TickRunner.LastShortcuts : IReadOnlyList<ResolvedShortcut>
TickTimings.ShortcutsMs
MonitorInfo(Signature, Bounds, IsPrimary, WallpaperMonitorId); DisplaySignature.ScalePercent
Paths.InRuntime(...); Com.EnsureInitialized()
Surface (for calibrate: screenshot diff)  -- Surface.Load(path), GetPixel
```

## File structure

```
src/DeskWall.Core/Shortcuts/ShortcutPlan.cs        pure geometry: slot -> desired icon position (Task 1)
src/DeskWall.Core/Shortcuts/Calibration.cs          arrow rect per (iconSize, scale); load/save (Task 1)
src/DeskWall.Core/Shortcuts/BlankIcon.cs            transparent 256 px PNG-in-ICO (Task 2)
src/DeskWall.Core/Shortcuts/ShortcutFiles.cs        .lnk write/read/delete per slot (Task 3)
src/DeskWall.Core/Shortcuts/DesktopView.cs          IFolderView2: positions, flags, item lookup (Task 4)
src/DeskWall.Core/Shortcuts/DesktopFlags.cs         save/restore of auto-arrange and snap (Task 4)
src/DeskWall.Core/Shortcuts/Calibrator.cs           deskwall calibrate (Task 5)
src/DeskWall.Core/Shortcuts/ShortcutManager.cs      reconcile (Task 6)
src/DeskWall.Core/Tick/TickRunner.cs                stage 7 wiring (Task 6)
src/DeskWall.Daemon/Program.cs                      calibrate, shortcuts commands (Task 6)
tests/DeskWall.Core.Tests/Shortcuts/*Tests.cs
```

---

### Task 1: Seam: `ShortcutPlan` and `Calibration` (controller)

**Files:**
- Create: `src/DeskWall.Core/Shortcuts/ShortcutPlan.cs`, `src/DeskWall.Core/Shortcuts/Calibration.cs`
- Test: `tests/DeskWall.Core.Tests/Shortcuts/ShortcutPlanTests.cs`, `tests/DeskWall.Core.Tests/Shortcuts/CalibrationTests.cs`

**Interfaces:**
- Produces:

```csharp
namespace DeskWall.Core.Shortcuts;

/// <summary>Where the shell draws the shortcut-arrow overlay relative to an item's position, for one
/// icon size and display scale. Measured by Calibrator; the POC's constants are the seed.</summary>
public sealed record ArrowRect(int Dx, int Dy, int Size);   // 48 px @ 100%: (0, 40, 13)

public sealed class Calibration
{
    public static Calibration Load();                       // runtime/calibration.json, or Seed() when absent
    public static Calibration Seed();                        // { "48@100": (0,40,13) }
    public ArrowRect? Get(int iconSize, int scalePercent);
    public void Set(int iconSize, int scalePercent, ArrowRect arrow);
    public void Save();
    public static string Key(int iconSize, int scalePercent) => $"{iconSize}@{scalePercent}";
}

/// <summary>Pure geometry. Item position = where the shell puts the icon's top-left; the arrow overlay
/// sits at item + (Dx, Dy) with side Size; we want the arrow Pad px from the cover's bottom-left.</summary>
public static class ShortcutPlan
{
    public const int DefaultPad = 5;
    /// <summary>item.x = rect.X + pad - arrow.Dx ; item.y = rect.Bottom - pad - arrow.Size - arrow.Dy</summary>
    public static (int X, int Y) IconPosition(Rect cover, ArrowRect arrow, int pad = DefaultPad);
    /// <summary>Slot file name: (slot + 1) non-breaking spaces + ".lnk" (slot is 0-based).</summary>
    public static string SlotFileName(int slot);
    public static int? SlotFromFileName(string fileName);   // null if not a slot name
    /// <summary>Stable order: by Slot, then Id. Duplicated slots throw (layout error).</summary>
    public static IReadOnlyList<ResolvedShortcut> Ordered(IEnumerable<ResolvedShortcut> shortcuts);
}
```

- [ ] **Step 1: Failing tests**

```csharp
using DeskWall.Core;
using DeskWall.Core.Resolve;
using DeskWall.Core.Shortcuts;
using Xunit;

public class ShortcutPlanTests
{
    [Fact]
    public void IconPosition_Matches_The_Poc_Constants()
    {
        // POC shortcuts.ps1: item.x = cover.X + Pad - 0 ; item.y = cover.Y + cover.H - Pad - 13 - 40
        var cover = new Rect(3220, 142, 172, 258);
        var (x, y) = ShortcutPlan.IconPosition(cover, new ArrowRect(0, 40, 13));
        Assert.Equal(3225, x);
        Assert.Equal(142 + 258 - 5 - 13 - 40, y);
    }

    [Fact]
    public void Slot_Names_Are_NonBreaking_Spaces_And_RoundTrip()
    {
        Assert.Equal("\u00A0.lnk", ShortcutPlan.SlotFileName(0));
        Assert.Equal("\u00A0\u00A0\u00A0\u00A0.lnk", ShortcutPlan.SlotFileName(3));
        Assert.Equal(3, ShortcutPlan.SlotFromFileName("\u00A0\u00A0\u00A0\u00A0.lnk"));
        Assert.Null(ShortcutPlan.SlotFromFileName("Steam.lnk"));
        Assert.Null(ShortcutPlan.SlotFromFileName("\u00A0 x.lnk"));
    }

    [Fact]
    public void Ordered_Sorts_By_Slot_And_Rejects_Duplicates()
    {
        var a = new ResolvedShortcut("b", new Rect(0, 0, 1, 1), 0, "x", "", 1);
        var b = new ResolvedShortcut("a", new Rect(0, 0, 1, 1), 0, "x", "", 0);
        Assert.Equal(["a", "b"], ShortcutPlan.Ordered([a, b]).Select(s => s.Id));
        Assert.Throws<InvalidOperationException>(() => ShortcutPlan.Ordered([a, a with { Id = "c" }]));
    }
}

public class CalibrationTests
{
    [Fact]
    public void Seed_Has_The_Poc_Value_And_Save_Load_RoundTrips()
    {
        var c = Calibration.Seed();
        Assert.Equal(new ArrowRect(0, 40, 13), c.Get(48, 100));
        Assert.Null(c.Get(32, 100));
        c.Set(32, 125, new ArrowRect(1, 27, 9));
        c.Save();
        var back = Calibration.Load();
        Assert.Equal(new ArrowRect(1, 27, 9), back.Get(32, 125));
        Assert.Equal(new ArrowRect(0, 40, 13), back.Get(48, 100));
    }
}
```

- [ ] **Step 2: Run, expect compile failure.**
- [ ] **Step 3: Implement**

```csharp
namespace DeskWall.Core.Shortcuts;

public sealed record ArrowRect(int Dx, int Dy, int Size);

public static class ShortcutPlan
{
    public const int DefaultPad = 5;
    private const char Nbsp = '\u00A0';

    public static (int X, int Y) IconPosition(Rect cover, ArrowRect arrow, int pad = DefaultPad)
        => (cover.X + pad - arrow.Dx, cover.Bottom - pad - arrow.Size - arrow.Dy);

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

    public static IReadOnlyList<Resolve.ResolvedShortcut> Ordered(IEnumerable<Resolve.ResolvedShortcut> shortcuts)
    {
        var list = shortcuts.OrderBy(s => s.Slot).ThenBy(s => s.Id, StringComparer.Ordinal).ToList();
        for (var i = 1; i < list.Count; i++)
            if (list[i].Slot == list[i - 1].Slot) throw new InvalidOperationException($"shortcuts '{list[i - 1].Id}' and '{list[i].Id}' both use slot {list[i].Slot}");
        return list;
    }
}
```

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeskWall.Core.Shortcuts;

public sealed class Calibration
{
    private readonly Dictionary<string, int[]> _map;
    private Calibration(Dictionary<string, int[]> map) { _map = map; }

    private static string FilePath => Paths.InRuntime("calibration.json");
    public static string Key(int iconSize, int scalePercent) => $"{iconSize}@{scalePercent}";

    public static Calibration Seed() => new(new Dictionary<string, int[]> { [Key(48, 100)] = [0, 40, 13] });

    public static Calibration Load()
    {
        var seed = Seed();
        if (!File.Exists(FilePath)) return seed;
        try
        {
            var stored = JsonSerializer.Deserialize(File.ReadAllText(FilePath), CalibrationJsonContext.Default.DictionaryStringInt32Array) ?? new();
            foreach (var (k, v) in stored) if (v.Length == 3) seed._map[k] = v;
            return seed;
        }
        catch (JsonException) { return seed; }
    }

    public ArrowRect? Get(int iconSize, int scalePercent)
        => _map.TryGetValue(Key(iconSize, scalePercent), out var v) ? new ArrowRect(v[0], v[1], v[2]) : null;

    public void Set(int iconSize, int scalePercent, ArrowRect arrow) => _map[Key(iconSize, scalePercent)] = [arrow.Dx, arrow.Dy, arrow.Size];

    public void Save()
    {
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_map, CalibrationJsonContext.Default.DictionaryStringInt32Array));
        File.Move(tmp, FilePath, overwrite: true);
    }
}

[JsonSerializable(typeof(Dictionary<string, int[]>))]
internal partial class CalibrationJsonContext : JsonSerializerContext;
```

- [ ] **Step 4: Run tests, expect pass** (4).
- [ ] **Step 5: Commit** `git commit -m "Phase 3 seam: ShortcutPlan geometry and slot names; Calibration store seeded with POC constants"`

Fan-out starts here.

---

### Task 2: `BlankIcon` (lane `lane/p3-files`, Sonnet)

**Files:**
- Create: `src/DeskWall.Core/Shortcuts/BlankIcon.cs`
- Test: `tests/DeskWall.Core.Tests/Shortcuts/BlankIconTests.cs`

**Interfaces:**
- Produces: `BlankIcon.Ensure() : string` returning `runtime/blank.ico`, writing it if absent: an ICO
  with one 256x256 32-bit PNG image that is fully transparent (the POC's format: 6-byte header,
  16-byte directory entry, PNG payload; `bWidth`/`bHeight` are 0 meaning 256).

- [ ] **Step 1: Failing test**

```csharp
using DeskWall.Core.Shortcuts;
using Xunit;

public class BlankIconTests
{
    [Fact]
    public void Ensure_Writes_A_Valid_Single_Image_Png_Ico_Once()
    {
        var p = BlankIcon.Ensure();
        var b = File.ReadAllBytes(p);
        Assert.Equal(0, BitConverter.ToUInt16(b, 0));     // reserved
        Assert.Equal(1, BitConverter.ToUInt16(b, 2));     // type icon
        Assert.Equal(1, BitConverter.ToUInt16(b, 4));     // one image
        Assert.Equal(0, b[6]); Assert.Equal(0, b[7]);     // 256 x 256
        Assert.Equal(32, BitConverter.ToUInt16(b, 12));   // bpp
        var size = BitConverter.ToInt32(b, 14); var offset = BitConverter.ToInt32(b, 18);
        Assert.Equal(22, offset);
        Assert.Equal(b.Length, offset + size);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, b[offset..(offset + 4)]);   // PNG signature
        var t1 = File.GetLastWriteTimeUtc(p);
        Assert.Equal(p, BlankIcon.Ensure());
        Assert.Equal(t1, File.GetLastWriteTimeUtc(p));    // not rewritten
    }
}
```

- [ ] **Step 2: Run, expect compile failure.**
- [ ] **Step 3: Implement** (use `Surface` to produce the PNG bytes so no other encoder is involved)

```csharp
using DeskWall.Core.Render;

namespace DeskWall.Core.Shortcuts;

public static class BlankIcon
{
    public static string Ensure()
    {
        var path = Paths.InRuntime("blank.ico");
        if (File.Exists(path)) return path;
        var pngPath = path + ".png.tmp";
        using (var s = Surface.Create(256, 256)) { s.Clear(Color.Transparent); s.SavePng(pngPath); }
        var png = File.ReadAllBytes(pngPath);
        File.Delete(pngPath);
        using var bw = new BinaryWriter(File.Create(path + ".tmp"));
        bw.Write((ushort)0); bw.Write((ushort)1); bw.Write((ushort)1);
        bw.Write((byte)0); bw.Write((byte)0);          // 256 x 256
        bw.Write((byte)0); bw.Write((byte)0);          // palette, reserved
        bw.Write((ushort)1); bw.Write((ushort)32);     // planes, bpp
        bw.Write((uint)png.Length); bw.Write((uint)22);
        bw.Write(png);
        bw.Flush(); bw.Close();
        File.Move(path + ".tmp", path, overwrite: true);
        return path;
    }
}
```

- [ ] **Step 4: Run tests, expect pass.**
- [ ] **Step 5: Commit** `git commit -m "BlankIcon: transparent 256 px PNG-in-ICO in the runtime dir"`

---

### Task 3: `ShortcutFiles` (lane `lane/p3-files`, Sonnet)

**Files:**
- Create: `src/DeskWall.Core/Shortcuts/ShortcutFiles.cs`
- Modify: `src/DeskWall.Core/NativeMethods.txt` (append `IShellLinkW`, `ShellLink`, `IPersistFile`, `CoCreateInstance` is present)
- Test: `tests/DeskWall.Core.Tests/Shortcuts/ShortcutFilesTests.cs`

**Interfaces:**
- Produces:

```csharp
public sealed record ShortcutSpec(string Target, string Arguments, string WorkingDirectory, string Description, string IconPath);

/// <summary>.lnk files through IShellLinkW + IPersistFile. Targets of the form "steam://..." (a URI) are
/// written as the target path verbatim: the shell launches URIs from .lnk targets via their protocol handler.
/// A target with arguments is split on the first unquoted space; a quoted program path is honoured.</summary>
public static class ShortcutFiles
{
    public static string DesktopDir();                                     // Environment.GetFolderPath(Desktop)
    public static ShortcutSpec SpecFor(ResolvedShortcut s, string iconPath);   // parses Target into program + args; Description = Tooltip
    public static void Write(string lnkPath, ShortcutSpec spec);           // create or overwrite
    public static ShortcutSpec? Read(string lnkPath);                      // null if missing or not a link
    public static void Delete(string lnkPath);
}
```

`SpecFor` rules: `steam://rungameid/620` -> Target `steam://rungameid/620`, Arguments "";
`"C:\Program Files\X\x.exe" --start abc` -> Target `C:\Program Files\X\x.exe`, Arguments `--start abc`;
`explorer.exe D:\` -> Target `explorer.exe`, Arguments `D:\`. WorkingDirectory = the target's directory
when it is a rooted path, else "".

- [ ] **Step 1: Failing tests** (real .lnk files under a `DeskWallTest-<guid>` folder inside the temp dir, not the desktop; the desktop is only touched by Task 6)

```csharp
using DeskWall.Core;
using DeskWall.Core.Resolve;
using DeskWall.Core.Shortcuts;
using Xunit;

public class ShortcutFilesTests
{
    private static string TempLnk() { var d = Path.Combine(Path.GetTempPath(), "deskwall-tests", "lnk-" + Guid.NewGuid().ToString("N")[..8]); Directory.CreateDirectory(d); return Path.Combine(d, "\u00A0.lnk"); }

    [Theory]
    [InlineData("steam://rungameid/620", "steam://rungameid/620", "", "")]
    [InlineData("\"C:\\Program Files\\X\\x.exe\" --start abc", "C:\\Program Files\\X\\x.exe", "--start abc", "C:\\Program Files\\X")]
    [InlineData("explorer.exe D:\\", "explorer.exe", "D:\\", "")]
    public void SpecFor_Splits_Program_And_Arguments(string target, string prog, string args, string wd)
    {
        var spec = ShortcutFiles.SpecFor(new ResolvedShortcut("s", new Rect(0, 0, 1, 1), 0, target, "Play it", 0), @"C:\blank.ico");
        Assert.Equal(prog, spec.Target);
        Assert.Equal(args, spec.Arguments);
        Assert.Equal(wd, spec.WorkingDirectory);
        Assert.Equal("Play it", spec.Description);
        Assert.Equal(@"C:\blank.ico", spec.IconPath);
    }

    [Fact]
    public void Write_Read_Delete_RoundTrip()
    {
        var lnk = TempLnk();
        var ico = BlankIcon.Ensure();
        var spec = new ShortcutSpec(@"C:\Windows\explorer.exe", @"D:\", @"C:\Windows", "Open D", ico);
        ShortcutFiles.Write(lnk, spec);
        Assert.True(File.Exists(lnk));
        var back = ShortcutFiles.Read(lnk)!;
        Assert.Equal(spec.Target, back.Target, ignoreCase: true);
        Assert.Equal(spec.Arguments, back.Arguments);
        Assert.Equal(spec.Description, back.Description);
        Assert.Equal(ico, back.IconPath, ignoreCase: true);
        ShortcutFiles.Write(lnk, spec with { Description = "Changed" });
        Assert.Equal("Changed", ShortcutFiles.Read(lnk)!.Description);
        ShortcutFiles.Delete(lnk);
        Assert.False(File.Exists(lnk));
        Assert.Null(ShortcutFiles.Read(lnk));
    }
}
```

- [ ] **Step 2: Run, expect compile failure.**
- [ ] **Step 3: Implement** (`IShellLinkW` methods: `SetPath`, `SetArguments`, `SetWorkingDirectory`, `SetDescription`, `SetIconLocation(path, 0)`, `SetShowCmd(SW_SHOWNORMAL)`; then `QueryInterface` to `IPersistFile` and `Save(path, true)`. Read: `CoCreateInstance(ShellLink)`, QI `IPersistFile`, `Load(path, STGM_READ)`, then `GetPath(buf, MAX_PATH, null, 0)`, `GetArguments`, `GetWorkingDirectory`, `GetDescription`, `GetIconLocation`. All void-and-throw per the CsWin32 shapes; `QueryInterface` returns `HRESULT`. Fixed-size `char` buffers of 1024 for `GetPath` (`SLGP_RAWPATH`) and 260 for the rest. Release every pointer in `finally`.)

Splitting `Target` in `SpecFor`:

```csharp
public static ShortcutSpec SpecFor(ResolvedShortcut s, string iconPath)
{
    var t = s.Target.Trim();
    string prog, args;
    if (Uri.TryCreate(t, UriKind.Absolute, out var uri) && uri.Scheme.Length > 1 && !uri.IsFile) { prog = t; args = ""; }
    else if (t.StartsWith('"')) { var end = t.IndexOf('"', 1); prog = end < 0 ? t.Trim('"') : t[1..end]; args = end < 0 ? "" : t[(end + 1)..].Trim(); }
    else { var sp = t.IndexOf(' '); prog = sp < 0 ? t : t[..sp]; args = sp < 0 ? "" : t[(sp + 1)..].Trim(); }
    var wd = Path.IsPathRooted(prog) && !prog.Contains("://") ? (Path.GetDirectoryName(prog) ?? "") : "";
    return new ShortcutSpec(prog, args, wd, s.Tooltip, iconPath);
}
```

(`Uri.TryCreate("C:\\x\\y.exe")` yields a `file` URI, hence the `!uri.IsFile` guard; `explorer.exe D:\`
is not an absolute URI.)

- [ ] **Step 4: Run tests, expect pass** (4).
- [ ] **Step 5: Commit** `git commit -m "ShortcutFiles: .lnk write/read/delete via IShellLinkW; target splitting"`

Lane `lane/p3-files` complete.

---

### Task 4: `DesktopView` and `DesktopFlags` (lane `lane/p3-view`, Opus)

**Files:**
- Create: `src/DeskWall.Core/Shortcuts/DesktopView.cs`, `src/DeskWall.Core/Shortcuts/DesktopFlags.cs`
- Modify: `src/DeskWall.Core/NativeMethods.txt` (append the names in Step 1)
- Test: `tests/DeskWall.Core.Tests/Shortcuts/DesktopViewTests.cs` (real desktop, test-named files, cleaned up)

**Interfaces:**
- Produces:

```csharp
/// <summary>The desktop's IFolderView2, reached the way poc/DeskIcons.cs does: ShellWindows.FindWindowSW(CSIDL_DESKTOP)
/// -> IServiceProvider.QueryService(SID_STopLevelBrowser, IShellBrowser) -> QueryActiveShellView -> IFolderView2.
/// Every call re-acquires the view (Explorer restarts invalidate it) and releases everything before returning.</summary>
public static class DesktopView
{
    public static bool IsAvailable();                                        // false when Explorer is not running or icons are hidden (HideIcons folder flag)
    public static (int X, int Y)? GetPosition(string desktopFilePath);       // null when the item is not on the desktop
    public static void Position(IReadOnlyList<(string Path, int X, int Y)> items);   // SelectAndPositionItems with SVSI_POSITIONITEM; items missing from the view throw FileNotFoundException naming the path
    public static (int X, int Y) Spacing();                                  // GetSpacing: the icon grid cell (76x98 at 48 px icons)
    public static int IconSize();                                            // IFolderView2.GetViewModeAndIconSize -> pixels (48 for medium)
    public static FOLDERFLAGS Flags();                                       // GetCurrentFolderFlags
    public static void SetFlags(FOLDERFLAGS mask, FOLDERFLAGS value);        // SetCurrentFolderFlags
}

/// <summary>Enforces spec 7 preconditions and remembers what to put back.</summary>
public static class DesktopFlags
{
    /// <summary>Turn off FWF_AUTOARRANGE and FWF_SNAPTOGRID if on. Saves the original values to runtime/desktop-flags.json the FIRST time only (so repeated ticks do not overwrite the genuine original).</summary>
    public static void EnsurePlacementAllowed();
    /// <summary>Restore the saved flags and delete the file. No-op when there is nothing saved.</summary>
    public static void Restore();
}
```

CsWin32 gives `IFolderView2` (it derives from `IFolderView`), `IShellBrowser`, `IServiceProvider`
(`Windows.Win32.System.Com.IServiceProvider`), `IShellWindows` + `ShellWindows` coclass,
`SHParseDisplayName`, `ILFindLastID`, `ILFree`, `FOLDERFLAGS`, `_SVSIF` (the `SVSI_POSITIONITEM`
flag), `SID_STopLevelBrowser`. `FindWindowSW` takes `VARIANT*` for the location and root; pass
`VT_I4 = CSIDL_DESKTOP (0)` and `VT_EMPTY` as the POC does through `InvokeMember`; with CsWin32 you
call `IShellWindows.FindWindowSW(&loc, &root, ShellWindowTypeConstants.SWC_DESKTOP, &hwnd, ShellWindowFindWindowOptions.SWFO_NEEDDISPATCH, &dispatch)` directly, no reflection.

- [ ] **Step 1: NativeMethods.txt additions**

```
IShellWindows
ShellWindows
ShellWindowTypeConstants
ShellWindowFindWindowOptions
IServiceProvider
IShellBrowser
IShellView
IFolderView
IFolderView2
FOLDERFLAGS
_SVSIF
SID_STopLevelBrowser
SHParseDisplayName
ILFindLastID
ILFree
ITEMIDLIST
POINT
IDispatch
```

- [ ] **Step 2: Failing tests**

```csharp
using DeskWall.Core.Shortcuts;
using Windows.Win32.UI.Shell;
using Xunit;

public class DesktopViewTests
{
    private static string TestLnk() => Path.Combine(ShortcutFiles.DesktopDir(), $"DeskWallTest-{Guid.NewGuid():N}.lnk");

    [Fact]
    public void View_Is_Available_And_Reports_Spacing_And_IconSize()
    {
        Assert.True(DesktopView.IsAvailable());
        var (sx, sy) = DesktopView.Spacing();
        Assert.InRange(sx, 40, 400); Assert.InRange(sy, 40, 400);
        Assert.Contains(DesktopView.IconSize(), new[] { 16, 32, 48, 96, 256 });
    }

    [Fact]
    public void Position_Then_GetPosition_RoundTrips_For_A_Test_Shortcut()
    {
        var lnk = TestLnk();
        try
        {
            ShortcutFiles.Write(lnk, new ShortcutSpec(@"C:\Windows\explorer.exe", "", @"C:\Windows", "DeskWall test", BlankIcon.Ensure()));
            DesktopFlags.EnsurePlacementAllowed();
            Thread.Sleep(1500);                                        // Explorer notices the new file asynchronously
            DesktopView.Position([(lnk, 700, 300)]);
            Thread.Sleep(500);
            var got = DesktopView.GetPosition(lnk);
            Assert.NotNull(got);
            Assert.InRange(got!.Value.X, 690, 710);
            Assert.InRange(got.Value.Y, 290, 310);
        }
        finally { if (File.Exists(lnk)) File.Delete(lnk); }
    }

    [Fact]
    public void GetPosition_Of_A_Missing_Item_Is_Null_And_Position_Throws()
    {
        var lnk = Path.Combine(ShortcutFiles.DesktopDir(), "DeskWallTest-missing.lnk");
        Assert.Null(DesktopView.GetPosition(lnk));
        Assert.Throws<FileNotFoundException>(() => DesktopView.Position([(lnk, 0, 0)]));
    }

    [Fact]
    public void Flags_Round_Trip_And_Restore()
    {
        var before = DesktopView.Flags();
        try
        {
            DesktopFlags.EnsurePlacementAllowed();
            var during = DesktopView.Flags();
            Assert.False(during.HasFlag(FOLDERFLAGS.FWF_AUTOARRANGE));
            Assert.False(during.HasFlag(FOLDERFLAGS.FWF_SNAPTOGRID));
            Assert.True(File.Exists(DeskWall.Core.Paths.InRuntime("desktop-flags.json")));
        }
        finally
        {
            DesktopFlags.Restore();
            Assert.Equal(before & (FOLDERFLAGS.FWF_AUTOARRANGE | FOLDERFLAGS.FWF_SNAPTOGRID), DesktopView.Flags() & (FOLDERFLAGS.FWF_AUTOARRANGE | FOLDERFLAGS.FWF_SNAPTOGRID));
        }
    }
}
```

(On JOES-PC both flags are already off, so `Restore` is a no-op there; the test still proves
the save/restore path does not corrupt them. The `Position_Then_GetPosition` tolerance covers
the shell's rounding to whole pixels; it must NOT be widened to the grid size, that would hide
snap-to-grid being on.)

- [ ] **Step 3: Implement**

`DesktopView.cs` structure (fill in with the generated shapes):

```csharp
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.System.Variant;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.Shell.Common;

namespace DeskWall.Core.Shortcuts;

public static unsafe class DesktopView
{
    private static IFolderView2* Acquire()
    {
        Com.EnsureInitialized();
        IShellWindows* windows; var clsid = typeof(ShellWindows).GUID; var iid = typeof(IShellWindows).GUID;
        PInvoke.CoCreateInstance(&clsid, null, CLSCTX.CLSCTX_LOCAL_SERVER, &iid, (void**)&windows).ThrowOnFailure();
        try
        {
            var loc = new VARIANT(); loc.Anonymous.Anonymous.vt = VARENUM.VT_I4; loc.Anonymous.Anonymous.Anonymous.lVal = 0;   // CSIDL_DESKTOP
            var root = new VARIANT(); root.Anonymous.Anonymous.vt = VARENUM.VT_EMPTY;
            int hwnd; IDispatch* disp;
            windows->FindWindowSW(&loc, &root, (int)ShellWindowTypeConstants.SWC_DESKTOP, &hwnd, (int)ShellWindowFindWindowOptions.SWFO_NEEDDISPATCH, &disp);
            if (disp is null) throw new InvalidOperationException("desktop shell window not found (is Explorer running?)");
            try
            {
                IServiceProvider* sp; var iidSp = typeof(IServiceProvider).GUID;
                ((IUnknown*)disp)->QueryInterface(&iidSp, (void**)&sp).ThrowOnFailure();
                try
                {
                    IShellBrowser* browser; var sid = PInvoke.SID_STopLevelBrowser; var iidSb = typeof(IShellBrowser).GUID;
                    sp->QueryService(&sid, &iidSb, (void**)&browser);
                    try
                    {
                        IShellView* view; browser->QueryActiveShellView(&view);
                        try
                        {
                            IFolderView2* fv; var iidFv = typeof(IFolderView2).GUID;
                            ((IUnknown*)view)->QueryInterface(&iidFv, (void**)&fv).ThrowOnFailure();
                            return fv;
                        }
                        finally { view->Release(); }
                    }
                    finally { browser->Release(); }
                }
                finally { sp->Release(); }
            }
            finally { disp->Release(); }
        }
        finally { windows->Release(); }
    }

    private static ITEMIDLIST* ChildPidl(string path)   // caller frees with ILFree
    {
        ITEMIDLIST* abs; uint attrs;
        fixed (char* p = path) PInvoke.SHParseDisplayName(p, null, &abs, 0, &attrs).ThrowOnFailure();
        // ILFindLastID returns a pointer INTO abs; ILClone it so the absolute pidl can be freed here
        var last = PInvoke.ILFindLastID(abs);
        var copy = PInvoke.ILClone(last);
        PInvoke.ILFree(abs);
        return copy;
    }

    public static bool IsAvailable()
    {
        try { var fv = Acquire(); try { return !Flags(fv).HasFlag(FOLDERFLAGS.FWF_NOICONS); } finally { fv->Release(); } }
        catch (Exception) { return false; }
    }

    public static (int X, int Y)? GetPosition(string desktopFilePath)
    {
        if (!File.Exists(desktopFilePath)) return null;
        var fv = Acquire();
        try
        {
            var pidl = ChildPidl(desktopFilePath);
            try { POINT pt; fv->GetItemPosition(pidl, &pt); return (pt.X, pt.Y); }
            catch (COMException) { return null; }
            finally { PInvoke.ILFree(pidl); }
        }
        finally { fv->Release(); }
    }

    public static void Position(IReadOnlyList<(string Path, int X, int Y)> items)
    {
        foreach (var it in items) if (!File.Exists(it.Path)) throw new FileNotFoundException("shortcut is not on the desktop", it.Path);
        var fv = Acquire();
        var pidls = new ITEMIDLIST*[items.Count];
        try
        {
            var pts = new POINT[items.Count];
            for (var i = 0; i < items.Count; i++) { pidls[i] = ChildPidl(items[i].Path); pts[i] = new POINT { X = items[i].X, Y = items[i].Y }; }
            fixed (ITEMIDLIST** pp = pidls) fixed (POINT* ppt = pts)
                fv->SelectAndPositionItems((uint)items.Count, pp, ppt, (uint)_SVSIF.SVSI_POSITIONITEM);
        }
        finally
        {
            foreach (var p in pidls) if (p is not null) PInvoke.ILFree(p);
            fv->Release();
        }
    }

    public static (int X, int Y) Spacing() { var fv = Acquire(); try { var pt = new POINT(); fv->GetSpacing(&pt); return (pt.X, pt.Y); } finally { fv->Release(); } }

    public static int IconSize() { var fv = Acquire(); try { FOLDERVIEWMODE mode; int size; fv->GetViewModeAndIconSize(&mode, &size); return size; } finally { fv->Release(); } }

    public static FOLDERFLAGS Flags() { var fv = Acquire(); try { return Flags(fv); } finally { fv->Release(); } }
    private static FOLDERFLAGS Flags(IFolderView2* fv) { uint f; fv->GetCurrentFolderFlags(&f); return (FOLDERFLAGS)f; }

    public static void SetFlags(FOLDERFLAGS mask, FOLDERFLAGS value) { var fv = Acquire(); try { fv->SetCurrentFolderFlags((uint)mask, (uint)value); } finally { fv->Release(); } }
}
```

Add `ILClone`, `FOLDERVIEWMODE` to `NativeMethods.txt`. `SelectAndPositionItems`'s `apidl`
parameter type in CsWin32 may be `ITEMIDLIST**` or `ITEMIDLIST*[]`-like; follow the generator.
`IShellWindows.FindWindowSW`'s integer parameters are `int` (they are `long` in IDL, which is
32-bit); the `hwnd` out-param is `int*`.

`DesktopFlags.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using Windows.Win32.UI.Shell;

namespace DeskWall.Core.Shortcuts;

public static class DesktopFlags
{
    private const FOLDERFLAGS Mask = FOLDERFLAGS.FWF_AUTOARRANGE | FOLDERFLAGS.FWF_SNAPTOGRID;
    private static string FilePath => Paths.InRuntime("desktop-flags.json");

    public static void EnsurePlacementAllowed()
    {
        var current = DesktopView.Flags();
        if ((current & Mask) == 0) return;
        if (!File.Exists(FilePath))
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new SavedFlags { Original = (uint)(current & Mask) }, DesktopFlagsJsonContext.Default.SavedFlags));
        DesktopView.SetFlags(Mask, 0);
    }

    public static void Restore()
    {
        if (!File.Exists(FilePath)) return;
        var saved = JsonSerializer.Deserialize(File.ReadAllText(FilePath), DesktopFlagsJsonContext.Default.SavedFlags);
        if (saved is not null) DesktopView.SetFlags(Mask, (FOLDERFLAGS)saved.Original);
        File.Delete(FilePath);
    }

    public sealed class SavedFlags { public uint Original { get; set; } }
}

[JsonSerializable(typeof(DesktopFlags.SavedFlags))]
internal partial class DesktopFlagsJsonContext : JsonSerializerContext;
```

- [ ] **Step 4: Run tests, expect pass** (4, on the real desktop; nothing left behind).
- [ ] **Step 5: Commit** `git commit -m "DesktopView: IFolderView2 positions, spacing, icon size, folder flags; DesktopFlags save/restore"`

---

### Task 5: `Calibrator` (`deskwall calibrate`) (lane `lane/p3-view`, Opus)

**Files:**
- Create: `src/DeskWall.Core/Shortcuts/Calibrator.cs`, `src/DeskWall.Core/Shortcuts/Screenshot.cs`
- Modify: `src/DeskWall.Core/NativeMethods.txt` (`GetDC`, `ReleaseDC`, `CreateCompatibleDC`, `CreateCompatibleBitmap`, `SelectObject`, `BitBlt`, `GetDIBits`, `DeleteObject`, `DeleteDC`, `BITMAPINFO`, `BITMAPINFOHEADER`, `ROP_CODE`, `DIB_USAGE`)
- Test: `tests/DeskWall.Core.Tests/Shortcuts/CalibratorTests.cs` (pure diff logic on synthetic surfaces; the live run is Step 4)

**Interfaces:**
- Produces:

```csharp
/// <summary>Capture the primary monitor (physical pixels) into a Surface via GDI BitBlt + GetDIBits (no GPU, no WinRT).</summary>
public static class Screenshot { public static Surface Capture(Rect physical); }

public sealed record CalibrationResult(int IconSize, int ScalePercent, ArrowRect Arrow, int ItemX, int ItemY, int Pixels);

public static class Calibrator
{
    /// <summary>Pure: given a screenshot and the flat-colour reference the wallpaper had at the probe rect, find the bounding box of pixels that differ by more than threshold in any channel. Returns null when nothing differs.</summary>
    public static Rect? DiffBounds(Surface shot, Surface reference, Rect probe, int threshold = 60);
    /// <summary>Live: paint a flat mid-grey probe (400x300) into the current wallpaper at a spot away from the layout,
    /// place one test shortcut there at a known item position, screenshot, diff, and derive
    /// Arrow = (bounds.X - itemX, bounds.Y - itemY, bounds.W). Stores the result in Calibration for (IconSize, ScalePercent). Cleans up the shortcut and restores the wallpaper file.</summary>
    public static CalibrationResult Run(MonitorInfo monitor, string currentWallpaperPath, Action<string>? log = null);
}
```

Live procedure (`Run`):
1. `DesktopFlags.EnsurePlacementAllowed()`; `size = DesktopView.IconSize()`; `scale = monitor.Signature.ScalePercent`.
2. Load the current wallpaper file into a `Surface`; fill `probe = Rect(monitor.Bounds.W/2 - 200, monitor.Bounds.H/2 - 150, 400, 300)` with `#FF808080`; save as `runtime/calibrate.jpg`; `WallpaperSetter.Set(monitor.WallpaperMonitorId, calibrate.jpg)`.
3. Write `DeskWallTest-calibrate.lnk` (blank icon), wait 1.5 s, `DesktopView.Position([(lnk, probe.X + 100, probe.Y + 100)])`, wait 0.7 s.
4. Minimise all windows (`Shell.Application.MinimizeAll` is what the POC did; here: `IShellDispatch` via CsWin32 `Shell` coclass, `MinimizeAll()` then `UndoMinimizeALL()` after), wait 0.8 s, `Screenshot.Capture(monitor.Bounds)`.
5. `bounds = DiffBounds(shot, wallpaperSurfaceWithProbe, probe.Inflate(-2))` (inset 2 px to dodge JPEG ringing, as `poc/verify.ps1` does). The arrow is the only non-transparent thing the blank-icon shortcut draws, so `bounds` is the arrow: `Arrow = (bounds.X - itemX, bounds.Y - itemY, max(bounds.W, bounds.H))`. Sanity: `Size` in 8..32 else throw with the numbers.
6. Restore: `WallpaperSetter.Set(original)`, delete the test shortcut, `UndoMinimizeALL`.
7. `Calibration.Load().Set(size, scale, arrow).Save()`; return the result.

- [ ] **Step 1: Failing tests** (pure part)

```csharp
using DeskWall.Core;
using DeskWall.Core.Render;
using DeskWall.Core.Shortcuts;
using Xunit;

public class CalibratorTests
{
    [Fact]
    public void DiffBounds_Finds_The_Arrow_Square()
    {
        using var reference = Surface.Create(400, 300);
        reference.Clear(new Color(255, 128, 128, 128));
        using var shot = Surface.Create(400, 300);
        shot.Clear(new Color(255, 128, 128, 128));
        shot.FillRect(new Rect(105, 140, 13, 13), new Color(255, 255, 255, 255));   // the arrow overlay
        shot.FillRect(new Rect(300, 20, 1, 1), new Color(255, 160, 128, 128));       // JPEG-noise-sized blip under threshold
        var b = Calibrator.DiffBounds(shot, reference, new Rect(0, 0, 400, 300));
        Assert.Equal(new Rect(105, 140, 13, 13), b);
    }

    [Fact]
    public void DiffBounds_Null_When_Identical()
    {
        using var a = Surface.Create(20, 20); a.Clear(Color.White);
        using var b = Surface.Create(20, 20); b.Clear(Color.White);
        Assert.Null(Calibrator.DiffBounds(a, b, new Rect(0, 0, 20, 20)));
    }

    [Fact]
    public void Screenshot_Captures_Primary_Monitor_Size()
    {
        var m = DeskWall.Core.Display.Monitors.Enumerate().First(x => x.IsPrimary);
        using var s = Screenshot.Capture(m.Bounds);
        Assert.Equal((m.Bounds.W, m.Bounds.H), (s.Width, s.Height));
    }
}
```

- [ ] **Step 2: Run, expect compile failure.**
- [ ] **Step 3: Implement**

`Screenshot.Capture`: `GetDC(HWND.Null)`, `CreateCompatibleDC`, `CreateCompatibleBitmap(w,h)`,
`SelectObject`, `BitBlt(mem, 0, 0, w, h, screen, x, y, SRCCOPY | CAPTUREBLT)`, then `GetDIBits` with a
`BITMAPINFOHEADER { biBitCount = 32, biCompression = BI_RGB, biHeight = -h }` (top-down) into a
`byte[w*h*4]`, and copy the rows into `Surface.Create(w,h)` through its raw path. Since GDI gives
straight BGRA with alpha 0 or 255, set alpha to 255 for every pixel before writing so premultiplied
storage is correct. Add an `internal static Surface FromBgra(int w, int h, ReadOnlySpan<byte> bgra)`
to `Surface` (one lock, one copy) for this; it is the only new Surface member.

`DiffBounds`: iterate `probe` (clipped to both surfaces); compare channels of `GetPixel` on both;
for speed use a single `Lock` per surface via a new `internal void ReadRegion(Rect r, byte[] dst)`
on `Surface` (again one lock, one copy), then compare in managed memory. Threshold on any of R, G, B.
Return the bounding box or null.

`Run`: as the procedure above. `IShellDispatch` for MinimizeAll: CsWin32 names `Shell` (coclass) and
`IShellDispatch`; add both to `NativeMethods.txt`. If the generated interface turns out unusable
under `allowMarshaling: false`, the fallback is `PostMessage(FindWindow("Shell_TrayWnd"), WM_COMMAND, 419 /*MIN_ALL*/, 0)` and `416 /*MIN_ALL_UNDO*/`, which is what Explorer's own hotkey uses.

- [ ] **Step 4: Live run and record**

`dotnet run --project src/DeskWall.Daemon -- calibrate` (Task 6 adds the command; for this lane a
temporary `calibrate-test` case in `Program.cs` is fine). Expected on JOES-PC at 3440x1440 100
percent, 48 px icons: `Arrow = (0, 40, 13)`, matching the POC. Paste the printed result and the
`calibration.json` content into the report. If the machine is on RDP at another resolution when you
run it, the result is for THAT signature; say so.

- [ ] **Step 5: Commit** `git commit -m "Calibrator: measure the shortcut-arrow offset with a probe shortcut and a screenshot diff"`

Lane `lane/p3-view` complete.

---

### Task 6: `ShortcutManager`, tick stage 7, commands (controller)

**Files:**
- Create: `src/DeskWall.Core/Shortcuts/ShortcutManager.cs`
- Modify: `src/DeskWall.Core/Tick/TickRunner.cs`, `src/DeskWall.Daemon/Program.cs`, `src/DeskWall.Daemon/DaemonLoop.cs` (Phase 2: uninstall calls `ShortcutManager.RemoveAll()` and `DesktopFlags.Restore()`)
- Test: `tests/DeskWall.Core.Tests/Shortcuts/ShortcutManagerTests.cs`

**Interfaces:**
- Produces:

```csharp
public sealed record ShortcutOutcome(int Written, int Positioned, int Removed, IReadOnlyList<string> Warnings);

public sealed class ShortcutManager(Calibration calibration, int pad = ShortcutPlan.DefaultPad, Func<string>? desktopDir = null)
{
    /// <summary>Make the desktop match: write/update slot files, delete slots not in the list, position all,
    /// verify with GetPosition and retry up to 3 times with 150 ms between. Never throws for a single bad slot;
    /// collects Warnings. Throws only when the desktop view is unavailable (caller logs and moves on).</summary>
    public ShortcutOutcome Reconcile(IReadOnlyList<ResolvedShortcut> shortcuts, int scalePercent);
    /// <summary>Delete every slot file on the desktop (uninstall).</summary>
    public int RemoveAll();
    /// <summary>The state we last wrote, so the tick can skip Reconcile when nothing changed.</summary>
    public string Fingerprint(IReadOnlyList<ResolvedShortcut> shortcuts, int scalePercent);   // slots+rects+targets+tooltips+arrow
}
```

`Reconcile` steps: `DesktopFlags.EnsurePlacementAllowed()`; `size = DesktopView.IconSize()`;
`arrow = calibration.Get(size, scale) ?? calibration.Get(48, 100)!` with a warning when it fell back;
`ico = BlankIcon.Ensure()`; for each ordered shortcut: path = desktop + `SlotFileName(slot)`;
`SpecFor`; write if `Read(path)` differs or is missing; compute `IconPosition(rect, arrow, pad)`;
delete any `*.lnk` on the desktop whose `SlotFromFileName` is not null and not in the current slot
set; `Position(all)`; verify each with `GetPosition`, re-`Position` the misses, up to 3 rounds.

`TickRunner` stage 7: `if (shortcuts is not null && (force || fingerprint changed)) outcome = shortcuts.Reconcile(...)`,
timed into `ShortcutsMs`; fingerprint persisted in `FrameState.ShortcutsFingerprint`. The
`ShortcutManager` is an optional ctor parameter (null in unit tests that must not touch the desktop).

Commands: `deskwall calibrate` (runs `Calibrator.Run` on the primary monitor and prints the result),
`deskwall shortcuts` (prints slot -> target -> position for the current layout, and `VERIFY OK` /
`OFF BY (dx,dy)` per slot comparing `GetPosition` to the planned position). `uninstall` (Phase 2)
adds `RemoveAll()` and `DesktopFlags.Restore()`.

- [ ] **Step 1: Failing tests** (real desktop, test slot range: use `desktopDir` pointing at a temp folder for the file logic tests; only one test touches the real desktop and it uses slots 60..63 which no layout uses, deleting them in `finally`)

```csharp
using DeskWall.Core;
using DeskWall.Core.Resolve;
using DeskWall.Core.Shortcuts;
using Xunit;

public class ShortcutManagerTests
{
    private static ResolvedShortcut S(int slot, int y, string target = "steam://rungameid/620") => new($"s{slot}", new Rect(3220, y, 172, 258), 1, target, $"Play {slot}", slot);

    [Fact]
    public void Fingerprint_Changes_With_Rect_Target_Tooltip_Slot_Or_Scale()
    {
        var m = new ShortcutManager(Calibration.Seed());
        var a = m.Fingerprint([S(0, 142)], 100);
        Assert.Equal(a, m.Fingerprint([S(0, 142)], 100));
        Assert.NotEqual(a, m.Fingerprint([S(0, 143)], 100));
        Assert.NotEqual(a, m.Fingerprint([S(0, 142, "steam://rungameid/730")], 100));
        Assert.NotEqual(a, m.Fingerprint([S(1, 142)], 100));
        Assert.NotEqual(a, m.Fingerprint([S(0, 142)], 125));
    }

    [Fact]
    public void Reconcile_On_The_Real_Desktop_Places_Two_Slots_And_Removes_A_Stale_One()
    {
        var desktop = ShortcutFiles.DesktopDir();
        var slots = new[] { 60, 61, 62 };
        try
        {
            var m = new ShortcutManager(Calibration.Load());
            var first = m.Reconcile([S(60, 142), S(61, 414), S(62, 686)], 100);
            Assert.Equal(3, first.Written);
            Assert.Equal(3, first.Positioned);
            var second = m.Reconcile([S(60, 142), S(61, 414)], 100);
            Assert.Equal(1, second.Removed);
            Assert.False(File.Exists(Path.Combine(desktop, ShortcutPlan.SlotFileName(62))));
            var arrow = Calibration.Load().Get(DesktopView.IconSize(), 100) ?? Calibration.Seed().Get(48, 100)!;
            var want = ShortcutPlan.IconPosition(new Rect(3220, 414, 172, 258), arrow);
            var got = DesktopView.GetPosition(Path.Combine(desktop, ShortcutPlan.SlotFileName(61)))!.Value;
            Assert.Equal(want, got);
        }
        finally
        {
            foreach (var s in slots) { var p = Path.Combine(desktop, ShortcutPlan.SlotFileName(s)); if (File.Exists(p)) File.Delete(p); }
        }
    }
}
```

The real-desktop test positions at x=3220 which is off-screen on a 1920-wide RDP session; the
shell still stores the position, and `GetPosition` returns what was set, so the assertion holds
regardless of the current resolution. If it does not on RDP, the test records that in the report
and the assertion is changed to `InRange` on the y coordinate only, with the reason.

- [ ] **Step 2: Implement; `dotnet test` green; commit** `git commit -m "ShortcutManager: reconcile slot files and positions; tick stage 7; calibrate and shortcuts commands"`

---

### Task 7: Live verification and Phase 3 exit (controller)

- [ ] **Step 1:** With the POC task disabled and `layouts/steam-recent.json` (Phase 4) or a local-cover
  test layout active: `deskwall calibrate` then `deskwall tick --force --measure` then `deskwall shortcuts`.
  Expected: four labelless shortcuts over the covers; `shortcuts` prints `VERIFY OK` for each; hover shows
  `Play <name>`; click launches Steam. `poc/verify.ps1`'s pixel-diff approach becomes `deskwall verify`
  in Phase 6, so for now the check is `deskwall shortcuts` plus a screenshot saved to the runtime dir.
- [ ] **Step 2:** Resolution change: connect over RDP at 1920x1200 (or change the display mode), wait for
  the Phase 2 daemon's display-change tick; `deskwall shortcuts` shows `VERIFY OK` against the scaled layout.
- [ ] **Step 3:** `deskwall uninstall` removes all slot files and restores folder flags; the POC's
  `shortcuts.ps1` still works afterwards (the two use different slot ranges only by accident: both use
  `\u00A0` names, so the POC and v1 must not both own the desktop; document that in `poc/README` and the
  ledger). Re-enable the POC task only after its shortcuts are re-placed.
- [ ] **Step 4:** Phase 3 exit criteria: tests green; calibrate result recorded for the live signature;
  `deskwall shortcuts` VERIFY OK on all slots; uninstall leaves the desktop as found.

## Self-review notes

- Spec 7 bullets: inputs (Task 6), slot files and ICO (Tasks 2, 3), placement with verify and retry
  (Tasks 4, 6), calibration (Tasks 1, 5), preconditions enforced and saved (Task 4), display change
  reconciliation (Phase 2 wake + Task 6 fingerprint), uninstall (Task 6 + Phase 2), tooltip as description (Task 3).
- Not in v1: global arrow removal (documented, admin).
- POC constants carried as the calibration seed so nothing regresses on JOES-PC even before `calibrate` runs.
