# DeskWall v1 Phase 5: Designer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `DeskWall.Designer.exe`, a WPF app that edits layout files on a live preview rendered by
the same Core renderer the daemon uses, with source and property panels, a binding picker, a
z-order list, a display selector, and a settings page. Apply is save; the daemon hot-reloads.

**Architecture:** MVVM without a framework: `DesignerModel` (the open `LayoutFile`, the current
`DisplaySignature`, selection, undo stack) is a plain class with change events; views bind to
thin view-model wrappers. The canvas is a WPF `Canvas` showing a scaled `WriteableBitmap` of the
Core render plus transparent hit-test adorners per component. All rendering goes through
`DeskWall.Core` (`LayoutResolver` + `FrameRenderer` on a `Surface`), so the preview is the output.
Sources run for real inside the designer through the same `SourceFactory` so the panels show
live values.

**Tech Stack:** WPF on .NET 10 (`UseWPF`, JIT, not AOT), `DeskWall.Core` project reference,
xUnit for model tests. No third-party UI packages.

**Spec:** `docs/superpowers/specs/2026-09-20-deskwall-v1-design.md` (section 8, and the design
doctrine gates in section 1)
**Master plan:** `docs/superpowers/plans/2026-09-20-deskwall-v1-master.md`
**Depends on:** Phases 2, 3, 4 merged into `v1` (LayoutStore, Footprint, RollingLog, Startup,
ShortcutManager, all sources, RemoteImageCache, Secrets).

## Global Constraints

- Everything in the master plan's Global Constraints.
- **Design doctrine gates apply to every screen.** Before any XAML is written for a screen, the
  implementer states the screen's job in one sentence and lists what it deliberately leaves out.
  No section headers that repeat the panel's name, no icon-per-button decoration, no gradients,
  no card-in-card nesting. System fonts, system colours (`SystemColors` and the Windows accent),
  light and dark follow the OS. If it looks like every other generated settings page, it fails.
- **The designer never talks to the daemon.** Files only: the layout store and layout files
  (the daemon watches them), `secrets.json`, `settings.json`. "Apply" is `LayoutFile.Save`.
  The footprint panel reads the daemon's numbers from the process list (`Process.GetProcessesByName("deskwall")`)
  and the log; it never opens a channel.
- **Only Core renders.** No WPF drawing of components; the preview bitmap comes from
  `FrameRenderer`. WPF draws selection handles, guides and the hit-test layer only.
- **Undo is whole-model snapshots** (the layout JSON string), capped at 100 entries. Simple and
  correct beats a command pattern here.
- Physical pixels everywhere in the model; the canvas view applies one zoom factor.
- ASCII-only sources. Tests under `DESKWALL_HOME`. Model tests need no WPF; view tests are manual
  checklists in the task, with screenshots saved to the runtime dir for the report.
- Lane assignment (cap stated in the ledger before dispatch): Task 1 seam (controller);
  `lane/p5-canvas` (Opus: Tasks 2, 3), `lane/p5-panels` (Sonnet: Tasks 4, 5),
  `lane/p5-settings` (Sonnet: Tasks 6, 7); Tasks 8, 9 controller.

## Interfaces consumed

```csharp
// Core (Phases 1-4)
LayoutFile { BaseImage, BaseFit, Encode, JpegQuality, Sources, Components; Parse/Load/ToJson/Save }
ComponentDef { Id, Rect, Z } : TextDef | ImageDef | BarDef | ShortcutDef | RepeaterDef { Template, Axis, Gap, CellHeight }
PropertyValue { LiteralText, Binding, IsBound; Literal(...), Bound(Binding) }
Binding.Parse(text); binding.ToString()
SourceDef { Name, Type, EverySeconds, Settings }
SourceFactory.Create(SourceDef, IClock, Secrets); ISource; SourceRegistry; SourceSnapshot
LayoutResolver.Resolve(layout, tree, canvas) : IReadOnlyList<Resolved>
FrameRenderer(w,h).RenderAll(baseRaw, resolved) : Surface;  BaseCache.Ensure(...)
Surface { Width, Height; SaveRaw/LoadRaw; internal ReadRegion(Rect, byte[]) (Phase 3 added) }
Monitors.Enumerate() : MonitorInfo(Signature, Bounds, IsPrimary, WallpaperMonitorId)
LayoutStore { Entries, Set, Remove, Resolve(sig) : LayoutResolution, WatchPaths }; LayoutScaler.Scale
Secrets(path) { Get, Substitute }  -- the designer edits the file directly as JSON
Footprint.Current(); RollingLog.Default() path; Startup.Install/Uninstall/Installed
ShortcutManager / Calibrator.Run / DesktopView.IsAvailable (Phase 3)
```

