# Plan: widget editor (build your own widget from parts)

Spec: `docs/superpowers/specs/2026-09-21-widget-editor-design.md`. Read it first; section numbers
below refer to it. Rapid-dev phase: one implementer, no reviewer agents, the controller gates on
build + tests + a diff skim and ships a build for the owner to try.

Base: `integration/2026-09-21-placement-and-text` @ ca29f5a or later. Work in a worktree branch
`lane/widget-editor`. Commit per task. TDD for the model tasks (test first, watch it fail, make it
pass); the window task is verified by build + a launch against a scratch `DESKWALL_HOME`.

Rules for the lane (from CLAUDE.md and project memory):
- Never launch the designer or `deskwall` against the real runtime dir. Set `DESKWALL_HOME` to a
  scratch folder for any manual launch; copy `layouts/column-system.json` there if a layout is
  needed. The live daemon is running; do not stop it.
- `.ps1`/`.cs` ASCII only unless the file already has a BOM. No em dashes in code or docs.
- `dotnet build` must stay at 0 warnings (AOT analyzers run under JIT too; `TreatWarningsAsErrors`).
- Tests run under `DESKWALL_HOME` (AssemblyInfo sets it). A test that touches
  `%LOCALAPPDATA%\DeskWall` is a defect.
- Do not spawn agents.

## Task 1: `WidgetDocument` + `DesignerModel.Resize` (spec 3.1)

Files: `src/DeskWall.Designer/Model/Widgets/WidgetDocument.cs`, `Model/DesignerModel.cs`
(`Resize`), `Model/Widgets/WidgetTemplate.cs` (`string? Path` on the template, set by
`WidgetCatalog.Load`). Tests: `tests/DeskWall.Designer.Tests/Widgets/WidgetDocumentTests.cs`.

- `New()`, `FromTemplate(template, path)`, `ToTemplate()`, `Resize`, `FitToParts`, `AddPart`,
  `AddSource/ReplaceSource/RemoveSource`, `Key`, `Adjustables` + `PassThroughKnobs` (the knob
  classification rule is in spec 3.1: one `sets` entry, one of the two simple forms, no `||`,
  no `:{`).
- `DesignerModel.Resize(int w, int h)`: replaces `Signature` with `Signature with { Width, Height }`
  inside `Edit("Resize canvas", ...)` so it is undoable; `Signature` needs a private setter and
  the undo stack must capture it. Look at how `Edit`/`Undo` snapshot the layout (JSON) and add the
  size to the snapshot rather than inventing a parallel stack.
- Round-trip test over every file in `widgets/` (use `TestRepo` to find the repo).

## Task 2: adjustables + `SourceForms` + `WidgetTemplateWriter` (spec 3.2, 3.3 table, 3.4)

Files: `Model/Widgets/Adjustable.cs` (target record, label disambiguation, Editor->KnobType
mapping, `ToKnob(document)`), `Model/Widgets/SourceForms.cs` (the per-type field table,
`ToSourceDef(type, values)`, `FromSourceDef`), `Model/Widgets/WidgetTemplateWriter.cs` (`ToJson`,
`Save(document)` with the shipped-key refusal and rename-deletes-old-file). Tests:
`AdjustableTests.cs`, `SourceFormsTests.cs`, `WidgetTemplateWriterTests.cs`.

- The writer goes through `LayoutFile.ToJson()` + `JsonNode` (spec 3.2); do not write a second
  component serialiser.
- Shipped keys come from `WidgetCatalog.Load(WidgetCatalog.ShippedDir)`; tests pass an explicit
  shipped list so they do not depend on the exe's folder.
- `SourceFormsTests`: for each type, `ToSourceDef` with defaults must be accepted by the Core
  source factory the designer uses (`LiveSources` constructs sources; find its factory and call
  it) without throwing.

## Task 3: the window (spec 3.3 panel, 3.4 toggle, 3.5)

Files: `Views/WidgetEditorWindow.xaml(.cs)`, `Views/SourcesPanel.xaml(.cs)`,
`Views/AdjustablesPanel.xaml(.cs)` (or inside the window if under ~150 lines),
`Views/PropertiesPanel.xaml.cs` (the optional `IsAdjustable`/`ToggleAdjustable` hooks and the
toggle at the row's right edge, shown only when hooks are set).

- Attach `PreviewView`, `AlignBar`, `PropertiesPanel` exactly as `MainWindow` does (copy the
  wiring, including `PreviewRenderer` creation and `LiveSources` rebuild on `Changed`).
- Flat grey canvas: the document's layout has no `BaseImage`; confirm `PreviewRenderer` draws
  grey and does not log an error each render for a missing base.
- Header fields write through the document; W/H commit on Enter or focus loss; clamp per spec.
- Status line at the foot for save result / refusal / "copied <path>".
- Window title `<Name> - Widget editor`, `*` when dirty. Unsaved-changes prompt on close and on a
  second open request (reuse the main window's dialog text).
- Use the Fluent theme resources the rest of the designer uses; no new colours or fonts. Count the
  controls against spec 3.5 before you finish; anything not in the Gate 3 table goes.

## Task 4: gallery hooks, docs, hand-off (spec 3.6, 6)

Files: `Views/GalleryPanel.xaml(.cs)` ("New widget" row, context menu Edit / Duplicate to mine /
Delete by `Template.Path`), `Views/MainWindow.xaml.cs` (`OpenWidgetEditor(WidgetDocument)`, single
editor instance, catalog reload on `Saved`/delete: `_catalog` becomes reassignable, then
`Gallery.Load`, `Knobs.Attach`, `RebuildLiveSources`, `Gallery.SetCounts`), `docs/layout-format.md`
(Widgets section paragraph), `README.md` (one sentence in the designer paragraph).

Manual check (scratch `DESKWALL_HOME`): New widget -> add Text + Dial -> add `hardware` source ->
bind the dial's fraction to `hardware.cpu` via the binding picker -> make the text's Text
adjustable -> Fit to parts -> Save as "My dial" -> it appears in the gallery -> add it to the
layout -> the knobs panel shows "Text". Then Edit it, rename to "My dial 2", save, the old file is
gone. Then Duplicate the shipped Weather widget, save, and confirm its Town knob is intact in the
saved file. Write what you saw, and any deviation from the spec, in
`.superpowers/sdd/2026-09-21-widget-editor/lane-report.md`.

## Hand-off to the controller

Branch `lane/widget-editor` with 4+ commits, 0 build warnings, both test suites green, lane
report written. The controller merges, publishes the designer to `%LOCALAPPDATA%\Programs\DeskWall`
and asks the owner to try it.
