# Lane report: Hearth feeds DeskWall (recently played, no Steam key)

Worktree `D:\Source\Personal\DeskWall-hearth`, branch `spike/hearth-feed`. Covers
`docs/superpowers/plans/2026-09-30-lane-hearth.md` parts 2 and 3 in full. Part 1 (Hearth's own
code) could not be built here -- the Hearth repo is on JOES-PC only, not this machine (checked
both `C:\Users\JosephPitts\source\repos\Hearth` and `D:\Source\Personal\Hearth`: neither exists)
-- and is instead a precise implementation spec at
`docs/superpowers/plans/2026-09-30-hearth-feed-spec.md`, written to be handed to an agent on
JOES-PC on its own.

## What changed, and why it stayed inside the lane's boundary

Touched only `docs/sources.md`, `layouts/README.md`, a **new** directory `layouts/widgets/`, and
two new plan documents -- never `widgets/`, `src/`, or an existing `layouts/*.json`, which another
lane was rewriting on `main` at the same time.

- **`src/DeskWall.Core/Sources/FileSource.cs` already watches the file.** The plan's "the one Core
  touch allowed" (add a watcher if the `file` source doesn't have one) turned out to be
  unnecessary: it already has a debounced `FileSystemWatcher` (300 ms) plus an mtime re-check, and
  `docs/sources.md`'s `## file` section already documented both and already named Hearth as the
  intended producer. **No Core code was changed in this lane.**
- **`docs/sources.md`**: added "Recipe: Hearth recently-played games" under `## file` -- the exact
  source declaration (`runtime:feeds/hearth-recent.json`, `parse: json`), what it publishes, and
  a pointer to the feed spec and the widget rather than re-describing either.
- **`layouts/widgets/recent-games.json`** (new file, new directory): the widget. A `file` source
  plus a horizontal `repeater` of 120x180 covers (8 px radius) each with a `shortcut` over it,
  slots 8 upward. One knob, **Count** (choice, default 4, 1..8): it sets the repeater's own width
  per choice (`components.games.w`, a plain pixel number for each of 1..8 items) rather than
  slicing the bound list -- there is no list-slicing in the binding language (`Binding.cs` is a
  path plus an optional format string, nothing else), so the repeater's own "overflow stops, it
  does not wrap or shrink" rule (`docs/layout-format.md` "Repeater semantics") is what actually
  trims the extra items. A `take`-style binding filter would replace this: #41.
- **`layouts/README.md`**: a new "Recent games (Hearth)" section documenting the widget, why it
  currently lives outside `widgets/`, and how to try it before the lanes merge.
- **`docs/superpowers/plans/2026-09-30-hearth-feed-spec.md`** (new): part 1, written for JOES-PC.
- This report (new).

## Proof (part 3)

1. `dotnet build DeskWall.slnx -c Release` -- clean, 0 warnings, 0 errors, before and after every
   change in this lane (none of the changes touch compiled code, so this was really confirming the
   baseline rather than re-checking after each edit).
2. A scratch `DESKWALL_HOME` (`$env:TEMP\dw-hearth-lane`, not the real runtime dir):
   - `covers\cover0..3.png`: four real 240x360 PNGs (System.Drawing, solid colour plus the game's
     name), not placeholders -- the point was to prove the `image` component actually decodes and
     draws a file from disk, not that the art looks like a game cover.
   - `feeds\hearth-recent.json`: a hand-written fake feed, 4 entries, the exact schema from the
     spec (`id`, `name`, `lastPlayed`, `cover`, `installed`, `playtimeMinutes`, `launch`), covers
     pointing at the PNGs above, most-recent-first.
   - `widgets\recent-games.json`: the widget file copied in, exactly as it sits in
     `layouts/widgets/` in this repo (proving the deliverable file itself, not a rewritten copy).
   - A scratch version-2 layout with one `copies` entry, `"widget": "recent-games"`.
3. `deskwall.exe --home <scratch> tick --layout <scratch layout> --force --measure --no-apply
   --no-shortcuts`: succeeded, `redrawn 4`, ~430 ms total (well inside budget for a one-shot debug
   tick; no attempt made to read this as the real per-minute tick cost, which is a resident-daemon
   measurement, not a `--no-apply` scratch one).
4. Cropped the region of the resulting `deskwall.jpg` and looked at it directly: four rounded
   cover cards, correctly ordered (Hollow Knight, Celeste, Hades, Stardew Valley -- matching the
   feed's `lastPlayed` order), 8 px gaps, no overlap, no missing-image fallback plate (the covers
   really decoded).
5. Re-ran the same tick with the copy's `knobs` set to `{ "count": "2||248" }`: `redrawn 2`, and
   the cropped frame shows exactly the first two covers (Hollow Knight, Celeste) with nothing
   drawn where the other two were -- confirms the knob's overflow-stop mechanism actually narrows
   the visible set rather than merely relabelling a fixed four.
6. Scratch home deleted afterward; nothing written to the real `%LOCALAPPDATA%\DeskWall` or the
   real desktop at any point (`--no-apply --no-shortcuts` throughout, and `--home` pointed every
   run at the scratch folder).

No `deskwall verify` run in this lane: `verify` measures **desktop icon slot padding** against the
real desktop (spec says it saves a screenshot and reads live icon positions), which is exactly the
kind of check CLAUDE.md says must never run against a scratch home dressed up as real -- there is
no "scratch desktop" to check it against, only the one JOES-PC actually has. The exact run for
JOES-PC, once Hearth exists and has written a real feed, is below.

## What to install on JOES-PC

**Hearth itself** (part 1, not built in this lane): build, install and first-write steps are in #35.

**DeskWall's side** (already built in this lane, not yet on `main`):

1. Ship `recent-games.json` in `widgets/` and drop the "for now" note: #36.
2. Until then, on JOES-PC: copy `layouts/widgets/recent-games.json` to
   `%LOCALAPPDATA%\DeskWall\widgets\recent-games.json` (the ordinary user-widget-override
   location, `docs/layout-format.md` "Where widget files come from") so the daemon and designer
   both pick it up without a rebuild.
3. Add a `copies` entry for it (`"widget": "recent-games"`) to whatever layout JOES-PC is running
   -- most likely a slot next to or instead of `steam-covers` in `column-system.json`, since both
   claim shortcut slots 8 upward and a layout must not use both at once (`layouts/README.md`
   "Shortcut slots": a duplicate slot is a layout error).
4. `deskwall layouts set <path to that layout>`.

## The exact `deskwall verify` run, once the above is done

```powershell
deskwall tick --force --measure          # picks up the registered layout; confirm it renders
deskwall verify
```

`verify` prints `left pad N, bottom pad N` per shortcut slot (wants 5/5 -- `layouts/README.md`,
CLAUDE.md "Verifying a v1 change") and exits non-zero on a mismatch. With the default Count of 4,
expect four lines for slots 8..11 (or fewer, in the order the widget's covers occupy them, if a
game is missing a cover and its shortcut has an empty target -- `LayoutResolver.Emit` places no
icon for a blank target, so that slot simply does not appear in the report rather than reporting
a mismatch for it).
