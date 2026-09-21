# Designer widgets — Implementation Plan (thin, by the owner's request)

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development, run in its fast mode for this plan: two lanes, no reviewer agents, the controller gates on build + tests + a diff skim and the owner tests the build.

**Goal:** Rebuild the designer as a widget picker on WPF + Fluent: gallery of real-rendered widget templates, one-click add, knobs instead of bindings, an arranger instead of coordinates, Details for the rare advanced case.

**Spec:** `docs/superpowers/specs/2026-09-21-designer-widgets-design.md` (binding; read it first).

## Global Constraints

- CLAUDE.md applies. Core stays native-AOT safe, `dotnet build` 0 warnings. The daemon's behaviour does not change: the only Core edits are `ComponentDef.Widget` (`string?`) and `LayoutFile.Widgets` (`Dictionary<string, WidgetRecord>?`), both optional, both ignored by resolve/render, both preserved by `LayoutScaler` and JSON round trip.
- Fluent via `ThemeMode="System"` on `App.xaml` (`<NoWarn>$(NoWarn);WPF0001</NoWarn>` in the designer csproj). No third-party UI packages.
- Doctrine (spec section 2): top bar at most six controls; at most five knobs per widget; gallery cards are real renders; no ids, rects, bindings or JSON in the default view.
- Files UTF-8 CRLF, keep BOM state. Designer tests run under `DESKWALL_HOME`. Lanes never run `deskwall`, never launch the designer window, never touch the live desktop or the real runtime dir.
- Lane reports to `.superpowers/sdd/2026-09-21-designer-widgets/lane-<name>-report.md` (inside the worktree if the shared path is refused).

## Frozen API (lane M produces, lane U consumes; both code against this exactly)

Namespace `DeskWall.Designer.Model`, folder `src/DeskWall.Designer/Model/Widgets/`.

```csharp
public sealed class WidgetTemplate            // loaded from widgets/<name>.json
{
    public required string Name { get; init; }           // file name without .json is the key: Key
    public string Key { get; init; }
    public required string Description { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public string Anchor { get; init; } = "top";          // "top" | "bottom"
    public IReadOnlyList<SourceDef> Sources { get; init; }
    public IReadOnlyList<ComponentDef> Components { get; init; }   // template coordinates
    public IReadOnlyList<Knob> Knobs { get; init; }
    public string? Requires { get; init; }                // human sentence shown when a requirement may be missing, e.g. "Needs an NVIDIA GPU"
    public static WidgetTemplate Load(string path);
    public LayoutFile Preview(string baseImage);          // a layout containing just this widget at 0,0 on the given base, for gallery cards
}

public sealed record Knob(string Id, string Label, KnobType Type, string Default, IReadOnlyList<string> Sets, IReadOnlyList<string>? Choices, double? Min, double? Max);
public enum KnobType { Number, Text, Choice, Color, Drive, Town }

public sealed class WidgetRecord { public required string Template { get; set; } public Dictionary<string, string> Knobs { get; set; } = new(); public bool Unlocked { get; set; } }

public static class WidgetCatalog
{
    public static IReadOnlyList<WidgetTemplate> Load(params string[] dirs);   // repo/shipped dir first, then runtime dir; runtime wins on key clash
    public static string ShippedDir => Path.Combine(AppContext.BaseDirectory, "widgets");
    public static string UserDir => Paths.InRuntime("widgets");
}

public static class WidgetInstance
{
    /// Adds the widget to the layout: components copied with ids "<instanceId>.<id>", Widget = instanceId,
    /// rects offset by origin; sources merged by name (same name+type reused; clash -> "<name>2" and bindings rewritten);
    /// knob defaults applied; layout.Widgets[instanceId] = new WidgetRecord. Returns instanceId ("<key>-<n>", n = first free).
    public static string Add(LayoutFile layout, WidgetTemplate t, Rect origin);
    public static void Remove(LayoutFile layout, string instanceId);       // components, Widgets entry, then sources no component binds to
    public static IReadOnlyList<ComponentDef> Components(LayoutFile layout, string instanceId);
    public static Rect Bounds(LayoutFile layout, string instanceId);       // union of the instance's rects
    public static void SetKnob(LayoutFile layout, WidgetTemplate t, string instanceId, string knobId, string value);  // applies Sets paths, records the value
    public static Task<(double lat, double lon)?> ResolveTownAsync(string town, HttpClient http);  // Open-Meteo geocoding, count=1
}

public static class Arranger
{
    public const int ColumnX = 3220, ColumnWidth = 172, TopY = 40, BottomY = 1400, Gap = 16;
    /// Stacks widgets in `order` (instance ids): anchor top downwards from TopY, anchor bottom upwards from BottomY;
    /// skips instances whose WidgetRecord.Unlocked is true; writes rects into the layout's components.
    public static void Arrange(LayoutFile layout, IReadOnlyList<WidgetTemplate> catalog, IReadOnlyList<string> order);
    public static IReadOnlyList<string> Order(LayoutFile layout);   // current top-to-bottom order by Bounds().Y, unlocked last
}
```

