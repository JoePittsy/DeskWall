# Plan: every out-of-the-box widget is defined, and editable

Owner's ask (2026-09-21): "I want all of the OTTB widgets to be defined and editable."

Two things are true today and this plan fixes both:

1. **Not editable.** A shipped widget's card offers only "Duplicate to mine". Edit and Delete are
   hidden because `WidgetCatalog.IsUserTemplate` is false for anything in the shipped directory,
   and `WidgetTemplateWriter.Save` refuses a key a shipped widget already owns.
2. **Not all defined.** `layouts/clock-disks.json` and `layouts/column-system.json` are generated
   from widget templates and every component in them belongs to an instance.
   `layouts/steam-recent.json` is still hand-authored: its three components (`clock`, `games`,
   `drives`) are loose, although shipped widgets `clock`, `steam-covers` and `drives` describe
   exactly those three things.

Base: the integration branch after `lane/drive-knob` and `lane/source-and-format-fixes` merge.
Rapid-dev: no reviewer agents, TDD for the model changes, build 0 warnings.

## Part 1: editing a shipped widget (copy on write)

The shipped directory sits beside the exe and is overwritten by every publish, so edits must not
be written there. `WidgetCatalog.Load(shippedDir, userDir)` already prefers a user template with
the same key **and keeps the position the key was first seen at**, which is exactly the mechanism
this needs: editing a shipped widget saves a user file under the same key, and it takes over.

- **Gallery menu on a shipped card:** Edit (new), Duplicate to mine (as now). Editing opens the
  editor on the shipped template with `EditingKey` set to that key and `Path` null, so Save writes
  `<userDir>\<key>.json`.
- **The save refusal changes meaning.** It exists so a *new* widget cannot silently shadow a
  shipped one. It must still fire for that, and must not fire when the document is deliberately
  editing that key (`EditingKey == Key`). Keep the refusal's sentence for the first case.
- **A way back.** A card whose key is shipped *and* overridden by a user file offers "Reset to the
  out-of-the-box version": confirm, delete the user file, reload the catalog. This is the existing
  Delete plumbing with a different label and confirmation text. A pure user widget keeps "Delete".
- **Say which is which.** A card backed by a user override of a shipped key shows a quiet "edited"
  marker, so the gallery does not lie about what a widget is. One word, in the caption style
  already on the card; no new colour.
- **Placed instances do not change.** They are stamped copies, as the docs already say. Note it in
  the Reset confirmation so nobody expects their layout to revert too.

`WidgetTemplate` needs to know both facts (its file is a user file; its key is also shipped).
`WidgetCatalog.Load` is the only place that knows, so let it record them on the template
(`IsUserTemplate` exists; add whether the key also exists in an earlier directory) rather than
having the gallery re-scan directories.

Tests (`tests/DeskWall.Designer.Tests/Widgets/`): a user file overriding a shipped key is flagged
as both; `Save` allowed when `EditingKey` equals the key, refused for a new document with the same
key; reset deletes only the user file and the shipped template returns from a reload; a shipped
key with no override is not flagged as edited.

## Part 2: `steam-recent.json` defined from widgets

`StarterGenerator` (in the test project) builds the other two starters from the catalog, and
`StarterGeneratorTests.Committed_Starter_Equals_Generated` pins the committed files to it. Add
`SteamRecent(catalog)` building the same layout from `clock`, `steam-covers` and `drives`, add the
file to the theory, and commit the regenerated `layouts/steam-recent.json`.

- Keep the on-screen result the same: right margin at x 3220, width 172, the same three bands
  (clock at the top, covers in the middle, drives at the bottom). Small differences the templates
  bring (an `effectRadius`, a `thresholdFill`) are accepted and land in the committed file; they
  are the widgets' own current settings and are the point of defining it from them.
- The layout keeps its `baseImage`, `baseFit`, `encode` and `jpegQuality`, and the Steam source
  still needs secrets. `requires` on `steam-covers` already says so.
- `layouts/README.md` mentions the starters; check whether it needs a line.

Test: the new theory row passes, and a test that `steam-recent.json` has no component without a
`widget` field (the property this plan is really about, and it would have caught this).

## Out of scope

Editing a shipped widget *in place* in the install directory (a publish would wipe it); updating
already-placed instances when their template changes (they are copies, by design); a diff between
a user override and the shipped original.
