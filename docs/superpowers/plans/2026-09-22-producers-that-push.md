# Producers that push (event seam, phase 2)

Owner, 2026-09-22, having chosen the narrower of three scopes: the three producers that are
genuinely push-shaped move to the seam, and the daemon's out-of-band wakes collapse onto it.
**Time, disks, system, hardware, http, rss and plain command stay on the schedule**, because they
are schedule-shaped and the pull path carries failure back-off and staleness that the push path
deliberately does not have. Do not convert them.

Base: `2324698` on `integration/2026-09-21-placement-and-text`, which already contains the seam and
the frozen shared interface below. Core work: TDD, 0 warnings, measured not asserted.

## The interface, already committed, that both lanes build on

```csharp
// src/DeskWall.Core/Events/EventBus.cs
public void Signal(string source);
```

Carries no payload and touches no provider record. The source keeps its name, its declaration and
its failure back-off, and gains only "I know I am due now". Signals share the one 400 ms coalescing
deadline with pipe events, and are deliberately not written to the diagnostics ring.

## Lane A: existing sources learn to push

Branch `lane/sources-push`.

### A1. One `Changed` event, replacing four bespoke wakes

**Files:** `src/DeskWall.Core/Sources/ISource.cs` (add the interface), `AsyncSource.cs`,
`src/DeskWall.Daemon/DaemonLoop.cs`, `src/DeskWall.Designer/Model/LiveSources.cs`.

```csharp
/// <summary>A source that knows when it has something new, rather than waiting to be asked.</summary>
public interface ISignalSource
{
    event Action<ISource>? Changed;
}
```

- `AsyncSource` implements it. Its existing `Completed` event **is** this event: rename it to
  `Changed` and update the two subscribers (`DaemonLoop` line ~207, `LiveSources`). One name for
  one thing; do not keep both.
- In `DaemonLoop`, replace the per-source `Completed` subscription and the image cache's `Landed`
  subscription with one rule: anything that signals goes through `bus.Signal(name)`, and the
  daemon subscribes to `bus.WakeRequested` once. The layout watcher and the display-change wake
  stay exactly as they are; they are structural, not a value arriving.
- `LiveSources` does the same with its own bus so the designer behaves identically.

Tests: a fake signalling source raises `Changed` and the daemon-side wiring calls `Signal` with
its name; two sources signalling inside one window produce one wake (drive `PumpWake`, as
`EventBusSignalTests` does).

### A2. A `file` source watches instead of polling

**Files:** `src/DeskWall.Core/Sources/FileSource.cs`, tests.

Today `NextDue` returns now when the modification time has changed, and `every` defaults to 30 s
purely as the re-check interval. The source comment says the watcher "was to be wired in Phase 2
Task 8" and it never was. Wire it now: a `FileSystemWatcher` on the file's directory filtered to
its name, raising `Changed`. `FileSystemWatcher` is AOT-safe and costs no dedicated thread while
idle (`LayoutWatcher`'s own comment says so; read it and follow its debounce, because an editor
saving a file raises several events).

Keep the mtime check in `NextDue` as the belt to the watcher's braces: a network path or a
container mount may raise nothing. Raise `every`'s default from 30 s to 300 s now that the watcher
carries the latency, and say so in `docs/sources.md`.

Tests: writing the file raises `Changed` within a second; a write raises **one** `Changed`, not
three; deleting and recreating still signals; a watcher that cannot be created (a missing
directory) leaves the source working on its mtime path rather than throwing.

### A3. A `command` source that stays up and streams

**Files:** `src/DeskWall.Core/Sources/CommandSource.cs` (or a sibling `StreamingCommandSource.cs`
if that file grows past readable), `SourceFactory.cs`, tests, `docs/sources.md`.

New setting `stream` = `true`. The process starts on the first refresh and **stays up**; every
line it writes to stdout is parsed exactly as the non-streaming source parses its whole output
(`json` or `text`), becomes the source's current values, and raises `Changed`. This is what makes
a cheap always-on producer possible without a process spawn per tick, and it is the answer to
"self serve providers" for anything that can print a line.

