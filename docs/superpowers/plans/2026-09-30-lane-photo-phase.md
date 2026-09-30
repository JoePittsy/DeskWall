# Lane: a photo per phase of the day

Worktree `D:\Source\Personal\DeskWall-photo`, branch `spike/photo-phase`. Read `CLAUDE.md`,
`docs/layout-format.md`, `src/DeskWall.Core/Render/BaseCache.cs`, `Resolve/LayoutResolver.cs`,
`Tick/TickRunner.cs` (base handling only) and the loud report
(`docs/superpowers/plans/2026-09-30-spike-loud-report.md`). Other lanes run in sibling worktrees;
you own `baseImage` handling in Layout/Resolve/Render/BaseCache and one designer row. No publish,
no touching `%LOCALAPPDATA%\DeskWall`; scratch `DESKWALL_HOME`. Full permissions, never stop to
ask; plain-English commits; build clean.

## 1. Bindable `baseImage`
`baseImage` becomes a `PropertyValue` (literal path or a binding), resolved each tick like any
other property; `BaseCache` keys on the *resolved* path + mtime, keeps up to 4 decoded bases so
swapping between phases costs no re-decode after the first day, and evicts oldest. A binding that
resolves to a missing file keeps the last good base and logs once. `runtime:` expansion applies.
The sky-grade overlays should not be needed when the photo itself changes: document that.

## 2. Four photos of the ridge
There is one photo, `C:\Users\JosephPitts\Downloads\5168918.jpg` (dusk). Produce dawn, day and
night variants **from it** with a script (`scripts/phase-photos.ps1`, System.Drawing or
ImageMagick if installed): colour-grade per phase (night: cool, darker, stars are the layout's
job; dawn: rose lift on the sky and warm rim on the peaks; day: neutral, brighter), written to
`%DESKWALL_HOME%\assets\alpine\ridge-<phase>.jpg` at 3440x1440, quality 92. These are honest
grades of the same frame, so the ridge trace still lands.

## 3. Wire it
`baseImage: { "bind": "time.phase | \"?night=runtime:assets/alpine/ridge-night.jpg,dawn=...,day=...,dusk=runtime:assets/alpine/ridge-dusk.jpg\"" }`
(`time.phase` exists on branch `spike/presence-sources`; if it has not merged, add the same
`phase` field to `TimeSource` here with fixed 06:30/19:30 defaults and note the duplicate for the
merge). A copy of the loud layout, `layouts/alpine-vision-photos.json`, using it, with the sky
grade layers' alpha halved (the photo now carries the mood).

## 4. Designer
The Layout panel's base image row gains the bind chip like any other property (it is a
`PropertyValue` now); the preview swaps bases as the live tree changes.

## 5. Prove and report
Gallery via `scripts/gallery.ps1` against the new layout (midnight, dawn, noon, dusk at least);
`tick --repeat 4 --measure` showing base swap cost warm and cold. Report at
`docs/superpowers/plans/2026-09-30-lane-photo-report.md`. Update `docs/layout-format.md`.
