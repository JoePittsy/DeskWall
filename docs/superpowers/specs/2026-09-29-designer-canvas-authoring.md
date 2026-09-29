# Designer: widget authoring on the canvas, and linked widgets

Status: brief confirmed by the owner on 2026-09-29. It comes from the `/impeccable critique` of
`src/DeskWall.Designer` (21/40; snapshot `.impeccable/critique/2026-09-29T10-47-59Z__src-deskwall-designer.md`)
and a `/impeccable shape` interview. This is the design brief, not an implementation plan.

## Why

The main window is good at placing a widget someone else built. Making a new one is not. It takes
about 40 actions, 3 modal dialogs, 2 windows, 12 typed coordinates and fields, and two pieces of
syntax (binding paths, .NET format strings). And a placed widget is a stamped copy: editing the
widget never reaches the copies already on the wallpaper (`docs/layout-format.md`, "A placed
instance is a stamped copy"). The owner's reference is Figma.

## Owner's answers

- The usual job is **tweaking what is already placed**; building a new widget is occasional. The
  main window stays arrange-first, and authoring is a mode you enter on the same canvas.
- A placed copy may override **anything** (Figma's instance model), with reset and push-to-widget.
- **The layout format may break freely.** There is one user and two machines to migrate
  (JOES-PC, JOES-XPS-17).

## 1. Job and audience

The owner, alone and expert, mostly in the main window, on a 3440x1440 ultrawide or a 1920x1200
laptop. Success means making a widget is as direct as arranging one: no typed coordinates, no
binding syntax, no second window. Changing a widget updates every placed copy that has not
overridden the change.

## 2. Outcome and proof

- "A CPU load dial with a label" takes about 8 actions, with 0 modal dialogs and 0 pieces of syntax.
- Editing a widget visibly updates its placed copies on the next render, in the designer and on the
  wallpaper.
- A re-run critique moves Recognition (heuristic 6) and Flexibility (7) from 1 to at least 3.

## 3. Direction: one canvas, three depths

The same document, with the wallpaper behind it throughout. Esc always climbs one depth.

- **Layout depth (default).** Select, move and resize placed widgets. A Layers panel on the left
  lists widgets, expandable to their parts. The gallery becomes an Insert panel you drag from onto
  the canvas.
- **Copy depth: double-click a placed widget.** Edit its parts in place, over the wallpaper,
  zoomed to fit. Every change is an override, marked in Layers and in the properties panel, with
  Reset and Push to widget.
- **Widget depth: "Edit widget", or Ctrl+Alt+K.** Edit the widget itself, isolated in place with
  the rest of the layout dimmed; every placed copy follows. New widgets start here: select parts on
  the canvas and choose Make widget (Ctrl+Alt+K), or New widget, which starts an empty frame at the
  clicked spot.
- **Binding by dragging.** The Data panel lists live values ("CPU load · 27%"). Dragging one onto
  empty canvas creates a part: a 0..1 value offers Dial or Bar, a timestamp offers Clock, anything
  else becomes Text with a format preset. Dragging one onto a part binds it. A bound property shows
  as a chip ("CPU load · 27%") with typeahead to change it. The source is added automatically. The
  picker only offers values whose type fits the property.
- **Canvas basics.** Smart guides are on by default (Alt suspends them). Ctrl+wheel zooms;
  Shift+1 fits all and Shift+2 zooms to the selection. Frames grow to fit their contents. The
  canvas renders at the current zoom, never as an upscaled bitmap.

## 4. Scope and boundaries

- **Retired:** `WidgetEditorWindow`. Its parts palette becomes the Insert panel, and its
  properties become the one contextual properties panel.
- **Kept:**
  - The daemon's renderer as the canvas.
  - One undo stack across all three depths.
  - Copy-on-write for shipped widgets: editing one forks it into `%LOCALAPPDATA%\DeskWall\widgets\`,
    and its placed copies follow the fork.
  - Apply as the only thing that paints the real wallpaper.
- **Anti-goals:** no general vector drawing (pen tool, boolean operations, free paths), and no
  plugin or scripting surface. Parts stay DeskWall's own set.

## 5. States and ranges

- **Sizes:**
  - 0–20 widgets per layout, 1–10 parts per widget, about 14 shipped widgets plus the owner's own.
  - Parts are small (13 px labels, 80 px dials), so copy and widget depths must zoom well past 1:1.
- **States to handle:**
  - A value that has not arrived yet.
  - A widget file that is missing or fails to parse: a visible broken-link state on the canvas,
    never a silent blank.
  - An override on a part the widget no longer has.
  - Editing a shipped widget, which forks it.
  - Two copies sharing one data source.

## 6. Interaction and layout

- **Columns:** left is Layers above Insert; the centre is the canvas; the right is the contextual
  properties panel.
- **Properties panel:**
  - Rows grouped into Content, Type, Colour and Arc, with human labels, not schema names.
  - Real colour pickers with a checkerboard under translucent colours.
  - Percentages instead of fractions.
  - Bind as a hover icon; Expose as knob in the row's menu.
- **Keyboard:**
  - Tab and Shift+Tab cycle siblings. Enter goes down a depth; Esc comes back up.
  - Ctrl+C, Ctrl+V, Ctrl+D and Ctrl+A work in the main window.
  - Arrow keys nudge whatever is selected. Ctrl+[ and Ctrl+] change z-order.
- **Breadcrumb:** "Layout › Hardware dial (copy)" or "Layout › Hardware dial (widget)", always
  visible.

## 7. Constraints and open decisions

- **Linking resolves in Core (the recommended option, accepted with the brief).**
  - A layout stores a placed copy as a widget key, a position and any overrides.
  - A Core expander turns copies into ordinary components before resolve, for both the daemon and
    the designer, so there is one path and what you see is what gets painted.
  - Costs:
    - The daemon also watches the widgets folders.
    - The expander needs an AOT-safe source-generated JSON context.
    - A one-off migration of the existing stamped copies on both machines.
  - Rejected alternative: the designer flattens widgets on save and the daemon stays ignorant. A
    widget edit would then reach the wallpaper only once every layout that uses it is re-saved.
- **Accessibility floor:** everything reachable by keyboard, every control named, and 4.5:1 text
  contrast. `lane/designer-quick-wins` starts this in the panels this redesign does not replace.
- **Budget:** the daemon's tick cost must not grow measurably. Measure with
  `deskwall tick --measure` before and after the expander.
- **Left to the build:**
  - The exact visuals of the chips and panels (the `design-doctrine` gates apply).
  - Whether the Insert and Data panels merge.
