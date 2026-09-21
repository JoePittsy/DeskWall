# DeskWall designer, take two: a widget picker

**Date:** 2026-09-21. **Branch:** `v1`. **Owner:** Joe. **Status:** approach A approved; owner asked
for a fast, testable build over review ceremony ("I want this done quickly so I can test it and
provide feedback"). This spec is deliberately short: it fixes the contracts, the owner's use is
the review.

## 1. Why

Joe used the Phase 5 designer for the first time on 2026-09-21: "it looks like dogshit, it feels
like dogshit". Every workflow failed: no way to add a component (the model has `Add`, no UI calls
it), binding by hand, coordinates on a 3440-wide canvas, nothing discoverable. Register chosen:
**widget picker**, like adding a widget to a phone home screen. Technology: **WPF + .NET's Fluent
theme** (`ThemeMode`), keeping the project, `DesignerModel`, undo and the Core-backed
`PreviewRenderer`; the views are rebuilt.

## 2. Doctrine for the designer UI

Gate 0: *get a widget onto the column and looking right in under a minute, without seeing a
coordinate or a binding.* Low-frequency surface: warmth and real previews are allowed; the
element budget still holds. Top bar at most six controls. A widget exposes at most five knobs.
Gallery cards are real renders of the widget on a crop of the base photo, never icons. Nothing
in the default view shows an id, a rect, a binding string or JSON. Empty state (no widgets) is a
designed screen, not a blank canvas. Gate 7: the ugly cases to look at before calling it done:
a layout with zero widgets, one, and every widget at once; a widget whose source has failed
(weather offline); the 1920x1200 RDP display; light and dark theme.

## 3. Widget templates

