# Alpine vision spike — 30 September 2026

## Part 1 checkpoint

Shipped day/night grade, skyline-clipped stars, mute indication, conditional reboot/crash text,
battery, hardware histories and `line`, event-driven media with cached PNG art, and live rules
with colour swatches. Native AOT publish succeeded and the live layout was installed.

Baseline forced tick: resolve 461 ms, draw 231 ms, encode 32 ms, total 727 ms, CPU 297 ms,
load 104 ms. Part 1 scratch forced tick: resolve 542 ms, draw 127 ms, encode 30 ms,
total 701 ms, CPU 312 ms, load 45 ms. These are one-shot processes, not resident warm timings.

The three original dials were day/week/year, despite the brief calling them hardware.
Replaced that linked copy with explicit CPU/RAM/GPU dials plus battery. The clock is preserved.
CPU temperature is unavailable by project design; no fabricated temperature will be shown.
Desktop folder view currently reports unavailable; shortcut verification remains for Part 2.

Revert the layout without overwriting the preserved backups:

```powershell
Copy-Item "$env:LOCALAPPDATA/DeskWall/column-system.before-spike.json" "$env:LOCALAPPDATA/DeskWall/column-system.json" -Force
```

Part 2 is in progress; final measurements and exceptions follow below.
