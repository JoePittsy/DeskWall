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
