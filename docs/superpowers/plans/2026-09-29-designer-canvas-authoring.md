# Plan: widget authoring on the canvas, and linked widgets

Brief: `docs/superpowers/specs/2026-09-29-designer-canvas-authoring.md` (confirmed 2026-09-29).
Read it first; "brief §n" below refers to it. Also read `docs/layout-format.md` "Widgets" (the
stamped-copy model this plan replaces) and `docs/architecture.md` (tick pipeline, budget).

Base: `main` @ e437597 or later. (CLAUDE.md still says `v1`, but the repo's integration branch is
`main`: every recent lane merged there.) Lanes are `lane/<name>`, each in its own worktree. Commit
per task. TDD for every Model/Core task; view tasks are verified by a build plus a screenshot under
a scratch `DESKWALL_HOME`.

Rules for every lane (CLAUDE.md, unchanged):
- Never run the designer, `deskwall tick`, `deskwall migrate` or `deskwall run` against the real
  runtime dir.
  - Scratch home: `$env:DESKWALL_HOME = "D:\scratch\dw-<lane>"`; copy `layouts\column-system.json`
    in if a layout is needed.
  - The live daemon stays running.
  - `column-system.json.mangled-by-agent-testing` in the XPS runtime dir is why.
- `dotnet build` at 0 warnings. `TreatWarningsAsErrors` + `IsAotCompatible` make every reflection
  JSON call in Core a build failure, which is the point.
- Tests run under `DESKWALL_HOME` (`tests/*/AssemblyInfo.cs`).
- `.cs`/`.ps1` are ASCII, with the BOM kept where present.
- Until `lane/designer-quick-wins` merges, a scratch designer shares the owner's
  `Local\DeskWall.Designer` mutex. Designer screenshots before that merge need the owner's designer
  closed. (Quick-wins makes the mutex per home: `RuntimeInstance.DesignerLockName`.)

## Decisions this plan settles

### D1. The v2 layout format and the override representation

A layout keeps `components` for hand-placed things and gains `copies`. A copy stores:
- a widget key, a position and a z offset;
- the knob values the owner set. Defaults are not stored, so changing a widget's default reaches
  every copy that never touched it;
- its overrides.

```json
{
  "version": 2,
  "baseImage": "...",
  "sources": [ ],
  "components": [ ],
  "copies": [
    { "id": "dial-2", "widget": "dial", "x": 3312, "y": 400, "z": 0,
      "knobs": { "metric": "GPU||hardware.gpu||hardware.gpuPct | \"{0}%\"||gpu" },
      "overrides": {
        "components.value.color": "#FFFFC000",
        "components.label.rect": "0,60,80,18",
        "components.label.hidden": true,
        "components.drives.letter.size": 15,
        "sources.hardware.every": 10
      } }
  ]
}
```

- **Override keys reuse the knob `sets` grammar.**
  - Parts: `components.<partId>.<property>`.
  - Repeater template children: `components.<repeaterId>.<childId>.<property>` (four segments).
    Part ids are validated as `[A-Za-z_][A-Za-z0-9_-]*`, so they never contain dots.
  - Sources: `sources.<name>.settings.<key>` and `sources.<name>.every`.
  - Three pseudo-properties for parts:
    - `rect`, a literal `"x,y,w,h"` relative to the copy;
    - `z`;
    - `hidden`, a literal `true`: the part is not emitted, which is how a copy "deletes" a part.
  - One executor (`KnobSets.Apply`, Core) serves knobs and overrides alike.
- **Override values are a `PropertyValue`** (a literal or `{"bind": ...}`), so the existing
  `PropertyValueConverter` serialises them with no new converter.
