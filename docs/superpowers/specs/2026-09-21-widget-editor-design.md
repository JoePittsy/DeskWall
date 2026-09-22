# Widget editor: build your own widget from parts

Date: 2026-09-21. Owner decisions taken in chat, walk-through waived ("Approach 1. Send it").
Rapid-dev phase: no reviewer agents, the owner's use is the review.

## 1. Job

> Get from "I want a widget that shows X" to a working template in the gallery, with the values
> it will draw visible live while building, without reading `docs/layout-format.md`.

Rules out: hand-editing JSON, a knob grammar on screen, anything the layout canvas does not need.

## 2. Decisions (owner, 2026-09-21)

| Question | Decision |
|---|---|
| Output | A reusable template: `%LOCALAPPDATA%\DeskWall\widgets\<key>.json`, appears in the gallery like the shipped ones. |
| Where | A separate editor window, not "select on the layout and save as widget". |
| Feel | Figma-style: a zoomed widget canvas, a parts palette, drag to place and resize, an inspector for the selected part. |
| Knobs | Simple only: "Make adjustable" on one property or one source setting -> one knob writing that one target. No composites, no `{token}` splicing. |
| Data | Built-ins (time, disks, system, hardware) with no setup, plus the user's own `http`, `command`, `file` sources with their settings; live values shown while editing. |

## 3. Shape: the same canvas over a widget-sized document

Everything on the canvas side is already document-agnostic: `DesignerModel(LayoutFile,
DisplaySignature, path)` and `PreviewView` size the canvas from the signature and zoom to fit, and
`PreviewRenderer` paints on flat grey when a layout has no `baseImage`. A template already
converts to a `LayoutFile` (`WidgetTemplate.Preview`). So the editor is:

- a `DesignerModel` whose layout is the template's sources and components in template-local
  coordinates, with no base image, and whose signature is `("widget", width, height, 100)`;
- the existing `PreviewView` + `AlignBar` + `PropertiesPanel` + `BindingPicker`, attached as
  `MainWindow` attaches them, fed by a `LiveSources` built from the document's sources;
- around them, the parts that are new: template header, parts palette, sources panel, knob list,
  save.

A 172x60 widget fits the pane at roughly 7x. Undo/redo, selection, move, resize, align, z-order,
duplicate and delete come with the model for free.

### 3.1 `WidgetDocument` (Model/Widgets)

Owns: `DesignerModel Model`, `string Name`, `string Description`, `string Anchor` ("top"/"bottom"),
`string? Requires`, `List<AdjustableTarget> Adjustables`, `string? Path` (the user-dir file being
edited, null for new), `string? EditingKey`.

- `static WidgetDocument New()` — 172x40, no sources, no components, name "New widget".
- `static WidgetDocument FromTemplate(WidgetTemplate t, string? path)` — deep copies (never shares
  the catalog's instances); simple knobs (one `sets` entry of the two simple forms, no `||` in
  default, no `:{token}`) become `Adjustables`; any other knob is kept verbatim in
  `List<Knob> PassThroughKnobs` and re-emitted unchanged so "Duplicate to mine" of a shipped
  widget with a composite knob does not lose it. The editor shows pass-through knobs read-only.
- `void Resize(int w, int h)` — via a new `DesignerModel.Resize(int, int)` that replaces the
  signature record and raises `Changed` (one undo entry). Min 8x8, max 3440x1440.
- `void FitToParts()` — bounding box of all component rects; shifts every rect so the box starts
  at (0,0); sets size to the box. One undo entry. No-op with no components.
- `ComponentDef AddPart(PartKind kind)` — `Text` (rect 0,0,120,24; text "Text"; size 16),
  `Image` (0,0,48,48; path ""), `Bar` (0,0,120,6), `Dial` (0,0,80,80; fraction 0.5 literal). Ids
  `text`, `image`, `bar`, `dial` (the model suffixes `-2`, `-3`). Each successive add offsets by
  (8,8) x (count of parts) so parts do not stack exactly. Returns the added def; the caller
  selects it. `shortcut` and `repeater` are not offered (slots and templates are layout-level
  concerns).
- Sources: `AddSource(SourceDef)`, `ReplaceSource(string name, SourceDef)`, `RemoveSource(string
  name)` through `Model.Edit`. Removing a source that components bind to is allowed; the canvas
  shows the unresolved fallback exactly as the daemon would.
- `WidgetTemplate ToTemplate()` — current state as a template. Knob defaults are computed **at
  this moment** from the target's current value (see 3.4), never stored separately.
