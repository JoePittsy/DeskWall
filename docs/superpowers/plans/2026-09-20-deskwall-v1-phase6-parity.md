# DeskWall v1 Phase 6: Verify, Budgets, Docs and Parity Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the parity gate in the master plan: `deskwall verify` proves icon placement by
pixel diff, golden-image tests pin the renderer, budget tests assert the spec's cost table under
native AOT, the docs describe the shipped system, and the PowerShell POC is retired.

**Architecture:** `verify` is the POC's `verify.ps1` grown up, built on Phase 3's `Screenshot` and
`Calibrator.DiffBounds`. Golden tests render fixed layouts with fixed fonts through the real
Direct2D path and compare to checked-in PNGs. Budget tests drive `deskwall tick --measure` and the
running daemon's `Footprint` on the reference machine and are skipped elsewhere by a trait.

**Tech Stack:** as before. Fonts for goldens: `Segoe UI` and `Segoe UI Light` are Windows-shipped,
so no font files are checked in; goldens are rendered and compared on Windows only.

**Spec:** `docs/superpowers/specs/2026-09-20-deskwall-v1-design.md` (sections 9, 1.2, 10)
**Master plan:** `docs/superpowers/plans/2026-09-20-deskwall-v1-master.md` (parity gate)
**Depends on:** Phases 2 to 5 merged; the MSVC linker installed so `dotnet publish` native AOT works
(budget tests are meaningless without it).

## Global Constraints

- Everything in the master plan's Global Constraints.
- Budget tests carry `[Trait("Category", "Budget")]` and are excluded from the default `dotnet test`
  run (`tests/DeskWall.Core.Tests/DeskWall.Core.Tests.csproj` gets a default filter, or the
  runsettings excludes the trait); they run explicitly with
  `dotnet test --filter Category=Budget` against a published AOT `deskwall.exe`.
- Golden PNGs live under `tests/DeskWall.Core.Tests/Goldens/` and are tracked (root `.gitignore`
  only excludes `/*.png`). A golden changes only in a commit whose message says why.
- `verify` and `calibrate` mutate the live desktop for seconds and restore it; they are commands,
  never on the tick path.
- The POC is deleted only after every parity gate item is ticked in this plan, in its own commit.
- Lane assignment: `lane/p6-verify` (Opus: Tasks 1, 2), `lane/p6-tests` (Sonnet: Tasks 3, 4),
  `lane/p6-docs` (Sonnet: Tasks 5, 6); Task 7 controller.

## Interfaces consumed

```csharp
Screenshot.Capture(Rect) : Surface;  Calibrator.DiffBounds(shot, reference, probe, threshold) : Rect?   // Phase 3
ShortcutManager, ShortcutPlan.IconPosition, Calibration.Load(), DesktopView.GetPosition               // Phase 3
TickRunner(..., shortcuts, images).RunAsync; TickTimings.ToTable(); FrameState                        // Phases 1-4
LayoutStore.Default().Resolve(sig); Monitors.Enumerate()                                              // Phase 2
Footprint.Current(); RollingLog.Default()                                                              // Phase 2
Surface.Load/GetPixel; FrameRenderer.RenderAll; BaseCache.Ensure; LayoutResolver.Resolve             // Phase 1
```

---

### Task 1: `deskwall verify` (lane `lane/p6-verify`, Opus)

**Files:**
- Create: `src/DeskWall.Core/Verify/Verifier.cs`, `src/DeskWall.Core/Verify/VerifyReport.cs`
- Modify: `src/DeskWall.Daemon/Program.cs` (`verify [--pad N] [--threshold N] [--json]`)
- Test: `tests/DeskWall.Core.Tests/Verify/VerifierTests.cs` (pure part on synthetic surfaces)

**Interfaces:**
- Produces:

```csharp
public sealed record SlotCheck(int Slot, string Id, Rect Cover, (int X, int Y) Wanted, (int X, int Y)? Got, Rect? ArrowBox, int LeftPad, int BottomPad, bool Ok, string? Note);
public sealed record VerifyReport(DisplaySignature Signature, IReadOnlyList<SlotCheck> Slots, string ScreenshotPath, string ClockCropPath, bool Ok)
{ public string ToText(); public string ToJson(); }

public static class Verifier
{
    /// <summary>Pure: for one cover rect, find the arrow box in the screenshot vs the composed frame
    /// (inset 2 px to dodge JPEG ringing) and compute the left and bottom padding.</summary>
    public static SlotCheck CheckSlot(Surface shot, Surface composed, ResolvedShortcut s, (int X, int Y) wanted, (int X, int Y)? got, int wantPad, int threshold);
    /// <summary>Live: minimise all, screenshot the primary monitor, load the composed frame from
    /// frame.raw, check every ResolvedShortcut of the current layout, save the right-hand 400 px
    /// column and an 8x clock crop (if a text component named clock exists) to the runtime dir,
    /// undo minimise, return the report. Exit code semantics: Ok = every slot found at wantPad.</summary>
    public static VerifyReport Run(int wantPad = 5, int threshold = 60, Action<string>? log = null);
}
```