## File structure

```
src/DeskWall.Designer/DeskWall.Designer.csproj      WPF, net10.0-windows10.0.19041.0, UseWPF
src/DeskWall.Designer/App.xaml(.cs)                  startup: open store, pick monitor, first-run
src/DeskWall.Designer/Model/DesignerModel.cs          the document + selection + undo (Task 1)
src/DeskWall.Designer/Model/Snap.cs                   edge snapping maths (Task 1)
src/DeskWall.Designer/Model/PreviewRenderer.cs        Core render -> WriteableBitmap (Task 2)
src/DeskWall.Designer/Model/LiveSources.cs            runs the layout's sources for the panels (Task 4)
src/DeskWall.Designer/Model/Settings.cs               settings.json (tray, start at logon, last layout) (Task 6)
src/DeskWall.Designer/Views/MainWindow.xaml(.cs)      shell: canvas centre, panels around (Task 8)
src/DeskWall.Designer/Views/CanvasView.xaml(.cs)      preview + adorners + drag/resize/snap (Task 3)
src/DeskWall.Designer/Views/SourcesPanel.xaml(.cs)    (Task 4)
src/DeskWall.Designer/Views/PropertiesPanel.xaml(.cs) + BindingPicker.xaml (Task 5)
src/DeskWall.Designer/Views/LayersPanel.xaml(.cs)     z-order list (Task 5)
src/DeskWall.Designer/Views/SettingsPage.xaml(.cs)    (Task 6)
src/DeskWall.Designer/Views/FirstRun.xaml(.cs)        starter picker (Task 7)
src/DeskWall.Daemon/...                              reads settings.json for tray on/off (Task 8)
tests/DeskWall.Designer.Tests/                        model tests only (no WPF)
layouts/starter-*.json                               starters (Task 7)
```

---

### Task 1: Seam: `DesignerModel`, `Snap`, project scaffold (controller)

**Files:**
- Create: `src/DeskWall.Designer/DeskWall.Designer.csproj`, `src/DeskWall.Designer/App.xaml`, `App.xaml.cs` (empty window for now), `src/DeskWall.Designer/Model/DesignerModel.cs`, `src/DeskWall.Designer/Model/Snap.cs`, `tests/DeskWall.Designer.Tests/DeskWall.Designer.Tests.csproj`, `tests/DeskWall.Designer.Tests/DesignerModelTests.cs`, `tests/DeskWall.Designer.Tests/SnapTests.cs`
- Modify: `DeskWall.sln` (add both), `Directory.Build.props` (nothing; the WPF project overrides `IsAotCompatible` to false)

**Interfaces:**
- Produces:

```csharp
namespace DeskWall.Designer.Model;

/// <summary>The open document. All mutation goes through methods here so undo and change
/// notification are uniform. Not thread-affine; the views marshal to the UI thread.</summary>
public sealed class DesignerModel
{
    public DesignerModel(LayoutFile layout, DisplaySignature signature, string? path);
    public LayoutFile Layout { get; }                       // mutable; always mutate via Edit(...)
    public DisplaySignature Signature { get; }
    public string? Path { get; set; }                       // null = unsaved
    public bool Dirty { get; }
    public event Action? Changed;                           // after every Edit/Undo/Redo
    public event Action? SelectionChanged;

    public IReadOnlyList<string> Selection { get; }         // component ids (top-level or "rep[0].child" paths are NOT selectable: repeaters select as a whole)
    public void Select(IEnumerable<string> ids); public void ClearSelection();

    /// <summary>Snapshot, run the mutation, notify. The mutation gets the live LayoutFile.</summary>
    public void Edit(string label, Action<LayoutFile> mutate);
    public bool CanUndo { get; } public bool CanRedo { get; }
    public void Undo(); public void Redo();

    // Convenience edits used by the canvas and panels (each is an Edit with a label):
    public void Move(IEnumerable<string> ids, int dx, int dy);
    public void Resize(string id, Rect newRect);
    public void SetZ(string id, int z);  public void BringToFront(string id); public void SendToBack(string id);
    public void Add(ComponentDef def);   // ensures a unique Id (suffix -2, -3, ...)
    public void Remove(IEnumerable<string> ids);
    public void Align(IEnumerable<string> ids, AlignEdge edge);   // Left, Right, Top, Bottom, CenterX, CenterY

    public string ToJson();                                  // LayoutFile.ToJson()
    public void Save();                                      // Path required; LayoutFile.Save; Dirty = false
    public ComponentDef? Find(string id);
}

public enum AlignEdge { Left, Right, Top, Bottom, CenterX, CenterY }

/// <summary>Edge snapping: given a moving rect and the other rects plus the canvas, return the
/// adjusted rect and the guide lines that explain it.</summary>
public static class Snap
{
    public const int Threshold = 6;   // px at 100 percent zoom; the view divides by zoom
    public sealed record Guide(bool Vertical, int Position);
    public static (Rect Snapped, IReadOnlyList<Guide> Guides) Apply(Rect moving, IEnumerable<Rect> others, Rect canvas, int threshold = Threshold);
}
```

- [ ] **Step 1: Failing tests**

```csharp
using DeskWall.Core;
using DeskWall.Core.Display;
using DeskWall.Core.Layout;
using DeskWall.Designer.Model;
using Xunit;

public class DesignerModelTests
{
    private static DesignerModel Model() => new(LayoutFile.Parse("""
        { "version": 1, "baseImage": "x.jpg", "sources": [],
          "components": [
            { "type": "text", "id": "clock", "rect": [100, 100, 200, 50], "z": 1, "text": "x" },
            { "type": "bar", "id": "bar", "rect": [100, 200, 200, 6], "z": 2, "fraction": 0.5 } ] }
        """), new DisplaySignature("T", 1000, 800, 100), null);

    [Fact]
    public void Move_Is_Undoable_And_Notifies()
    {
        var m = Model(); var n = 0; m.Changed += () => n++;
        m.Move(["clock"], 10, -5);
        Assert.Equal(new Rect(110, 95, 200, 50), m.Find("clock")!.Rect);
        Assert.True(m.Dirty); Assert.True(m.CanUndo); Assert.Equal(1, n);
        m.Undo();
        Assert.Equal(new Rect(100, 100, 200, 50), m.Find("clock")!.Rect);
        Assert.True(m.CanRedo); Assert.Equal(2, n);
        m.Redo();
        Assert.Equal(new Rect(110, 95, 200, 50), m.Find("clock")!.Rect);
    }

    [Fact]
    public void Add_Makes_Ids_Unique_And_Remove_Clears_Selection()
    {
        var m = Model();
        m.Add(new TextDef { Id = "clock", Rect = new Rect(0, 0, 10, 10), Text = PropertyValue.Literal("y") });
        Assert.NotNull(m.Find("clock-2"));
        m.Select(["clock-2", "bar"]);
        m.Remove(["clock-2"]);
        Assert.Null(m.Find("clock-2"));
        Assert.Equal(["bar"], m.Selection);
    }

    [Fact]
    public void Z_Order_And_Align()
    {
        var m = Model();
        m.BringToFront("clock");
        Assert.True(m.Find("clock")!.Z > m.Find("bar")!.Z);
        m.Align(["clock", "bar"], AlignEdge.Right);
        Assert.Equal(m.Find("clock")!.Rect.Right, m.Find("bar")!.Rect.Right);
    }

    [Fact]
    public void Undo_Is_Capped_At_100()
    {
        var m = Model();
        for (var i = 0; i < 150; i++) m.Move(["clock"], 1, 0);
        var undone = 0; while (m.CanUndo) { m.Undo(); undone++; }
        Assert.Equal(100, undone);
        Assert.Equal(150, m.Find("clock")!.Rect.X);   // 100 + 50 moves that could not be undone
    }
}

public class SnapTests
{
    [Fact]
    public void Snaps_To_Neighbour_Edges_And_Canvas_Within_Threshold()
    {
        var canvas = new Rect(0, 0, 1000, 800);
        var other = new Rect(300, 100, 100, 100);
        var (r, g) = Snap.Apply(new Rect(404, 96, 50, 50), [other], canvas);
        Assert.Equal(400, r.X);                      // left edge to other's right edge
        Assert.Equal(100, r.Y);                      // top to other's top
        Assert.Contains(g, x => x.Vertical && x.Position == 400);
        Assert.Contains(g, x => !x.Vertical && x.Position == 100);
        var (r2, g2) = Snap.Apply(new Rect(3, 3, 50, 50), [], canvas);
        Assert.Equal(new Rect(0, 0, 50, 50), r2);
        Assert.Equal(2, g2.Count);
        var (r3, g3) = Snap.Apply(new Rect(500, 500, 50, 50), [other], canvas);
        Assert.Equal(new Rect(500, 500, 50, 50), r3);
        Assert.Empty(g3);
    }
}
```