- `string Key` — slug of `Name`: lower-case, `[a-z0-9]+` runs joined by `-`, trimmed; empty ->
  `widget`.

### 3.2 Save (`WidgetTemplateWriter`)

`static string ToJson(WidgetTemplate)` builds the file with the Core encoder for the parts Core
owns: serialise a `LayoutFile { Sources, Components }` with `LayoutFile.ToJson()`, parse to
`JsonNode`, lift its `sources` and `components` arrays into a `JsonObject` with `version: 1`,
`name`, `description`, `size`, `anchor` (omitted when "top"), `requires` (omitted when null),
`knobs`. Writes with `WriteIndented`. Round-trip contract: `WidgetTemplate.Load(saved)` has equal
name/description/size/anchor/requires, component ids/types/rects, source names/types/settings,
and knob id/label/type/default/sets/choices/min/max.

`WidgetDocument.Save()` writes `WidgetCatalog.UserDir\<Key>.json` (creating the dir). Rules:
- a **new** document, or a renamed one, whose key matches a **shipped** template's key is refused
  with "A shipped widget is already called '<name>'. Pick another name." (a user file would
  silently override the shipped one in the catalog);
- a renamed document deletes its previous file after writing the new one;
- the window title shows `<Name> — Widget editor` with `*` while dirty (`Model.Dirty`, plus
  header/knob edits tracked by comparing `ToJson()` to the last saved text).

Placed instances on layouts are stamped copies. Saving a template never changes them; the
gallery card reflects the new version and new placements use it. Say so in `docs/layout-format.md`.

### 3.3 Sources panel (new, Views/SourcesPanel)

A list of the document's sources (`name · type`), a "+ Source" button, and for the selected
source: its form and its live value tree (`ValueTreeView.Populate`, "Refresh" calls
`LiveSources.RefreshNowAsync`). Clicking a value path copies it to the clipboard and shows it in
the status line — binding itself stays where it is, in the properties panel's binding picker,
which already lists every source.

"+ Source" offers seven rows: **time, disks, system, hardware** (added at once with their
canonical name, no form, one instance each), and **http, command, file** (opens the form with
the type's defaults). Forms are a data table, not seven hand-built panels (`SourceForms`):

| Type | Fields (setting key, editor, default) |
|---|---|
| http | name (text, `http`), `url` (text, required), `every` s (number, 600), `parse` (choice auto/json/text), `timeout` s (number, 10) |
| command | name (`command`), `command` (text, required), `args` (text), `workingDir` (text), `every` (600), `timeout` (10), `parse` (auto/json/text) |
| file | name (`file`), `path` (text, required), `every` (30), `parse` (auto/json/text/rss) |

`auto` means the key is omitted. Header lines and `unixTimeFields` are not in the form; they
survive in `Settings` if a duplicated shipped widget had them. Name must be unique in the
document and `[a-z][a-z0-9]*`. Every setting row has the same "Make adjustable" toggle as a
property row (3.4).

The live tree is the same `LiveSources` the properties panel uses (one instance per document,
rebuilt when sources change, disposed with the window), so a bound value on the canvas and the
tree agree.

### 3.4 Knobs: "Make adjustable"

`AdjustableTarget(string ComponentId, string Property)` or `(string SourceName, string
SettingKey)`; `Label` (editable, default: the property/setting name, disambiguated with the part
id when two targets share a name: "Text (text-2)"); for number targets, editable `Min`/`Max`
(nullable). Knob `Id` = slug of the label, unique.

Mapping from `PropertySchema.Editor` to `KnobType` at `ToTemplate()`: Text/Font/Path -> `text`,
Number/AutoNumber -> `number`, Color -> `color`, Enum -> `choice` with the prop's `Choices`,
Binding -> **not offered** (the toggle is hidden). A property whose current value is a **binding**
cannot be made adjustable either (a knob writes literals; hidden). Source settings are `text`
except `every`/`timeout` (`number`). `Default` = the target's current literal at save time, so
editing the value after exposing it moves the default with it and there is nothing to keep in sync.
`Sets` = `["components.<id>.<property lower-case>"]` or `["sources.<name>.settings.<key>"]`.

Entry point: `PropertiesPanel` gets an optional `Func<string componentId, string prop,
bool>? IsAdjustable` and `Action<string, string>? ToggleAdjustable`; when they are set (only the
widget editor sets them) each eligible row shows a small toggle at its right edge, filled when
the target is already a knob. The layout window never sets them, so it does not change. The knob
list panel (right column, under the properties) lists the adjustables with label, target,
min/max for numbers, and a remove button, plus pass-through knobs greyed.

Existing templates: `WidgetInstance.SetKnob` already handles both simple `sets` forms; nothing
changes in instantiation.

### 3.5 The window (Views/WidgetEditorWindow)

Low-frequency surface: warmth allowed, but the control count still matters. Layout:

```
[ Name ________ ] [ Description ______________ ] [ W ][ H ] [Fit to parts] [Anchor v] [ Save ]
+----------+------------------------------------------------+-----------------------+
| Parts    |                                                | Properties            |
|  Text    |                                                |  (existing panel,     |
|  Image   |          canvas (existing PreviewView,         |   + adjustable toggle)|
|  Bar     |           AlignBar above it, fit zoom,         |                       |
|  Dial    |           flat grey, no photo)                 |-----------------------|
|----------|                                                | Adjustable            |
| Sources  |                                                |  label · target  [x]  |
|  name·ty |                                                |  min [ ] max [ ]      |
|  + Source|                                                |  (pass-through greyed)|
|  form    |                                                |                       |
|  live    |                                                |                       |
+----------+------------------------------------------------+-----------------------+
 status line: last save / refusal message / copied path
```

Gate 3 table (without this the user cannot ...): Name — name the file and gallery entry.
Description — the gallery card's body text (the card is name plus description since the
pictures went), so it stays. W/H — set what the arranger reserves. Fit to parts — stop typing sizes.
Anchor — a bottom-anchored widget stacks from the bottom. Save — persist. Parts x4 — create.
Sources list/+/form/live — feed and see data. Properties — edit the part. Adjustable list —
see and remove knobs. Nothing else. No toolbar, no zoom control beyond what `PreviewView`
already has, no colour theme controls.