`widgets/<name>.json` in the repo (shipped beside the designer exe like the starters, and also
read from `%LOCALAPPDATA%\DeskWall\widgets\` so a user can add their own):

```json
{
  "version": 1,
  "name": "Weather",
  "description": "Temperature and sky for a town, from Open-Meteo. No key needed.",
  "size": [172, 60],
  "sources": [
    { "name": "weather", "type": "http", "every": 900, "settings": {
        "url": "https://api.open-meteo.com/v1/forecast?latitude={lat}&longitude={lon}&current=temperature_2m,weather_code,is_day&timezone=auto" } }
  ],
  "components": [
    { "type": "text", "id": "temp", "rect": [0, 0, 108, 52], "text": { "bind": "weather.json.current.temperature_2m | \"{0:N0}°\"" }, "font": "Segoe UI Light", "size": 40, "weight": 300, "align": "right" },
    { "type": "image", "id": "sky", "rect": [116, 0, 56, 56], "fit": "contain", "source": { "bind": "weather.json.current.weather_code | \"runtime:assets/weather/{0}.png\"" } }
  ],
  "knobs": [
    { "id": "town", "label": "Town", "type": "town", "default": "Leeds", "sets": ["sources.weather.settings.url:{lat}", "sources.weather.settings.url:{lon}"] }
  ]
}
```

- `components` are in template coordinates (0,0 top-left, `size` is the widget's footprint).
- `knobs[].type`: `number`, `text`, `choice` (with `choices`), `color`, `drive` (a drive letter
  present on the machine), `town` (typed as a town, resolved once to `{lat}`/`{lon}` through
  `https://geocoding-api.open-meteo.com/v1/search?name=<town>&count=1`; the layout stores the
  coordinates and the town name in the widget instance's `knobs` record so it can be shown back).
- `knobs[].sets`: paths written when the knob changes. Grammar: `components.<id>.<property>`
  (writes a literal), `sources.<name>.settings.<key>` (writes the setting), and the
  `...:{token}` form that substitutes a token inside the current string value.
- **Instantiation** (`WidgetInstance.Create(template, instanceId, origin)`): copy components
  with ids `<instanceId>.<id>`, rects offset by `origin`, `widget` = instanceId on each; add each
  source the layout does not already have by name (same name + same type = reuse; same name,
  different type = suffix the name and rewrite the instance's bindings); apply knob defaults.
  The layout gets a `widgets` record: `{ "<instanceId>": { "template": "weather", "knobs": { "town": "Leeds" } } }`
  so knobs can be re-edited and the instance re-created after a template update.
- `ComponentDef.Widget` (`string?`) is the one Core change; the daemon ignores it. `LayoutFile`
  gains an optional `Widgets` dictionary the daemon also ignores.

Shipped widgets, each a real render in the gallery: Clock; Weather; VPN (Tailscale); Hardware
dial (knob `Metric`: CPU, GPU, RAM, GPU temperature; knob `Warn at`); Drives (all fixed drives,
as today's repeater); Steam covers (needs `steamKey` and `steamId` secrets; the card says so and
the knobs panel offers the two secret fields inline); Days since crash; Pending reboot flag.
`column-system.json` and `clock-disks.json` are regenerated from widgets so the repo has one
source of truth; the old `starter-*.json` files are deleted.

## 4. The screen

One window, Fluent, follows the system theme. Default size 1440x900 centred; the saved placement
is restored only when it lies on a current monitor.

- **Top bar:** layout name and display (read-only text), Undo, Redo, **Apply** (primary),
  Settings. That is five.
- **Left, gallery:** a scrolling list of widget cards, each a real render of the template at
  1:1 on a crop of the base photo, the name, the one-line description, and an Add button. Widgets
  already on the column show a count badge. Cards for widgets whose requirement is missing (no
  NVIDIA GPU, no Tailscale, no Steam secrets) still render, with the requirement stated under the
  description.
- **Centre, preview:** the composed frame from `PreviewRenderer`, fit to the available height,
  showing the whole wallpaper by default with the right-hand column framed; a toggle zooms to
  the column at 1:1. Widgets are selectable and draggable as units. Dragging within the column
  reorders; the arranger recomputes positions (see below) on drop. Delete removes the widget.
  Empty layout: the column shows a soft dashed frame and the sentence "Add a widget from the
  list to start."
- **Right, knobs:** for the selected widget: its name, its knobs as Fluent controls, Remove, and
  a "Details" expander that reveals the Phase 5 properties panel and binding picker for the
  widget's components (this is the only place ids, rects and bindings appear). Nothing selected:
  the layout's own knobs: base image (file picker), JPEG quality, and the sources list read-only
  with each source's last-refresh state.
- **Bottom status line:** "Applied 09:41 · daemon running" or the daemon's last error, read from
  the runtime dir exactly as the Phase 5 settings page did.

**Arranger.** The column is a vertical stack at x 3220, width 172, starting at y 40, gap 16.
Widgets have an `anchor` of `top` (default) or `bottom` (Drives is bottom-anchored by its
template); the arranger stacks top-anchored widgets downwards and bottom-anchored ones upwards
from y 1400 and writes plain rects into the components. Free placement is off by default; the
Details expander has an "Unlock position" switch per widget that exempts it from the arranger.
Layouts written by the arranger are ordinary layouts; the daemon knows nothing of it.

**Sources and secrets.** Managed automatically: added by instantiation, removed when the last
component bound to them goes. A widget that needs a secret shows the fields in its knobs panel
and writes `secrets.json` through the existing `Secrets` API.

**Apply** saves the layout to the display's path and registers it, as today; the daemon's watcher
repaints within two seconds. First run: an empty layout for this display is created and the
gallery is the first thing seen; no starter picker.

## 5. What is kept, what goes

Kept: `DesignerModel` (undo, selection, Edit), `PreviewRenderer`, `LiveSources`, `Settings`,
`SecretsEditor`, `PropertySchema`, `BindingPicker` and `PropertiesPanel` (inside Details),
`ShellState.LayoutPathFor`, `CopyAssets`. Rebuilt: `MainWindow`, `CanvasView` (becomes the
preview with widget-level selection and reorder drag), `LayersPanel` and `SourcesPanel` (gone
from the default view; sources are read-only in the right panel), `FirstRun` (gone).

## 6. Testing, thin by intent

Model tests, no UI automation: template load and validation; instantiation (id prefixing,
offsetting, source merge, knob defaults, `Widgets` record); knob `sets` paths; arranger
(top/bottom anchors, gap, unlocked widgets exempt); removal cleans up orphaned sources;
regenerated starters equal the committed files. Core: `Widget` field round-trips JSON and
survives `LayoutScaler`. Build 0 warnings. Everything else is Joe using it.

## 7. Out of scope now

Grouping loose components into a widget; multi-column layouts; widget marketplace; per-widget
theming; a `stack` container in the daemon.