- [ ] **Step 2: Projects**

`src/DeskWall.Designer/DeskWall.Designer.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <UseWPF>true</UseWPF>
    <AssemblyName>DeskWall.Designer</AssemblyName>
    <RootNamespace>DeskWall.Designer</RootNamespace>
    <IsAotCompatible>false</IsAotCompatible>
    <ApplicationManifest>app.manifest</ApplicationManifest>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\DeskWall.Core\DeskWall.Core.csproj" />
  </ItemGroup>
</Project>
```

(`app.manifest`: PerMonitorV2 DPI awareness, same as the daemon's.) The test project mirrors
`DeskWall.Core.Tests` but references the Designer project and sets `DESKWALL_HOME` in its own
`AssemblyInfo.cs`.

- [ ] **Step 3: Implement `DesignerModel` and `Snap`**

Undo: `_undo` and `_redo` are `Stack<string>` of `ToJson()`; `Edit` pushes the current JSON,
clears redo, mutates, raises `Changed`. `Undo` pushes current to redo, pops, `Layout = LayoutFile.Parse(...)`
(replace the instance; views re-read through `Find`). Cap: when `_undo.Count == 100`, drop the oldest
(use a `LinkedList<string>` or `List<string>` with `RemoveAt(0)`). `Dirty` = `_undo.Count > 0 || _redoSinceSave`;
simplest: a `_savedJson` string compared with `ToJson()`.

`Snap.Apply`: candidate x positions = canvas left/right, every other rect's left/right; for the
moving rect try left edge, right edge and centre against each candidate; pick the smallest delta
under threshold; same for y with top/bottom/centre. Guides are the matched candidate lines.

- [ ] **Step 4: `dotnet test` green (5). Commit** `git commit -m "Designer seam: project, DesignerModel with snapshot undo, Snap"`

Fan-out starts here.

---

### Task 2: `PreviewRenderer` (lane `lane/p5-canvas`, Opus)

**Files:**
- Create: `src/DeskWall.Designer/Model/PreviewRenderer.cs`
- Modify: `src/DeskWall.Core/Render/Surface.cs` (add `public void CopyTo(Span<byte> bgra)` if `ReadRegion` from Phase 3 is `internal`; make one public whole-frame read)
- Test: `tests/DeskWall.Designer.Tests/PreviewRendererTests.cs` (pure part: throttling and hit map)

**Interfaces:**
- Produces:

```csharp
/// <summary>Turns the model into pixels on a background thread and hands a WriteableBitmap to the
/// UI. Coalesces bursts: at most one render in flight, the latest request wins. Also produces the
/// hit map (component rects in canvas pixels) the canvas uses for picking.</summary>
public sealed class PreviewRenderer : IDisposable
{
    public PreviewRenderer(Func<RecordValue> valueTree);   // LiveSources.Tree (Task 4) or ValueTree.Empty
    public void Request(DesignerModel model);              // debounced 50 ms; safe from any thread
    public event Action<PreviewFrame>? Rendered;           // on the UI thread via Dispatcher
    public void Dispose();
}
public sealed record PreviewFrame(int Width, int Height, byte[] Bgra, IReadOnlyList<Resolved> Resolved, TimeSpan RenderTime);
```

The canvas turns `Bgra` into a `WriteableBitmap` (`PixelFormats.Bgra32`) with `WritePixels`.
Render path: `BaseCache.Ensure(model.Layout.BaseImage, w, h, fit)` then
`LayoutResolver.Resolve(model.Layout, valueTree(), canvas)` then `FrameRenderer.RenderAll`, then the
whole-frame read. Errors (bad base path, bad binding) produce a frame with the error text drawn by
Core (`Surface.DrawText` on a flat surface) so the user sees what is wrong where the preview is.

- [ ] **Step 1: Failing tests** (coalescing: 20 `Request` calls in 10 ms produce exactly one `Rendered`; a request during a render produces one more after; the hit map contains every top-level component's rect).
- [ ] **Step 2: Implement** with a `System.Threading.Timer` for the debounce, a `SemaphoreSlim(1)` and a `_pending` flag; `Rendered` is raised through `Dispatcher.CurrentDispatcher` captured at construction if one exists, else directly (tests).
- [ ] **Step 3: Tests green; commit** `git commit -m "Designer: PreviewRenderer, coalesced Core render to BGRA frames"`

---

### Task 3: `CanvasView` (lane `lane/p5-canvas`, Opus)

**Files:**
- Create: `src/DeskWall.Designer/Views/CanvasView.xaml`, `CanvasView.xaml.cs`, `src/DeskWall.Designer/Views/Adorner.cs`

**Behaviour (spec 8, first bullet):**
- Shows the preview bitmap scaled by `Zoom` (fit-to-window by default; Ctrl+wheel zooms 25 to 400
  percent around the cursor; space+drag or middle-drag pans). Physical pixel coordinates in the model,
  one transform in the view.
- Click selects the topmost component whose `Rect` contains the point (repeaters select as a whole,
  by their block rect). Shift-click adds. Drag on empty space is a marquee. Escape clears.
- Selected components get a 1 px accent outline and 8 resize handles (single selection only);
  dragging the body moves all selected, dragging a handle resizes (min 4x4). Both call the model
  once at mouse-up (live visual feedback is the adorner moving, not the model), so one undo entry
  per gesture.
- While dragging, `Snap.Apply` runs against all other top-level rects and the canvas; guides are
  drawn as 1 px dashed lines the length of the canvas.
- Arrow keys nudge the selection 1 px, Shift+arrow 10 px; Delete removes; Ctrl+Z / Ctrl+Y undo/redo;
  Ctrl+D duplicates (offset 16,16); Ctrl+] / Ctrl+[ bring forward/send back.
- Right-click menu: Bring to front, Send to back, Duplicate, Delete, Align (submenu, enabled with 2+ selected).
- Repeaters draw a faint outline around each expanded cell (from the `Resolved` list) with the
  first cell at full opacity, the rest at 40 percent, matching the spec.
- A status strip (bottom of the canvas, not a separate panel): cursor position in canvas pixels,
  selected rect, zoom, last render time. Nothing else.

- [ ] **Step 1: Doctrine statement** in the report: the job of this screen and what it leaves out.
- [ ] **Step 2: Implement.** Hit testing uses the `PreviewFrame.Resolved` rects, not WPF elements.
- [ ] **Step 3: Manual checklist**, each item ticked with a screenshot in `%DESKWALL_HOME%\designer-shots\`:
  select, multi-select, marquee, drag with snap guides visible, resize with handles, arrow nudges,
  undo/redo restores exactly, zoom and pan, repeater cell outlines, right-click menu.
- [ ] **Step 4: Commit** `git commit -m "Designer: canvas with selection, drag, resize, snap guides, keyboard and menu"`

Lane `lane/p5-canvas` complete.

---

### Task 4: `LiveSources` and `SourcesPanel` (lane `lane/p5-panels`, Sonnet)

**Files:**
- Create: `src/DeskWall.Designer/Model/LiveSources.cs`, `src/DeskWall.Designer/Views/SourcesPanel.xaml(.cs)`, `src/DeskWall.Designer/Views/SourceEditor.xaml(.cs)`
- Test: `tests/DeskWall.Designer.Tests/LiveSourcesTests.cs`

**Interfaces:**
- Produces:

```csharp
/// <summary>Runs the layout's sources inside the designer so panels show real values and the preview
/// renders real data. Re-created whenever the model's Sources list changes. Refreshes each source on
/// its own schedule with a single System.Threading.Timer; never more often than every 5 s.</summary>
public sealed class LiveSources : IDisposable
{
    public LiveSources(IReadOnlyList<SourceDef> defs, Secrets secrets, IClock clock);
    public RecordValue Tree();                                  // SourceRegistry.Tree()
    public IReadOnlyList<SourceSnapshot> Snapshots { get; }
    public event Action? Updated;                              // after any refresh (UI marshals)
    public Task RefreshNowAsync(string name);                  // the panel's "refresh" button
    public void Dispose();
}
```

`SourcesPanel` (left, spec 8): a list of the layout's sources, one row each: name, type, last
refresh age ("12 s ago"), and a red dot with the last error as tooltip when failed. Selecting a row
shows its value tree below as a two-column tree (path, value) which is also where the binding
picker (Task 5) gets its data. Buttons: Add source (type dropdown of the seven types), Remove,
Refresh now. `SourceEditor` is a small dialog: name, type (read-only after creation), every
(seconds), and the type-specific settings as a key/value grid pre-populated with the keys that type
knows (`url`, `header.*`, `path`, `command`, `args`, `timeout`, `parse`, `unixTimeFields`, `max`).
A setting value that contains `{secret:` shows the placeholder, never the value.

- [ ] Model tests: `Tree()` merges snapshots; `Updated` fires after `RefreshNowAsync`; a failing
  source keeps its last values and exposes `LastError`.
- [ ] Doctrine statement for the panel; manual checklist with screenshots: add `time`, see it
  refresh; add an `http` source with a bad URL, see the red dot and the error tooltip; remove it.
- [ ] Commit `git commit -m "Designer: LiveSources and SourcesPanel"`

---

### Task 5: `PropertiesPanel`, `BindingPicker`, `LayersPanel` (lane `lane/p5-panels`, Sonnet)

**Files:**
- Create: `src/DeskWall.Designer/Views/PropertiesPanel.xaml(.cs)`, `BindingPicker.xaml(.cs)`, `LayersPanel.xaml(.cs)`, `src/DeskWall.Designer/Model/PropertySchema.cs`
- Test: `tests/DeskWall.Designer.Tests/PropertySchemaTests.cs`

**Interfaces:**
- Produces:

```csharp
/// <summary>What the properties panel shows for each ComponentDef type, in order, with editor kind.
/// Explicit table, no reflection (the Core defs are plain classes; reflection would also be a trim hazard later).</summary>
public static class PropertySchema
{
    public enum Editor { Text, Number, Color, Enum, Font, Path, Binding }
    public sealed record Prop(string Name, Editor Editor, string[]? Choices, Func<ComponentDef, PropertyValue?> Get, Action<ComponentDef, PropertyValue> Set);
    public static IReadOnlyList<Prop> For(ComponentDef def);          // TextDef: Text, Font, Size, Weight, Color, Align, Effect, EffectRadius, EffectColor; ImageDef: Source, Fit, Radius, Opacity; BarDef: Fraction, Track, Fill, Threshold, ThresholdFill, Direction; ShortcutDef: Target, Tooltip, Slot(int); RepeaterDef: Items(binding only), Axis, Gap, CellHeight
    public static IReadOnlyList<(string Name, Func<ComponentDef,int> Get, Action<ComponentDef,int> Set)> Geometry;   // X, Y, W, H, Z
}
```

`PropertiesPanel` (right): geometry row (X Y W H Z as five numeric fields), then one row per
`Prop`: label, editor, and a "bind" toggle. When bound, the editor is replaced by the binding text
(`Binding.ToString()`) and a resolved-value preview in grey underneath ("14:32"); clicking it opens
`BindingPicker`. Edits go through `model.Edit("Set <prop>", ...)`. Colour editor: hex text plus a
swatch; Font editor: combo of installed families (`Fonts.SystemFontFamilies`); Enum: combo from
`Choices`; Path: text plus browse. Repeater template children are edited by selecting them in the
`LayersPanel` tree under their repeater (template children carry a `(template)` suffix).

`BindingPicker`: a popup with the source value tree (from `LiveSources.Tree()`), a path text box
that fills as you click nodes (`disks.drives[C].free`), a format text box, and a live preview of
`BindingResolver.ResolveText`. OK writes `PropertyValue.Bound(Binding.Parse(path + " | " + format))`.
Inside a repeater template the tree root is the first item of the bound list.

`LayersPanel` (bottom): z-ordered list, top first: id, type, rect; drag to reorder sets Z; selection
is two-way with the canvas; repeaters expand to show template children.

- [ ] `PropertySchemaTests`: every ComponentDef type returns a non-empty schema; `Set` then `Get`
  round-trips a literal and a binding for one prop per type; `Geometry` sets rect fields.
- [ ] Doctrine statement; manual checklist with screenshots: change the clock's size, bind a text to
  `disks.drives[C].freeGB` via the picker and see the preview, reorder layers, edit a repeater's
  template child.
- [ ] Commit `git commit -m "Designer: properties panel with binding picker, layers panel"`

Lane `lane/p5-panels` complete.

---

### Task 6: `Settings` and `SettingsPage` (lane `lane/p5-settings`, Sonnet)

**Files:**
- Create: `src/DeskWall.Designer/Model/Settings.cs`, `src/DeskWall.Designer/Views/SettingsPage.xaml(.cs)`, `src/DeskWall.Designer/Views/SecretsEditor.xaml(.cs)`
- Test: `tests/DeskWall.Designer.Tests/SettingsTests.cs`

**Interfaces:**
- Produces:

```csharp
/// <summary>runtime/settings.json, shared with the daemon (which reads TrayIcon at start).</summary>
public sealed class Settings
{
    public bool TrayIcon { get; set; } = true;
    public string? LastLayoutPath { get; set; }
    public string? LastSignatureKey { get; set; }
    public static Settings Load();  public void Save();      // source-generated JSON, atomic write
}
```

`SettingsPage` (spec 8 settings bullet), one page, sections in this order and nothing else:
1. **Daemon**: running / not running (from the process list), footprint line (working set, handles,
   threads, from `Process` for the daemon PID), last five log lines (from `deskwall.log`), buttons
   Start, Stop (posts `WM_CLOSE` to the `DeskWallHost` window via `FindWindow`), Refresh now
   (`deskwall tick`), Start at logon (checkbox bound to `Startup.Installed`), Tray icon (checkbox
   bound to `Settings.TrayIcon`; note "takes effect at next daemon start").
2. **Wallpaper**: base image path with Browse, fit, encode (jpeg/png), quality slider 60 to 100.
   These edit the current model, not settings.
3. **Desktop icons**: `DesktopView.IsAvailable()` status, current calibration line
   (`48@100 -> (0,40,13)`), button Calibrate now (runs `Calibrator.Run` with a progress line, shows
   the result), button Verify placement (runs the Phase 6 `deskwall verify` when it exists; until
   then `deskwall shortcuts` and shows its output).
4. **Secrets**: opens `SecretsEditor`: a key/value grid over `secrets.json` with values masked,
   reveal per row, add, remove, save. Never logs values.
5. **Layouts**: the store table (signature key -> layout path), Remove, and "Use this layout for
   the current display" (writes `LayoutStore.Set(current signature, model.Path)`).

- [ ] `SettingsTests`: Load default when missing; Save/Load round-trip; atomic tmp cleanup on failure.
- [ ] Doctrine statement; manual checklist with screenshots for each section.
- [ ] Commit `git commit -m "Designer: settings page, secrets editor, settings.json"`

---

### Task 7: First run and starter layouts (lane `lane/p5-settings`, Sonnet)

**Files:**
- Create: `src/DeskWall.Designer/Views/FirstRun.xaml(.cs)`, `layouts/starter-column.json`, `layouts/starter-clock.json`, `layouts/starter-blank.json`, `layouts/README.md` (extend)

`FirstRun` shows when the store has no layout for the current signature: three cards named by
what they are, one sentence each, a 320 px preview rendered by `PreviewRenderer` from the starter
scaled to the current signature via `LayoutScaler`, and a button "Use this". Choosing copies the
starter to `runtime/layouts/<signature-key-safe>.json`, registers it in the store, opens it in the
designer. A fourth option "Open an existing layout file". No wizard steps, no welcome text.

Starters: `starter-column.json` is `layouts/steam-recent.json` (Phase 4) with the Steam source's
secrets left as placeholders and a comment-free design; `starter-clock.json` is the clock only,
top right; `starter-blank.json` has the base image only. All three at 3440x1440 100 percent; the
scaler handles the rest. The base image for all three is the Spotlight asset path with a note in
`layouts/README.md` on choosing your own.

- [ ] Doctrine statement; manual checklist: empty store -> first run -> choose clock -> designer opens with it -> daemon picks it up within 2 s.
- [ ] Commit `git commit -m "Designer: first-run starter picker and starter layouts"`

Lane `lane/p5-settings` complete.

---

### Task 8: `MainWindow`, wiring, daemon settings read, `Designer.Open` (controller)

**Files:**
- Create: `src/DeskWall.Designer/Views/MainWindow.xaml(.cs)`
- Modify: `src/DeskWall.Designer/App.xaml.cs`, `src/DeskWall.Daemon/DaemonLoop.cs` (read `Settings.TrayIcon` from `settings.json`; `Designer.Open` launches `DeskWall.Designer.exe` beside the daemon or logs), `src/DeskWall.Daemon/Program.cs` (`run --no-tray` still wins over the setting)

Shell: canvas centre, `SourcesPanel` left (280 px), `PropertiesPanel` right (320 px), `LayersPanel`
bottom (160 px), a single toolbar row: layout file name (dirty marker), display selector (combo of
`Monitors.Enumerate()` signatures plus every store key, with "copy from..." when the chosen one has
no layout), Apply (Ctrl+S), Revert to last applied, Settings (opens `SettingsPage` as a page over the
canvas, Escape returns). Panels collapse with F-keys; layout of the shell persists in `settings.json`.

Startup: `App` loads `LayoutStore.Default()`, picks the primary monitor's signature, resolves; if
null shows `FirstRun`; else opens the resolved layout (if `Scaled`, the toolbar shows a banner
"scaled from <key>; Save as layout for this display" per spec 5). `LiveSources` starts; the
`PreviewRenderer` is fed `model.Changed` and `LiveSources.Updated`.

- [ ] Manual checklist: full round trip on JOES-PC: open, move the clock, Apply, wallpaper changes
  within 2 s while the daemon runs; Revert restores; display selector switch to the RDP signature
  shows the scaled banner; Settings toggles tray and the daemon honours it on restart.
- [ ] Commit `git commit -m "Designer: main window shell, startup, daemon reads tray setting"`

---

### Task 9: Phase 5 review and exit (controller)

- [ ] Doctrine pass on every screen against spec 1 gates: element budget per screen written down;
  anything that survives only because it "looks complete" is removed.
- [ ] Opus final review of the whole `src/DeskWall.Designer` tree (one agent), findings fixed by
  ONE fix wave.
- [ ] Exit criteria: all model tests green; the manual checklists of Tasks 3 to 8 done with
  screenshots in the runtime dir; the daemon still meets its idle budget while the designer is
  open and after it closes (designer is a separate process; verify no daemon regression);
  `settings.json`, `secrets.json`, `layouts.json` are the only files the designer writes besides layouts.

## Self-review notes

- Spec 8 bullets: canvas (Task 3), left panel (Task 4), right panel with bind toggle and picker
  (Task 5), bottom z-list (Task 5), repeaters edited via template with expanded cells shown (Tasks 3, 5),
  apply is save + revert (Task 8), display selector with copy-from (Task 8), settings page items
  (Task 6), starter layouts (Task 7). Not in v1 list respected.
- Spec 5: scaled-layout banner and "save for this signature" (Task 8).
- Spec 3.3: tray checkbox writes `settings.json`; daemon reads it (Task 8).
- Doctrine: each view task carries a statement step; Task 9 is the gate.
- Type names match Phases 1-4 as listed under "Interfaces consumed"; `Surface` whole-frame read
  is the one Core addition (Task 2) and is additive.