Rules, each with a test:
- `RefreshAsync` returns the latest parsed line, or the last good values if none has arrived yet.
- A line that does not parse is skipped, counted, and does not disturb the last good values.
- The process exiting is not fatal: restart it, with the same exponential back-off `Scheduler.BackOff`
  already uses, and never in a tight loop. A process that exits immediately and repeatedly must
  not spin the machine; assert that.
- `Dispose` kills the whole process tree, as the timeout path already does.
- `stream` and a `timeout` together: the timeout does not apply, because there is no single run to
  time out. Say so in the docs rather than silently ignoring it.
- The stdout reader runs on its own thread and nothing may escape it into the process.

### A4. Docs and measurement

`docs/sources.md` for `file` (the watcher, the new default) and `command` (`stream`). Measure and
record in `docs/superpowers/plans/2026-09-22-producers-that-push-results.md`: the latency from
saving a watched file to the wallpaper changing, the cost of a streaming command at rest (handles,
threads, CPU over four minutes) against the same command polled every 300 s, and that an idle
machine with both still wakes once a minute.

## Lane B: volume, the first source that is pure push

Branch `lane/audio-source`.

### B1. The CoreAudio callback

**Files:** `src/DeskWall.Core/Sources/Audio/AudioSource.cs`,
`Audio/IAudioReader.cs`, `Audio/CoreAudioReader.cs`, `src/DeskWall.Core/NativeMethods.txt`,
`SourceFactory.cs`, tests.

A new source type `audio`, declared in a layout like any other, implementing `ISignalSource`.

| Field | Type | Notes |
|---|---|---|
| `volume` | number 0..1 | master scalar of the default playback endpoint |
| `volumePct` | number 0..100 | rounded, for text |
| `muted` | bool | drives a colour through the map format |
| `device` | text | the endpoint's friendly name |

- Register `IAudioEndpointVolumeCallback` via `IAudioEndpointVolume::RegisterControlChangeNotify`.
  The notification carries the new scalar and mute flag, so the callback stores them and raises
  `Changed`; it must not call back into COM.
- Under native AOT this means a hand-built vtable: four
  `[UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]` statics (`QueryInterface`,
  `AddRef`, `Release`, `OnNotify`) over a struct whose first field is the vtable pointer, with a
  `GCHandle` back to the managed object. CLAUDE.md's callback rule and
  `docs/superpowers/plans/2026-09-20-phase1-spike-results.md` apply. Keep it in one file and say in
  a comment why it is hand-rolled rather than `ComWrappers`.
- **Nothing may escape the callback.** It runs on an audio service thread and an exception crossing
  into native code takes the process down. Guard it the way `HardwareSource.SampleOnce` guards its
  timer callback, and count faults the same way.
- `NextDue` is the whole minute, as `TimeSource` and `HardwareSource` do it, so it shares the
  clock's existing wake and costs nothing. Its jobs are the first reading and noticing the
  **default device changed** (a headset plugged in): compare the endpoint id and re-register.
- No playback device: publish an empty record, not zeros.
- `Dispose` unregisters and releases; double dispose is safe.

Testability follows `SystemSource`'s probe pattern: the source takes an `IAudioReader` whose fake
raises notifications on demand, so debounce, device-change and the empty case are tested without
real hardware. `CoreAudioReader` gets its own plausibility test against this machine (volume in
0..1, mute a bool, a non-empty device name), in the style of `Win32HardwareReaderTests`.

### B2. The widget, replacing the PowerShell stopgap

**Files:** `widgets/volume.json`, `docs/sources.md`.

A dial of `audio.volume` with the percent inside, painted `#FFD13438` when `audio.muted` through
the map format, caption "muted" or "vol". Right-hand column width, existing type sizes, no header.

The owner's runtime directory holds a user `volume.json` built from a PowerShell script, which
under the copy-on-write rule would **override** this shipped one and keep showing the old version
marked "edited". Say so in your report; the controller removes it at install time.

### B3. Measurement

Record in `docs/superpowers/plans/2026-09-22-producers-that-push-results.md` (append; lane A owns
the file's other half): the wall time of one refresh, the latency from pressing a volume key to the
wallpaper changing, what dragging the volume slider for several seconds costs in repaints and CPU,
the handle and thread delta, and that an idle machine still wakes once a minute.