## Lane M (model + widgets + Core field), Sonnet, worktree from v1

1. Core: `ComponentDef.Widget` (`string?`, JSON `widget`), `LayoutFile.Widgets` (`Dictionary<string, WidgetRecord>?`, JSON `widgets`; `WidgetRecord` lives in Core `Layout/` so the source-generated JSON context can carry it). Tests: round trip both, `LayoutScaler` keeps them, resolver ignores them. `dotnet build` 0 warnings.
2. `widgets/*.json` in the repo root: `clock`, `weather`, `vpn`, `dial` (knobs Metric: CPU/GPU/RAM/GPU temperature -> sets `components.dial.fraction` binding target and the label text; Warn at -> `components.dial.threshold`), `drives` (anchor bottom), `steam-covers` (Requires: Steam key and id; sets nothing), `days-since-crash`, `pending-reboot`. Content comes from today's `layouts/column-system.json`, `steam-recent.json` and the `system` source docs. A knob may set a binding: the `sets` path `components.dial.fraction=bind:hardware.cpu` form writes a binding (document it in `docs/layout-format.md` under a new "Widgets" section).
3. `Model/Widgets/*.cs` per the frozen API, TDD: `WidgetTemplateTests` (load, validation errors name the file and field), `WidgetInstanceTests` (prefixing, offset, source merge and clash, knob defaults, Remove cleans orphaned sources, SetKnob writes literal / setting / token / binding), `ArrangerTests` (top and bottom stacks, gap, unlocked skipped, order), `WidgetCatalogTests` (runtime dir overrides shipped). `ResolveTownAsync` tested with a fake `HttpMessageHandler`.
4. Regenerate `layouts/column-system.json` and `layouts/clock-disks.json` from widgets via a small test-side generator (`StarterGeneratorTests` asserts the committed files equal the generated ones, so they cannot drift); delete `layouts/starter-*.json`; update `layouts/README.md`. Link `widgets/*.json` into the designer output (`widgets\` folder) in the csproj like the starters were. Keep `StarterLayoutTests` green.
5. Commit per step. Report.

## Lane U (the screen), Opus, worktree from v1, starts at the same time coding against the frozen API (stub the four types locally in a `Widgets/` folder only if lane M has not landed when you need to run; delete the stubs before your final commit and rebase on lane M's branch, which the controller will name)

1. Fluent: `ThemeMode="System"`, NoWarn WPF0001, default window 1440x900 centred, placement restored only when on a current monitor (replaces the Phase 5 finding-15 code).
2. `MainWindow` rebuilt to spec section 4: top bar (name/display text, Undo, Redo, Apply, Settings), gallery (left), preview (centre), knobs (right), status line (bottom). Gallery cards render through `PreviewRenderer` from `WidgetTemplate.Preview(baseImage)` cropped to the widget size on a 172-wide slice of the base photo; a count badge for instances present; the `Requires` sentence when set.
3. Preview: reuse `PreviewRenderer` frames; hit-test by `WidgetInstance.Bounds`; selection is the widget; drag within the column reorders (drop position -> new index -> `Arranger.Arrange`); Delete removes; whole-wallpaper view with the column framed, toggle to column at 1:1; empty state per spec.
4. Knobs panel: controls by `KnobType` (Number -> NumberBox-like TextBox with validation, Text, Choice -> ComboBox, Color -> the existing colour editor, Drive -> ComboBox of fixed drives, Town -> TextBox that resolves on commit via `ResolveTownAsync` and shows "Leeds (53.80, -1.55)"); Remove; Details expander hosting `PropertiesPanel` + `BindingPicker` for the instance's components and the "Unlock position" switch. Nothing selected: base image picker, JPEG quality, read-only sources list with last-refresh state from `LiveSources`.
5. Apply = save + register as Phase 5 did; status line from the runtime dir. First run creates an empty layout for the display and shows the gallery; `FirstRun` deleted. `SourcesPanel`, `LayersPanel` removed from the window (files may stay if `Details` reuses them).
6. Every edit goes through `DesignerModel.Edit` so undo works. Keep `dotnet test tests/DeskWall.Designer.Tests` green; add model-level tests only where the view has logic worth it (reorder index maths, placement-on-monitor check).
7. Commit per step. Report with a screenshot of the window taken by rendering `MainWindow` off-screen is NOT required; the controller will run the app.

## Controller

Merge M, then U (U rebases on M first). Build, both suites, publish the designer to `%LOCALAPPDATA%\Programs\DeskWall`, launch it for Joe, hand over. His feedback is the review.
