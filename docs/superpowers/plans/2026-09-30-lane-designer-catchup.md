# Lane: the designer catches up with the daemon

Status: not merged -- `spike/designer-catchup` stopped at 4 commits plus WIP; tracked in #29.

Worktree `D:\Source\Personal\DeskWall-designer`, branch `spike/designer-catchup`. Read
`CLAUDE.md`, then `git log --oneline 6dc36bc..HEAD` and the spike reports
(`docs/superpowers/plans/2026-09-30-spike-report.md`, `...-spike-loud-report.md`): the daemon
gained, in one evening, `line` parts, bindable geometry, dial `opacity`, bar `shape`/`thickness`/
`glow`, Step/Blend rules on everything, `tick --preview`, sun/moon/weather/heat layers, media,
battery, a pen and a trace tool. The designer got the pen and rules swatches. Close the gap.

You own `src/DeskWall.Designer/` and `tests/DeskWall.Designer.Tests/` only. Never open the
designer on the live runtime dir; use the in-process rendering harness pattern (see
`%TEMP%\dw-*\live.ps1` and the earlier lane's `Modes.cs` harness if present, else build your own
under `%TEMP%\dw-designer\`) with a scratch `DESKWALL_HOME` holding a copy of
`layouts/alpine-vision.json`. Full permissions, never stop to ask; plain-English commits; build
clean and the designer tests green.

## Do
1. **Insert panel**: `Line` in the Parts row, drag and Enter like the others; a "Shape" preset
   under Bar that inserts a shaped bar with a placeholder path; a Sun/Moon preset (image + the
   arc Blend rules pre-wired to `time.dayFraction`).
2. **Properties panel**: every new property has a proper editor: `shape` gets a multi-line box
   with "Draw..." (pen) and "Trace..." (skyline) buttons; `glow`/`thickness`/`opacity` as
   numbers with the bind chip; geometry rows show a chip when bound and a literal box when not.
3. **Preview scenarios**: a "Preview as..." menu on the canvas toolbar with the gallery's scenes
   (midnight, dawn, noon, dusk, storm, snow, machine on fire, drive full, now playing) that pins
   the same values `tick --preview` pins, so the designer shows the desktop the owner cannot see
   yet. Shares the pinning code with Core (`PreviewOverrides`), does not reimplement it.
4. **Layers**: 50 components need grouping. Group by the `z` band and by a new optional
   `"group": "sky"` field on components (Core: a string on `ComponentDef`, serialised, otherwise
   inert; the one Core touch allowed); collapsible groups; a visibility eye per layer that sets a
   designer-only hide (not saved).
5. **Critique round**, in the style of the repo's "critique 3" commits: run the harness, take
   screenshots of every panel with the loud layout open, write
   `docs/superpowers/plans/2026-09-30-designer-critique-4.md` with numbered findings (P1/P2/P3),
   then fix every P1 and P2 in this lane, one commit each, referencing the finding.

## Report
`docs/superpowers/plans/2026-09-30-lane-designer-report.md` with before/after harness
screenshots. Leave the branch unmerged.