- **Precedence:** widget template, then knob values (the copy's, else the knob default), then
  overrides. Reset deletes the key. Setting a value equal to the baseline also deletes it
  (`Overrides.Diff`).
- **A part, property or knob the widget no longer has:** the override stays in the file untouched.
  - The expander skips it and reports `ExpandProblem(OrphanOverride, copyId, key)`.
  - The designer shows it on the copy's Layers row and in the properties panel, with Remove.
  - The daemon does not log it: it is not an error on the wallpaper.
  - If the part comes back under the same id (an undo, a restored widget file), the override
    applies again. Nothing is ever deleted silently.
- **Copy z:** each part's z is `copy.z + part.z`. The default of 0 keeps every part's z exactly as the
  widget authored it, so migration keeps content keys.
- **Expanded ids:** `"<copyId>.<partId>"`, the same as v1 stamped ids, with `ComponentDef.Widget`
  set to the copy id on expanded parts. `frame-state.json` keys therefore carry straight across.
- **Sources:** layout-level `sources` come first. Then each copy's sources, computed with its knobs
  and overrides applied:
  - Same name, identical definition (type, every, settings): shared. Four dials use one `hardware`
    sampler (brief §5, two copies sharing one data source).
  - Same name, different definition: renamed `<name>2`, `<name>3`, and so on, and that copy's
    bindings are rewritten.
  - This retires both "Known limitation" paragraphs in `docs/layout-format.md`: two headline copies
    with different feeds no longer fight.
- **v1 is still read.** A v1 file has no `copies`, so its expansion is the identity.
  `LayoutFile.Widgets` and `ComponentDef.Widget` stay readable (`[JsonIgnore(Condition =
  WhenWritingNull)]`) until Phase 6, only for the migrator.
- **Missing or unparseable widget file:**
  - Daemon: that copy is skipped, the rest paints, and the problem goes through the store's
    `onError` (tray "ERROR see log").
  - Designer: a broken-link box drawn at the copy's `x,y` (brief §5). Never a silent blank.

### D2. Copy-on-write forking needs no re-pointing

Copies reference a widget by **key**. The key is the file name, and the user dir shadows the
shipped dir by key (`WidgetCatalog.Load` already does this).
- Editing a shipped widget at widget depth writes `%LOCALAPPDATA%\DeskWall\widgets\<same key>.json`
  on Apply. Every copy of that key, in every layout on the machine, follows the fork, with nothing
  rewritten.
- "Reset to the out-of-the-box version" deletes the user file. The copies follow the shipped widget
  again, overrides intact.

To keep this true, **a key never changes after creation.**
- Renaming a widget changes `name` only.
- Phase 2 removes `WidgetTemplateWriter.Save`'s rename-then-delete-old-file, which would otherwise
  break every linked copy.
- New keys come only from New widget, Make widget and Duplicate.

Known ceiling: a fork shadows later shipped updates to that key. That is the same as today;
recorded, not solved.

### D3. AOT constraints for the expander

- Everything the daemon reaches lives in `DeskWall.Core`, under `IsAotCompatible`.
- The widget file loads through a new source-generated `WidgetJsonContext`
  (`[JsonSerializable(typeof(WidgetTemplateFile))]`). It uses the same options as
  `LayoutJsonContext`: camelCase, comments skipped, trailing commas, string enums.
- `ComponentDef` polymorphism and `PropertyValueConverter` already work under source generation.
- Knob `type` is parsed with `Enum.TryParse<KnobType>` (generic, AOT-safe).
- The `:{token}` regex is `[GeneratedRegex]`.
- Deep copies go through `LayoutFile.Parse(ToJson())`, as `LayoutScaler` already does.
- Property access is an explicit lambda table, not reflection: `Core/Layout/ComponentProperties.cs`,
  moved out of the designer's `PropertySchema`.
- Two checks, both required:
  - `dotnet build` at 0 warnings;
  - an **actual AOT publish** ticking a v2 layout. A reflection fallback compiles under JIT and only
    fails at runtime under AOT.

### D4. Where expansion runs, and why the tick budget cannot move

Expansion runs **at load, not per tick**:
- `LayoutStore.TryLoad` expands before `LayoutScaler.Scale`, so the scaler sees ordinary components.
- `Program.ResolveLayout` does the same for `--layout`.
- `PreviewRenderer` does the same on the designer's render thread.

`TickRunner` and `LayoutResolver` are not touched. Activation (on every layout edit or display
change, not every minute) gains one read of each referenced widget file; only referenced keys are
read, not the whole catalog.

The daemon watches only the user widgets dir. The shipped dir changes only on install, and
`publish.ps1` restarts the daemon then. That is one extra `FileSystemWatcher`: a 64 KB buffer and
about 1-2 handles, measured in Task 1.6.

### D5. Migration on JOES-PC and JOES-XPS-17 (runbook, Task 2.7)

Run on each machine **after Phase 2 merges**, by the owner or by the controller with the owner's
explicit go-ahead in that session. Migrating earlier would leave the owner with a layout the
designer cannot edit.

1. **Install the new build.** Close the designer. Run `scripts\publish.ps1 -Aot` from `main`. The
   new daemon reads v1 as the identity, so nothing on screen changes. Copy
   `%LOCALAPPDATA%\DeskWall\frame-state.json` to `frame-state.before.json`.
2. **Check.** Run `deskwall migrate --check`. It writes nothing. For every layout registered in
   `layouts.json`, it prints each copy, its non-default knobs and overrides, any component left
   loose, and `equivalent: yes|no`. `no` stops the runbook.
   - XPS: the Dell signature maps to `%LOCALAPPDATA%\DeskWall\column-system.json`.
   - PC: inferred to be the same file name. Read `layouts.json` there first.
3. **Migrate.** Run `deskwall migrate`.
   - It writes `<file>.v1.json` beside each file, and refuses if that backup already exists.
   - It writes v2 through `LayoutFile.Save` (temp file, then rename).
   - The watcher fires and the daemon reactivates with a forced redraw.
4. **Verify.**
   - `deskwall.log` has no ERROR line since the migrate.
   - `frame-state.json` `keysById` equals `frame-state.before.json` `keysById` (same ids, same keys).
   - `deskwall verify` reports 5/5 padding per slot.
5. **Confirm in the designer.** Open it and confirm every widget shows as a copy in Layers.

**Rollback:** `copy <file>.v1.json <file>`, then republish the previous build. An old build refuses
v2 ("version 2; this build understands up to 1") and keeps the wallpaper already on screen, so a
rollback never blanks the desktop.

The `widgets\` user dir is per machine and not synced. Each machine migrates against its own
catalog, which is why the check runs on each.

## Phase order, and what changed from the brief

The brief's six phases are kept, with four moves:
- **Migration of the live layouts moves to the end of Phase 2.** Phase 1 ships a daemon that reads
  both formats and installs with zero visible change. The switch happens once the designer can edit
  v2 at layout depth, so the owner's usual job (tweaking what is placed) never regresses.
- **Make widget and New widget:** the model lands in Phase 2 (pure, one undo entry), the UI in
  Phase 3.
- **The Insert panel (gallery to drag) moves from Phase 3 to Phase 4.** It shares the drop target
  with the Data panel, and `GalleryPanel.xaml` is on `lane/designer-quick-wins`. The brief leaves
  "whether Insert and Data merge" to the build, and one panel is the smaller build.
- **Phase 5's pure pieces (`PropertyRows`, `ColorPicker`) run as lanes during Phase 4.** They are new
  files and touch nothing else.

## Conflicts with `lane/designer-quick-wins`

That branch owns:
- `App.xaml`, `App.xaml.cs`, `BindingPicker.*`, `SecretsEditor.*`, `SettingsPage.*`, `ValueTreeView.cs`,
  `SourcesPanel.*`, `ProvidersPanel.*`, `GalleryPanel.xaml`, and `MainWindow.xaml.cs` (`RefreshStatus`);
- committed in 749b6a5: `Core/RuntimeInstance.cs`, `Core/NativeMethods.txt`,
  `Daemon/Host/HostWindow.cs`.

| Task | Conflicting file | Rule |
|---|---|---|
| Phase 1 (all) | none | Runs now. Does not touch `HostWindow.cs`; `Program.cs` is not on quick-wins. |
| 2.2 | `MainWindow.xaml.cs` (Add/Remove/Counts, not `RefreshStatus`) | May start; **merge after** quick-wins, then rebase. |
| 2.6 | `MainWindow.xaml.cs` | Same. |
| 3.5 | `MainWindow.xaml.cs` | Same. |
| 4.3, 4.4 | `GalleryPanel.xaml`, `ValueTreeView.cs` | **Wait for merge.** |
| 4.5 | `BindingPicker.*` | **Wait for merge.** |
| 5.3 | `App.xaml` (chip and row styles), `BindingPicker.*` | **Wait for merge.** |
| 6.1 | `SourcesPanel.*` (re-hosted) | **Wait for merge.** |

---

## Phase 1: Core expander, v2 format, migrator, daemon watches widgets, budget measured

### Task 1.1 (seam, integrator, on `main` first)

Freeze the shapes both lanes build on.

Files:
- `src/DeskWall.Core/Layout/WidgetCopy.cs` (new): `Id`, `Widget`, `X`, `Y`, `Z`,
  `Dictionary<string,string> Knobs`, `Dictionary<string,PropertyValue> Overrides`.
- `src/DeskWall.Core/Layout/LayoutFile.cs`: `List<WidgetCopy>? Copies`, and
  `[JsonIgnore(WhenWritingNull)]` on `Widgets`.
- `src/DeskWall.Core/Layout/ComponentDef.cs`: `[JsonIgnore(WhenWritingNull)]` on `Widget`.
- `src/DeskWall.Core/Widgets/WidgetExpander.cs` (new): signatures, plus an identity body for
  layouts with no copies:
  - `Expansion Expand(LayoutFile, Func<string, WidgetTemplate?> find)`;
  - `record Expansion(LayoutFile Layout, IReadOnlyList<ExpandProblem> Problems, IReadOnlyList<string> WidgetKeys)`;
  - `enum ExpandProblemKind { MissingWidget, BrokenWidget, OrphanOverride, OrphanKnob }`.
- `src/DeskWall.Core/Widgets/LayoutMigrator.cs` (new, stub):
  `MigrationResult Migrate(LayoutFile v1, Func<string, WidgetTemplate?> find)` and
  `record MigrationResult(LayoutFile V2, bool Equivalent, IReadOnlyList<string> Notes)`.
- `src/DeskWall.Core/Widgets/WidgetTemplate.cs` (new): the public shape only: `Key`, `Name`,
  `Size`, `Sources`, `Components`, `Knobs`, `Path`.
- `docs/layout-format.md`: D1 as a new "Copies" section; the old "Widgets" section is marked "v1,
  read only for migration".

Done when: `dotnet build` 0 warnings, and the existing suites are green (the v1 identity path
changes nothing).

### Task 1.2 (`lane/p1-expander`): widget templates in Core, source-generated

Files:
- `src/DeskWall.Core/Widgets/WidgetTemplate.cs`: the body, ported from
  `src/DeskWall.Designer/Model/Widgets/WidgetTemplate.cs` with its validation messages, loaded
  through `WidgetJsonContext`.
- `src/DeskWall.Core/Widgets/WidgetCatalog.cs`:
  - `ShippedDir` = `AppContext.BaseDirectory\widgets`; `UserDir` = `Paths.InRuntime("widgets")`;
  - `Finder(params string[] dirs)`: the last dir wins by key; reads only the keys asked for;
    caches per finder;
  - `CandidatePaths(key)`.
- `src/DeskWall.Core/DeskWall.Core.csproj`: nothing. Instead, `src/DeskWall.Daemon/DeskWall.Daemon.csproj`
  gets the same `<None Include="..\..\widgets\*.json" Link=...>` the designer has (Task 1.5), so a
  dev `deskwall` finds shipped widgets. `publish.ps1`'s merge skips hash-identical files, so the
  install is unaffected.

The designer keeps its own copy until Task 2.1. That is deliberate: Task 1.4's parity test needs
both.

Tests, in `tests/DeskWall.Core.Tests/Widgets/WidgetTemplateTests.cs`:
- every file in the repo's `widgets/` loads;
- the five designer-side rejections still reject (missing name, bad size, bad anchor, more than 5
  knobs, unknown knob type);
- a user-dir key shadows a shipped key.

Done when: green, and `dotnet build` at 0 warnings (the AOT analyzer is the gate).

### Task 1.3 (`lane/p1-expander`): `KnobSets`, `ComponentProperties` and the expander

Files:
- `src/DeskWall.Core/Layout/ComponentProperties.cs` (new): a per-type `(Name, Get, Set)` table, with
  the same names and order as `Designer/Model/PropertySchema.cs`, plus `rect`, `z` and `hidden`.
- `src/DeskWall.Core/Widgets/KnobSets.cs` (new): the `sets` path executor, ported from
  `WidgetInstance.ApplySet`, `ApplyTokenGroup` and `Substitute`.
  - It works on a clone of the template's parts with template-local ids. Expansion always starts
    from the template, so the rule "substitute from the template's own placeholder text" is
    automatic.
  - It also handles the four-segment repeater-child form.
- `src/DeskWall.Core/Widgets/WidgetExpander.cs`: the full body, per D1: clone, apply knobs, apply
  overrides, offset, prefix ids, set `Widget` to the copy id, merge sources with rename-on-difference,
  report problems.

Tests, in `tests/DeskWall.Core.Tests/Widgets/KnobSetsTests.cs` and `WidgetExpanderTests.cs`:
- identity for a v1 file;
- ids, rects and z for a plain copy;
- the Metric, Town (two tokens, one URL) and Drive (a token inside a binding) knobs;
- an override beats a knob;
- `rect`, `hidden`, and a repeater-child override;
- an orphan override, an orphan knob, a missing widget and a broken widget are each reported,
  while the rest still emits;
- identical sources are shared, and differing ones are renamed with their bindings rewritten.

Done when: green, 0 warnings.

### Task 1.4 (`lane/p1-expander`): migrator, and parity with the old stamping code

Files:
- `src/DeskWall.Core/Widgets/LayoutMigrator.cs`, per `WidgetRecord`:
  - `x,y` = the most common (instance rect minus template rect) offset over matching part ids;
  - knob values are kept only when they differ from the default;
  - every property, rect or z that differs from the expanded baseline becomes an override
    (`Overrides.Diff`);
  - a template part the instance lacks gets `hidden`;
  - an instance component the template lacks becomes a loose component (`Widget` cleared);
  - a layout source is dropped only when no loose component binds it and the copies reproduce it.

  `Equivalent` means all three of:
  - the expanded v2 components are structurally equal to the v1 components (JSON by id, with the
    `widget` field dropped);
  - the sources are the same;
  - every overlapping pair of equal z keeps its paint order.

  Migrating a v2 file is a no-op.
- `src/DeskWall.Core/Widgets/Overrides.cs`: `Diff(baseline, edited)` returns the overrides. The
  designer's copy-depth lens reuses it in Phase 2.

Tests:
- `tests/DeskWall.Core.Tests/Widgets/LayoutMigratorTests.cs`:
  - `layouts/column-system.json`, `clock-disks.json` and `steam-recent.json` (real v1 stamped files)
    each migrate `Equivalent`;
  - fixtures for a drifted property, a deleted part, an extra component and a renamed source
    (`weather2`);
  - migrating a second time is a no-op.
- `tests/DeskWall.Designer.Tests/Widgets/ExpanderParityTests.cs` (new file, no conflict). For every
  shipped widget and every choice of every choice knob, the old designer path (`WidgetInstance.Add`
  plus `SetKnob`) must equal `WidgetExpander.Expand` of the matching copy. This proves the Core port
  of the `sets` grammar did not change what a knob produces.

Done when: both suites green.

### Task 1.5 (`lane/p1-daemon`, parallel with 1.2-1.4, against the seam)

Owned files:
- `src/DeskWall.Core/Layout/LayoutStore.cs`:
  - `MaxVersion = 2`;
  - `TryLoad` expands through `WidgetCatalog.Finder(ShippedDir, UserDir)` and reports Missing and
    Broken through `onError`;
  - `WatchPaths` adds the user-dir candidate path of every widget key the last resolve referenced.
- `src/DeskWall.Daemon/DaemonLoop.cs`:
  - `Activate` is reordered to Reload, then Resolve, then `Rescan`, so the widget paths are known
    before the watch set is taken;
  - `Run` calls `Directory.CreateDirectory(WidgetCatalog.UserDir)` once, so the first fork is
    watchable.

  `LayoutWatcher.cs` needs no change: it already filters on exact paths and watches their
  directories.
- `src/DeskWall.Daemon/Program.cs`:
  - the `--layout` branch of `ResolveLayout` expands;
  - `tick --measure` prints a `load N` line (a stopwatch around `ResolveLayout`), named so the
    budget test's `^total` regex does not match it;
  - a new `migrate [--check] [<path>]` command. The default is every file in `layouts.json`. It
    behaves as in D5: backup to `<file>.v1.json`, refuse if the backup exists, temp file then rename.
- `src/DeskWall.Daemon/DeskWall.Daemon.csproj`: the `widgets\*.json` link.

Tests:
- `tests/DeskWall.Core.Tests/Layout/LayoutStoreTests.cs` (additions):
  - v2 resolves expanded;
  - expanding then scaling is correct;
  - version 3 is rejected;
  - `WatchPaths` contains `widgets\<key>.json`.
- `tests/DeskWall.Core.Tests/MigrateCommandTests.cs` (new). It drives the exe like
  `StopCommandTests`, under a scratch `--home`:
  - `--check` writes nothing;
  - `migrate` writes the backup and a v2 file;
  - a second `migrate` refuses.

Done when:
- both suites green;
- `deskwall --home D:\scratch\dw tick --layout <v2 file> --force --measure --no-apply --no-shortcuts`
  prints a table with a `load` line;
- under a scratch `deskwall --home D:\scratch\dw run`, touching `D:\scratch\dw\widgets\dial.json`
  logs a reactivation within 1 s. Stop it with `deskwall --home D:\scratch\dw stop`.

### Task 1.6 (integrator, after 1.2-1.5 merge): AOT and the budget, measured

Both machines now have the MSVC linker (JOES-XPS-17 since 2026-09-28). Record which machine the
numbers come from; JOES-PC is preferred, because the 10 MB budget is defined there.

1. **Publish twice.** With the VS Installer PATH fix (CLAUDE.md), run
   `dotnet publish src/DeskWall.Daemon -c Release -r win-x64` at e437597 ("before") and at the merged
   `main` ("after"). Zero warnings both times.
2. **Tick both under a scratch home.** The before build ticks `layouts\column-system.json` (v1). The
   after build ticks the same file, then its `deskwall migrate` output (v2). Each run is five ticks
   of `tick --force --measure --no-apply --no-shortcuts`, then five unforced (skip-path) ticks.
3. **Record the results** in `docs/superpowers/plans/2026-09-29-designer-canvas-authoring-results.md`:
   the median `resolve / draw / total / cpu / load` rows, plus idle handles and threads from a
   scratch `run`.

Pass rule:
- `total` and `cpu` within one 15.6 ms CPU quantum of before;
- the skip-path tick unchanged;
- at most 2 more handles.

The AOT exe ticking a v2 file is also the runtime proof of D3.

Done when: the results file is committed, with both columns and a verdict per row.

**Lanes in Phase 1**

| Lane | Owns |
|---|---|
| (seam, `main`) | `Core/Layout/WidgetCopy.cs`, `Core/Layout/LayoutFile.cs`, `Core/Layout/ComponentDef.cs`, `docs/layout-format.md`, stubs of `Core/Widgets/WidgetExpander.cs`, `LayoutMigrator.cs`, `WidgetTemplate.cs` |
| `lane/p1-expander` | `Core/Widgets/*` (bodies), `Core/Layout/ComponentProperties.cs`, `tests/DeskWall.Core.Tests/Widgets/*`, `tests/DeskWall.Designer.Tests/Widgets/ExpanderParityTests.cs` |
| `lane/p1-daemon` | `Core/Layout/LayoutStore.cs`, `Daemon/DaemonLoop.cs`, `Daemon/Program.cs`, `Daemon/DeskWall.Daemon.csproj`, `tests/DeskWall.Core.Tests/Layout/LayoutStoreTests.cs`, `tests/DeskWall.Core.Tests/MigrateCommandTests.cs` |

---

## Phase 2: the designer's document: v2, three depths, overrides, one undo stack

### Task 2.1 (serial, first): the designer uses Core's widget types

Delete `src/DeskWall.Designer/Model/Widgets/WidgetTemplate.cs`, `WidgetCatalog.cs` and `WidgetJson.cs`.
Point these at `DeskWall.Core.Widgets`:
- `Model/Widgets/Adjustable.cs`
- `WidgetDocument.cs`
- `WidgetTemplateWriter.cs` (serialise through `WidgetJsonContext`)
- `Arranger.cs`
- `Views/GalleryPanel.xaml.cs` (not the `.xaml`)
- `Views/KnobsPanel.xaml.cs`
- `Views/WidgetEditorWindow.xaml.cs`
- `tests/DeskWall.Designer.Tests/Widgets/*`

`Model/PropertySchema.cs` keeps `Editor` and `Choices` but takes `Get`/`Set` from Core's
`ComponentProperties`, so the two lists cannot drift apart.

Also D2's key rule:
- `WidgetDocument.Key` is fixed at creation: `EditingKey` if set, otherwise `Slug(Name)`, once.
- `WidgetTemplateWriter.Save` no longer deletes on rename.
- Tests: `WidgetTemplateWriterTests` (a rename keeps the key and the file) and `ShippedEditingTests`.

Done when: build at 0 warnings, both suites green. `ExpanderParityTests` would now compare Core
with Core, so delete it in this task.

### Task 2.2 (seam, integrator): the `DesignerModel` document shape

`src/DeskWall.Designer/Model/DesignerModel.cs` gains:
- `IReadOnlyDictionary<string, WidgetTemplate> WidgetEdits`: the widget overlay (edited, not yet
  applied);
- `Func<string, WidgetTemplate?> Finder()`: the overlay, then the user dir, then the shipped dir;
- `Expansion Expanded()`, cached per change;
- `Depth` and `SetDepth`, with `Depth` in `src/DeskWall.Designer/Model/Depth.cs` (new):
  `record Depth(DepthKind Kind, string? CopyId, string? WidgetKey)`.

Signatures only, with an empty overlay.

Done when: build.

### Task 2.3 (`lane/p2-document`): one undo stack over the layout, the widgets and depth pruning

`DesignerModel.cs`:
- `Snapshot` becomes `(LayoutJson, Signature, WidgetEditsJson)`. `WidgetDocument`'s canvas-size
  change already set the precedent for extending it.
- `Dirty` includes the overlay.
- `Save` writes every overlay widget to `WidgetCatalog.UserDir\<key>.json` (the fork, D2), then the
  layout, each as temp file then rename.
- `PruneDepth()` runs after Undo/Redo and climbs out of a copy or widget that no longer exists.

Tests, in `tests/DeskWall.Designer.Tests/DesignerModelDepthTests.cs`:
- an undo across a widget edit restores the layout and the widget together;
- an undo of Make widget removes both the copy and the overlay entry;
- Save forks a shipped key into the user dir and leaves the shipped dir untouched;
- an undo while at the depth of a removed copy climbs to layout depth.

Done when: green.

### Task 2.4 (`lane/p2-document`): the lens

The one edit path for all three depths, in `src/DeskWall.Designer/Model/Lens.cs` (new):
- `Project(model)`: the `ComponentDef`s the canvas and properties panel edit at the current depth:
  - layout depth: loose components, plus the copies as targets;
  - copy depth: that copy's expanded parts, with absolute rects;
  - widget depth: the widget's parts at the copy's origin.
- `Commit(model, before, after)`:
  - copy depth: `Overrides.Diff` against the knob-applied baseline. A value equal to the baseline
    removes its override, so Reset falls out of the same code;
  - widget depth: writes the parts back into the overlay template, minus the origin.
- `Reset(copyId, key)` and `PushToWidget(copyId, key)` (move the value into the overlay part, then
  delete the override).
- `MakeWidget(componentIds)`: a new key; parts made relative to their bounds; the sources they bind
  copied from the layout; the loose components replaced by a copy. One `Edit`.
- `NewWidget(at)`: an empty 172x40 template, a copy placed, depth set to widget.

The existing `Move`/`Scale`/`SetRect`/`Resize.Apply` code runs unchanged on the projection. That is
the reason for a lens rather than separate mutators per depth.

Tests, in `tests/DeskWall.Designer.Tests/LensTests.cs`:
- moving a part at copy depth writes `components.<id>.rect` and nothing else; moving it back
  removes the override;
- a colour change at widget depth reaches a second copy that has no override, and not one that has;
- Push to widget empties the override and changes the other copy;
- `MakeWidget` round-trips: expanding the result gives the original components back, ids aside.

Done when: green.

### Task 2.5 (`lane/p2-copies`, parallel with 2.3-2.4): everything that read stamped instances

Files:
- `src/DeskWall.Designer/Model/Copies.cs` (new; replaces `Model/Widgets/WidgetInstance.cs`, which is
  deleted):
  - `Add(layout, template, x, y)` gives copy id `<key>-<n>`;
  - `Remove`;
  - `SetKnob` writes `copy.Knobs`, and removes the entry when it equals the default;
  - `Bounds(expansion, copyId)`: the union of the parts; else the template size; else 172x40 for a
    broken link.
- `Model/Targets.cs`: targets from `model.Expanded()`, one per copy.
- `Views/KnobsPanel.xaml.cs`: knobs over `copy.Knobs`.
- `Views/MainWindow.xaml.cs`:
  - `Add`, `Remove`, `RemoveSelection`, `Counts`, and `RebuildLiveSources` (from the expanded
    sources);
  - the `DeleteTemplate` wording becomes "N copies on this wallpaper will show as missing". The old
    "stay exactly as they are" is false once copies are linked.
- `Model/PreviewRenderer.cs`: the snapshot carries the overlay JSON; `Render` expands before
  `Resolve`; `PreviewFrame` gains `Problems`.
- `Model/ShellState.cs`: opening a v1 file migrates it in memory (`LayoutMigrator`); the first Apply
  writes `<file>.v1.json` if it is absent.
- `tests/DeskWall.Designer.Tests/Widgets/StarterGenerator.cs`: emits v2 through `Copies` and
  `Arranger` (`Arranger.cs` moves `copy.X/Y`). Regenerate `layouts/*.json` and commit them.
- `tests/DeskWall.Core.Tests/Layout/StarterLayoutTests.cs`: expand with the repo's `widgets/` before
  resolving.

Tests:
- `WidgetInstanceTests.cs` becomes `CopiesTests.cs`, keeping the behaviours that still apply;
- `PreviewRendererTests`: a missing widget yields a problem, not an exception;
- `StarterGeneratorTests`.

Done when: green, and under a scratch home the designer opens `layouts\column-system.json` (v2),
adds a dial, turns its Metric knob and Applies. The scratch file then has one `copies` entry per
widget. The screenshot goes in the lane report.

### Task 2.6 (serial, after 2.3-2.5)

- `KnobsPanel`'s Details expander edits through `Lens` at copy depth, so tweaking a placed widget
  writes overrides.
- The existing `WidgetEditorWindow` Save now reloads the catalog, and the placed copies visibly
  change: linking is live before any new UI.

Done when (scratch home): change the clock widget's size in the editor and Save; the placed clock
redraws larger without being re-added. Screenshot.

### Task 2.7 (owner, both machines): the migration runbook, D5

Done when, on each machine:
- `migrate --check` says `equivalent: yes`;
- the backup exists;
- `keysById` is identical before and after;
- the log has no ERROR line.

Record both machines in the results file.

**Lanes in Phase 2**

| Lane | Owns |
|---|---|
| 2.1 (serial) | the files listed in 2.1 |
| `lane/p2-document` | `Model/DesignerModel.cs`, `Model/Depth.cs`, `Model/Lens.cs`, `tests/.../DesignerModelDepthTests.cs`, `LensTests.cs`, `DesignerModelTests.cs` |
| `lane/p2-copies` | `Model/Copies.cs`, `Model/Targets.cs`, `Model/PreviewRenderer.cs`, `Model/ShellState.cs`, `Model/Widgets/Arranger.cs`, `Views/KnobsPanel.xaml.cs`, `Views/MainWindow.xaml.cs` (merge after quick-wins), `layouts/*.json`, `tests/.../Widgets/{CopiesTests,StarterGenerator,StarterGeneratorTests}.cs`, `tests/.../PreviewRendererTests.cs`, `tests/DeskWall.Core.Tests/Layout/StarterLayoutTests.cs` |

---

## Phase 3: canvas basics, depth navigation, Layers panel

### Task 3.1 (`lane/p3-canvas`): continuous zoom, rendered at scale

Files:
- `src/DeskWall.Designer/Model/Viewport.cs` (new, pure):
  - zoom steps from 1/8 to 16;
  - fit all (Shift+1), fit selection (Shift+2), and zoom about the cursor (Ctrl+wheel);
  - `ToCanvas` / `ToScreen`;
  - the pan clamp, moved from `PreviewView.ClampPan` / `KeepVisible`.
- `src/DeskWall.Core/Layout/LayoutScaler.cs`: a public `Transform(layout, scale, dx, dy)` sharing the
  private `ScaleComponent`. Text size, dial thickness and radius scale; `"auto"` is left alone, as
  now.
- `Model/PreviewRenderer.cs`: `Request(model, Viewport)`:
  - at zoom 1 or below: the current full-canvas render, which WPF downscales;
  - above zoom 1: expand, then `Transform`, then resolve, then render a viewport-sized `Surface`. The
    base is drawn with `Surface.DrawSurface(Surface.LoadRaw(baseRaw), dst, Fit.Stretch)`, and
    Direct2D clips it;
  - the hit map stays the 1:1 resolve.
- `Views/PreviewView.xaml(.cs)`: replaces the `_oneToOne` toggle.

Tests:
- `tests/DeskWall.Designer.Tests/ViewportTests.cs`;
- `PreviewRendererTests`: at 4x the frame is the viewport size, and a 13 px label's resolved rect is
  4x; `RenderTime` at 8x on 3440x1440 is recorded.

Done when (scratch home): a screenshot of the dial's 11 px `label` at 800% shows crisp glyph edges
and no bitmap scaling. The lane report notes the measured render time at 8x; accept it at or under
100 ms, otherwise stop and raise it.

### Task 3.2 (`lane/p3-canvas`): smart guides by default, hug contents

Files:
- `Model/Snap.cs`: already written, never called.
- `Views/PreviewView.xaml.cs`: moves and resizes call `Snap.Apply` / `Snap.Edge` against the sibling
  targets at the current depth, with the threshold divided by the zoom. Alt suspends snapping and
  the guides are drawn. The grid stays on Shift (`SnapModifier`).
- `Model/Lens.cs`: owned by Phase 2 and already merged, so it is edited here; no parallel lane
  touches it in Phase 3.
  - At widget depth, `Commit` sets the template `Size` to the parts' bounds.
  - A part dragged to negative coordinates renormalises the parts to (0,0), and every copy of that
    key in the open layout shifts by the same amount.

Tests: `tests/DeskWall.Designer.Tests/SnapTests.cs` (new; there are none today), plus a `LensTests`
addition for hug and renormalise.

Done when: green, plus a screenshot of a guide showing mid-drag.

### Task 3.3 (`lane/p3-layers`, parallel with 3.1-3.2): the Layers panel

Files:
- `src/DeskWall.Designer/Model/LayerTree.cs` (new, pure):
  - rows for copies (expandable to their parts), loose components and repeater children;
  - flags `HasOverride`, `IsOrphan`, `IsBroken`, `IsForkedShipped`.
- `src/DeskWall.Designer/Views/LayersPanel.xaml(.cs)` (new): a TreeView with named,
  keyboard-reachable rows and two-way selection sync through `model.Select`. `ComponentLookup.cs`
  stays as it is.

Tests: `tests/DeskWall.Designer.Tests/LayerTreeTests.cs`: the override dot is on the right part;
an orphan override gets its row; a broken widget gets its row.

Done when: green.

### Task 3.4 (`lane/p3-canvas`): depth on the canvas

`Views/PreviewView.xaml.cs`:
- double-clicking a copy goes to copy depth and zooms to fit it;
- at widget depth, everything else is dimmed (a WPF overlay outside the copy's bounds);
- a hatched broken-link box with the text "Widget '<key>' is missing", from `PreviewFrame.Problems`;
- events `DepthRequested` and `EditWidgetRequested`.

Done when: a screenshot at each of the three depths.

### Task 3.5 (serial, after 3.1-3.4): the shell

Files:
- `Views/MainWindow.xaml`: the left column becomes Layers above the gallery, and the top bar gains a
  breadcrumb ("Layout › Hardware dial (copy)" or "(widget)").
- `Views/MainWindow.xaml.cs` (merge after quick-wins), in `OnPreviewKeyDown`:
  - Enter and Esc go down and up a depth; Esc climbs before it clears the selection;
  - Tab and Shift+Tab cycle siblings;
  - Ctrl+C, V, D and A, with an in-process clipboard of copies and components as JSON;
  - arrow-key nudges (already there);
  - Ctrl+[ and Ctrl+] (the copy's `z`, or a part's z through the lens);
  - Ctrl+Alt+K: Make widget when loose parts are selected, Edit widget when a copy is selected;
  - update the class doc comment's "deliberately left out" list.

Tests: none new; the model tests in 2.4 cover the verbs.

Done when: the owner's sequence reaches widget depth and back with the keyboard alone; the lane
report lists the keys pressed. The critique re-run is not due yet.

**Lanes in Phase 3**

| Lane | Owns |
|---|---|
| `lane/p3-canvas` | `Model/Viewport.cs`, `Model/Snap.cs`, `Model/Lens.cs`, `Model/PreviewRenderer.cs`, `Views/PreviewView.xaml(.cs)`, `Core/Layout/LayoutScaler.cs`, `tests/.../{ViewportTests,SnapTests,LensTests,PreviewRendererTests}.cs`, `tests/DeskWall.Core.Tests/Layout/LayoutScalerTests.cs` |
| `lane/p3-layers` | `Model/LayerTree.cs`, `Views/LayersPanel.xaml(.cs)`, `tests/.../LayerTreeTests.cs` |
| 3.5 (serial) | `Views/MainWindow.xaml(.cs)` |

---

## Phase 4: drag to bind, the Data panel, binding chips, format presets

### Task 4.1 (`lane/p4-data-model`; new files, may start before quick-wins merges)

- `src/DeskWall.Designer/Model/ValueCatalog.cs`: flattens `LiveSources.Tree()` into
  `(path, label, kind, sample)`.
  - Kind is one of Fraction, Timestamp, Number, Text, Bool and List. A Fraction is a number in 0..1
    from a field known to be one, e.g. `hardware.cpu` or `disks.drives[*].usedFraction`.
  - Human labels ("CPU load") come from a table built from `docs/sources.md`, with the path as the
    fallback.
  - `Fits(PropertySchema.Editor, kind)`.
- `src/DeskWall.Designer/Model/FormatPresets.cs`: `(label, format)` pairs per kind, e.g. `27%`,
  `27.4`, `14:05`, `Mon 29 Sep`, `64 GB free`.
- `src/DeskWall.Designer/Model/DropPlan.cs`: maps (value kind, drop target) to an action:
  - on empty canvas, create a Dial or Bar (Fraction), a Clock (Timestamp) or Text with a preset
    (anything else);
  - on a part, bind its fitting property;
  - either way, add the value's source to the layout (or to the widget, at widget depth) if it is
    missing.

Tests:
- `ValueCatalogTests`;
- `FormatPresetsTests`: every preset passed through Core `Value.ToText` gives no fallback to the
  raw value;
- `DropPlanTests`: a fraction on empty canvas offers Dial and Bar; a timestamp on a text part binds
  `Text` with `HH:mm`; the source is added once.

Done when: green.

### Task 4.2 (`lane/p5-rows`, parallel)

`Model/PropertyRows.cs`:
- the groups Content, Type, Colour and Arc;
- human labels ("Warn at", "Stroke width");
- a percentage adapter for `Threshold`, `Opacity` and `Fraction` (90% to and from 0.9).

Tests: `PropertyRowsTests`: every property in `PropertySchema` has a row.

### Task 4.3 (`lane/p5-color`, parallel)

`Views/ColorPicker.xaml(.cs)` (new): a hue and alpha picker, a checkerboard under translucent
swatches, hex in and out through Core `Color.Parse`.

Done when: a screenshot of `#A0FFFFFF` over the checkerboard.

### Task 4.4 (after quick-wins merges): the Insert panel

`Views/InsertPanel.xaml(.cs)` (new) replaces `GalleryPanel.xaml(.cs)`, which is deleted.
- Three sections: Parts, Widgets and Data.
- Data rows read "CPU load · 27%", are searchable, and are drag sources.
- `ValueTreeView.cs` is reused or deleted, depending on what quick-wins left it as.
- `Views/PreviewView.xaml.cs` is the drop target and runs `DropPlan`.
- `Views/MainWindow.xaml` swaps the gallery for the Insert panel.

Done when (scratch home): the brief §2 sequence "a CPU load dial with a label" takes at most 8
actions, 0 dialogs and 0 syntax. Write the actions in the lane report. The target sequence:
1. Drag "CPU load" to empty canvas.
2. Pick Dial.
3. Drag "CPU load" again, onto the dial's centre, which gives Text "27%".
4. Drag a Text part below it.
5. Type "cpu".
6. Press Enter.
7. Select all three.
8. Press Ctrl+Alt+K.

### Task 4.5 (after quick-wins merges): binding chips

`Views/BindingChip.xaml(.cs)` (new): the "CPU load · 27%" chip, with typeahead over `ValueCatalog`
filtered by `Fits`. `Views/BindingPicker.xaml(.cs)` is deleted once `PropertiesPanel` stops using it
(Task 5.1).

**Lanes in Phase 4**
- `lane/p4-data-model`: the 4.1 files and tests.
- `lane/p5-rows`: 4.2.
- `lane/p5-color`: 4.3.
- Then `lane/p4-insert`: `InsertPanel.*`, the `GalleryPanel.*` deletion, `PreviewView.xaml.cs` and
  `MainWindow.xaml`.
- Then `lane/p4-chips`: `BindingChip.*`.

---

## Phase 5: the properties panel

### Task 5.1 (after quick-wins merges)

`Views/PropertiesPanel.xaml(.cs)` is rebuilt on `PropertyRows`:
- `ColorPicker` for colour rows and `BindingChip` for bound rows;
- a Bind hover icon on each row;
- at widget depth, "Expose as knob" in the row menu (`Adjustable.ToKnob`, written into the overlay
  template);
- at copy depth, an override marker per row, with Reset and Push to widget (`Lens`);
- the copy's knobs as the first group.

`Views/KnobsPanel.xaml(.cs)` is deleted. `Views/MainWindow.xaml`'s right column becomes the
properties panel above `ProvidersPanel`. `App.xaml` gets the row and chip styles.

Tests: `PropertiesPanelTests.cs` (exists): an overridden row reports Reset; percentage rows
round-trip.

Done when: a screenshot at copy depth with one overridden row.

### Task 5.2: the accessibility floor (brief §7)

Every new control gets `AutomationProperties.Name` and is reachable with Tab. Text contrast is
checked at 4.5:1 against the panel background in both themes, and the lane report lists the pairs
checked.

Done when: the lane report's table is filled in, and Narrator reads every row of the properties
panel.

---

## Phase 6: retire `WidgetEditorWindow`, drop v1, prove it

### Task 6.1

- Delete `Views/WidgetEditorWindow.xaml(.cs)`.
- The Insert panel's menu: Edit goes to widget depth; New calls `Lens.NewWidget`; Duplicate makes an
  overlay copy under a new key; Reset deletes the user file.
- `Views/SourcesPanel.*` is hosted in the right column at widget depth.
- Trim `WidgetDocument.cs` to what `Lens` and `Adjustable` still call.

Done when: it builds, the suites are green, and `grep -r WidgetEditorWindow src` finds nothing.

### Task 6.2 (only after Task 2.7 is recorded on both machines)

- Delete `Core/Layout/WidgetRecord.cs` and `LayoutFile.Widgets`.
- Make `ComponentDef.Widget` `[JsonIgnore]` (runtime only).
- Delete `Core/Widgets/LayoutMigrator.cs`, `deskwall migrate` and their tests.
- A version-1 file without `widgets` still loads as a v2 file with no copies.

Done when: green, and `deskwall --home <scratch> tick` of each repo layout is unchanged.

### Task 6.3: docs and proof

- Update `docs/layout-format.md` ("Copies" replaces "Widgets"), `docs/architecture.md` (the watcher,
  and the load cost from 1.6), `README.md`, and CLAUDE.md (the branch name `main`, the designer row).
- Re-run `/impeccable critique` on `src/DeskWall.Designer`. Recognition (6) and Flexibility (7) must
  each reach at least 3 (brief §2). Record the snapshot path.

Done when: the critique scores are recorded in the results file.

---

## Top five risks, and the phase that retires each

1. **The migration loses or changes the live layout on either machine.** This has happened before:
   the `.mangled-by-agent-testing` file.
   - Phase 1 proves the migrator `Equivalent` on the three real v1 repo files and on drift, deletion
     and rename fixtures. v1 still reads as the identity, so installing the new build changes
     nothing.
   - The migration itself runs only at the end of Phase 2, by the owner, with `--check`, a backup
     that refuses to be overwritten, and the `keysById` equality check.
   - Rollback is one copy, and an old build refuses v2 without blanking the wallpaper.
2. **The expander works under JIT and fails in the AOT daemon.** Phase 1: a source-generated
   `WidgetJsonContext`, the analyzer at 0 warnings, and Task 1.6's real AOT publish ticking a v2
   file.
3. **The tick or idle budget grows.** Phase 1:
   - expansion runs at activation, not per tick (D4), so `TickRunner` is untouched by construction;
   - Task 1.6 measures before and after, with a pass rule;
   - only one watcher is added, for the user dir.
4. **Linking corrupts the document.** For example: an undo that restores the layout but not the
   widget, a rename that orphans copies, or overrides silently dropped when a part goes. Phase 2
   covers this, all tested in `DesignerModelDepthTests` and `LensTests`:
   - one snapshot of (layout, overlay, signature);
   - keys fixed at creation (D2);
   - orphan overrides kept and reported, never deleted (D1).
5. **The Core port changes what shipped widgets draw, or the zoomed canvas is too slow to use.**
   - The port: Phase 1's `ExpanderParityTests` compares the old stamping with the new expander for
     every shipped widget and knob choice.
   - The zoom: Phase 3 renders only the viewport, measures `RenderTime` at 8x, and stops to raise it
     above 100 ms.

## Hand-off per phase

Each phase ends on `main` with:
- 0 build warnings and both suites green;
- a lane report per lane in `.superpowers/sdd/2026-09-29-designer-canvas-authoring/`: what was
  verified, the screenshots with their scratch-home paths, and any deviations;
- the results file updated.

The integrator publishes to the owner only after Phase 2 (for the migration) and after Phase 6.

## Inferred, not read

- JOES-PC's `layouts.json`, and whether it has a user `widgets\` folder. Step 2 of the runbook reads
  it first.
- The 100 ms threshold for rendering at 8x; nothing has measured it yet.
- Task 4.4's eight-action sequence is a design target, not a count anyone has done.