One editor window at a time; a second "New widget"/"Edit" focuses the open one (it asks to
discard unsaved changes first, the same dialog the main window uses). Closing with unsaved
changes asks. The window is owned by the main window, not modal.

Escape/Delete/arrows/Ctrl+Z/Ctrl+D behave as on the layout canvas (they are `PreviewView`'s).

### 3.6 Gallery hooks (Views/GalleryPanel, MainWindow)

- A "New widget" row at the foot of the gallery list.
- Right-click on a name: **Edit** (user-dir templates), **Duplicate to mine** (shipped: opens the
  editor on a copy named "<Name> copy", unsaved), **Delete** (user-dir: confirm, delete file,
  reload). Determined by the template's path: `WidgetTemplate` gains `string? Path` set by
  `WidgetCatalog.Load`.
- `MainWindow` reloads the catalog (`Gallery.Load`, `Knobs.Attach`, `RebuildLiveSources`) when
  the editor saves or a template is deleted (`WidgetEditorWindow.Saved` event).

## 4. Testing, thin by intent

Model tests only, `tests/DeskWall.Designer.Tests/Widgets/`:
- `WidgetDocumentTests`: New defaults; FromTemplate of every shipped widget then ToTemplate equals
  it (names, sizes, component ids/rects, sources, knobs — composites pass through unchanged);
  AddPart ids and offsets; Resize bounds; FitToParts shifts and sizes; Key slug cases ("GPU °C" ->
  "gpu-c", "" -> "widget").
- `AdjustableTests`: Editor->KnobType mapping; label disambiguation; default taken at
  ToTemplate time after a later edit; bound property refused; source setting knob path; removing
  the target component/source drops the adjustable.
- `WidgetTemplateWriterTests`: ToJson -> `WidgetTemplate.Load` round trip for each shipped
  widget; `anchor`/`requires` omitted when default/null; shipped-key refusal; rename deletes the
  old file (under `DESKWALL_HOME`).
- `SourceFormsTests`: each type's fields; `auto` omits the key; defaults produce a `SourceDef`
  the corresponding `ISource` factory accepts.

Build 0 warnings, Core 254 and Designer suites green. Everything else is Joe using it.

## 5. Out of scope now

Composite knobs, `{token}` knobs, `town`/`drive` knob types from the editor; `shortcut` and
`repeater` parts; the photo as editor backdrop; promoting a selection on the layout into a
widget (fits on top of this later: same `WidgetDocument`, different constructor); updating
already-placed instances when a template changes; `rss` source form (the `file` form covers RSS
files, and the shipped headline widget covers feeds).

## 6. Docs

`docs/layout-format.md` "Widgets": one paragraph on user templates, the editor, and that
instances are copies. `README.md` designer paragraph: one sentence. `layouts/README.md` if it lists
where templates come from.