- [ ] **Step 1: Failing tests** (`CheckSlot` on synthetic 200x300 surfaces: arrow at exact pad -> Ok, LeftPad 5, BottomPad 5; arrow 1 px off -> not Ok with pads 6/5; no diff -> Ok false with Note "NO ICON FOUND"; JPEG-noise blip below threshold ignored).
- [ ] **Step 2: Implement.** `Run` resolves the layout via `LayoutStore.Default().Resolve(primary.Signature)`, resolves components with the current source values (`TickRunner`'s registry is not available: build a fresh `SourceRegistry`, refresh every source once with a 5 s timeout), takes `LastShortcuts`-equivalent from the resolved list, computes `wanted` via `ShortcutPlan.IconPosition` with `Calibration.Load()`, reads `got` via `DesktopView.GetPosition`, uses `MinimizeAll`/`UndoMinimizeALL` as `Calibrator` does, `Screenshot.Capture(primary.Bounds)`, `Surface.LoadRaw(frame.raw)`.
- [ ] **Step 3: Live run** on JOES-PC (console session, 3440x1440) with `layouts/steam-recent.json` active and the daemon running: expected `left pad 5, bottom pad 5` on every slot, `RESULT: OK`. Paste the text report into the lane report; keep `verify-desktop.png` and `clock-now.png` in the runtime dir.
- [ ] **Step 4: Commit** `git commit -m "verify: screenshot vs composed frame, arrow padding per slot, clock crop"`

---

### Task 2: Budget test harness (lane `lane/p6-verify`, Opus)

**Files:**
- Create: `tests/DeskWall.Core.Tests/Budget/BudgetTests.cs`, `tests/DeskWall.Core.Tests/Budget/DaemonProcess.cs`, `tests/deskwall.runsettings`
- Modify: `tests/DeskWall.Core.Tests/DeskWall.Core.Tests.csproj` (`<RunSettingsFilePath>` pointing at the runsettings that excludes `Category=Budget` by default)

Each test finds the published AOT exe at `src/DeskWall.Daemon/bin/Release/net10.0-windows10.0.19041.0/win-x64/publish/deskwall.exe`
(skips with a clear message if absent), uses a scratch `DESKWALL_HOME` with `layouts/clock-disks.json`
registered for the current signature, and asserts the spec 1.2 table:

| Test | Method | Budget |
|---|---|---|
| `ColdStart_To_First_Wallpaper` | `Process.Start(exe, "run --no-tray")`, poll `deskwall.jpg` mtime at 10 ms | under 500 ms |
| `Idle_PrivateBytes_After_Trim` | daemon running 3 min, then `Process.PrivateMemorySize64` | under 10 MB |
| `Idle_Cpu_Between_Wakes` | `TotalProcessorTime` delta over a 4-minute window minus the ticks' own `cpu` from the log | under 50 ms total (measurement noise floor; the spec says 0) |
| `Idle_Handles_And_Threads` | `HandleCount` under 100, `Threads.Count` under 5 after 3 min | as spec |
| `ClockOnly_Tick_Wall_And_Cpu` | `deskwall tick --measure` (clock layout, warm cache) parsed table | total under 60 ms wall, cpu under 40 ms |

Log the actual numbers with `ITestOutputHelper` and write them to
`docs/superpowers/plans/2026-09-20-phase1-spike-results.md` under `## Phase 6 budget results`
(the harness prints a markdown row; the lane pastes it).

- [ ] **Step 1:** runsettings excludes the trait; `dotnet test` count unchanged; `dotnet test --filter Category=Budget` runs five tests (skipped without the exe).
- [ ] **Step 2:** run on JOES-PC after `dotnet publish -c Release -r win-x64`; paste results. Any failing line is a finding for the controller, not something to loosen.
- [ ] **Step 3: Commit** `git commit -m "Budget tests: AOT daemon footprint and tick cost against spec 1.2"`

Lane `lane/p6-verify` complete.

---

### Task 3: Golden-image tests (lane `lane/p6-tests`, Sonnet)

**Files:**
- Create: `tests/DeskWall.Core.Tests/Goldens/*.png`, `tests/DeskWall.Core.Tests/Goldens/GoldenTests.cs`, `tests/DeskWall.Core.Tests/Goldens/layouts/*.json`, `tests/DeskWall.Core.Tests/Goldens/Compare.cs`
- Modify: `tests/DeskWall.Core.Tests/DeskWall.Core.Tests.csproj` (copy Goldens to output)

Four goldens at 860x360 (a quarter-scale canvas so files stay small): `text-styles` (all four
effects, three sizes, three alignments), `bar-states` (0, 0.5, 0.86 with threshold, vertical),
`image-fits` (cover/contain/stretch, radius 0 and 12, opacity 0.5) with a checked-in 60x90 PNG,
`repeater-auto` (three items with auto cell height). Base is a flat `#FF203040`. Values come from a
fixed `ValueTree`, never from real sources; `time` is a literal.

`Compare.Diff(Surface a, Surface b, int tolerance = 8) : (int DifferentPixels, Rect? Bounds)`;
a test fails when more than 0.1 percent of pixels differ by more than `tolerance` in any channel,
and on failure writes `<name>.actual.png` and `<name>.diff.png` next to the golden for inspection.
`GOLDENS_UPDATE=1` in the environment rewrites the goldens instead of comparing (documented in the
test file header; never set in CI).

- [ ] **Step 1:** write the four layouts and `Compare`; generate goldens with `GOLDENS_UPDATE=1`; commit them with the message stating the renderer commit they were made from.
- [ ] **Step 2:** deliberately change a colour in a layout, see the test fail and the diff PNG appear, revert.
- [ ] **Step 3: Commit** `git commit -m "Golden-image tests for text, bars, images and repeaters"`

---

### Task 4: Test hygiene from the Phase 1 review (lane `lane/p6-tests`, Sonnet)

Review minor 18: three tests depend on machine state (`WallpaperSetterTests.Get_ReturnsCurrentPath_ForPrimary`,
`DisplaySignatureTests.Enumerate_Returns_At_Least_Primary`, `DisksSourceTests`). Keep them (they are the
only real-desktop coverage) but give each a `[Trait("Category", "Desktop")]` and make them
`Skip` with a reason when `Monitors.Enumerate()` is empty or no fixed drive is ready, so a CI box
without a desktop session reports skipped, not failed. Also add the missing corrupt-`.lnk` test
for `ShortcutFiles.Read` (Phase 3 deferred minor).

- [ ] Commit `git commit -m "Tests: desktop-bound tests skip without a desktop; corrupt .lnk read test"`

Lane `lane/p6-tests` complete.

---

### Task 5: Documentation (lane `lane/p6-docs`, Sonnet)

**Files:**
- Rewrite: `README.md` (human overview of v1: what it is, install, first run, layouts, budget numbers from Task 2, how verify works, uninstall, the POC's place in history)
- Create: `docs/layout-format.md` (every component type and property, the binding grammar with ten examples, repeater semantics including auto cells and clamping, display signatures and scaling), `docs/sources.md` (every source type, its settings, what it publishes, failure behaviour, the secrets file), `docs/architecture.md` (the process model in one page, the tick pipeline, where files live in the runtime dir, the budget and how it is enforced)
- Modify: `CLAUDE.md` (fold the "v1 gotchas" into the main structure; move POC-only sections under a "POC (retired)" heading; update the file table to the v1 tree)

Rules: no marketing adjectives; every number comes from a measurement file; every command shown
was run. The layout-format doc is generated by hand from `ComponentDef.cs`, `PropertySchema.cs`
and the JSON tests, and each property's default is the one in the code.

- [ ] Commit `git commit -m "Docs: README for v1, layout format, sources, architecture; CLAUDE.md restructured"`

---

### Task 6: Starter layouts and the `layouts/` folder (lane `lane/p6-docs`, Sonnet)

Reconcile `layouts/` with what Phase 5 shipped: `starter-column.json`, `starter-clock.json`,
`starter-blank.json`, `steam-recent.json`, `clock-disks.json`, each opening cleanly in the designer
and rendering with `deskwall tick --layout <file> --force --no-apply` on the ultrawide signature.
`layouts/README.md` lists them all with one sentence each and the secrets section.

- [ ] Commit `git commit -m "layouts: starters reconciled with the designer; README"`

Lane `lane/p6-docs` complete.

---

### Task 7: Parity gate and POC retirement (controller)

- [ ] Every box in the master plan's parity gate ticked with evidence pasted into the ledger:
  starter column reproduces the POC column and `deskwall verify` reports 5/5 on every cover;
  budget table passes under AOT; the spec section 9 manual acceptance list walked; the daemon has
  run 24 hours with zero tick failures in `deskwall.log`.
- [ ] `Disable-ScheduledTask 'DeskWall Tick'` then `Unregister-ScheduledTask`; `deskwall install`.
- [ ] `git rm -r poc/` and remove the POC sections from `CLAUDE.md` and `README.md` (one commit:
  "Retire the PowerShell POC: v1 reached parity on <date>").
- [ ] Opus whole-branch review of Phases 2 to 6 changes (one agent), ONE fix wave, re-review.
- [ ] Tag `v1.0.0-rc1` on `v1`. Merging `v1` into a `main` branch is Joe's call (finishing-a-development-branch).

## Self-review notes

- Spec 9 bullets: unit (all phases), golden (Task 3), budget (Task 2), verify (Task 1), manual acceptance (Task 7).
- Spec 10 parity gate and POC removal: Task 7.
- Master plan's Global Constraints: goldens tracked; budget tests need AOT; `.gitignore` already scoped in Phase 1.
- Type names: `Screenshot.Capture`, `Calibrator.DiffBounds`, `ShortcutPlan.IconPosition`, `Calibration.Load`, `DesktopView.GetPosition` as shipped by Phase 3.
