# Producers that push: what it actually cost

Measurements for `docs/superpowers/plans/2026-09-22-producers-that-push.md`. Machine: JOES-PC
(i7-6700K, Dell U3425WE 3440x1440 @ 100%, Windows 11). Daemon: **native AOT publish**
(`dotnet publish src/DeskWall.Daemon -c Release -r win-x64`), run against a scratch
`--home` beside the owner's live daemon.

## Lane B: `audio`

Date: 2026-09-22. Endpoint: "Digital Output (3- High Definition Audio Device)".

### One refresh

`CoreAudioReaderTests.A_Whole_AudioSource_Publishes_This_Machine` prints it, so it is re-measured
on every run rather than recorded once:

| | wall |
|---|---|
| first refresh in a cold process (enumerator, endpoint, property store, registration, JIT) | 13.9 / 13.9 / 14.3 ms over three runs |
| first refresh in a process where COM is already warm | 4.7 ms |
| every later refresh (compare two endpoint ids, read four cached fields) | 1.62 / 1.70 / 1.73 / 1.76 ms |

The later refreshes are the ones the daemon pays, once a minute, and 1.7 ms of it is
`GetDefaultAudioEndpoint` + `GetId` - the default-device check, not the volume, which the callback
already delivered.

### Latency: the volume moves, the wallpaper changes

Driven by `IAudioEndpointVolume::SetMasterVolumeLevelScalar` / `SetMute`, which raises exactly the
notification a volume key or the flyout raises. t0 is the instant before the set; t1 is when
`deskwall.jpg` in the scratch home is rewritten, polled in a tight loop.

| change | latency |
|---|---|
| volume -> 10% | 478 ms |
| volume -> 25% | 461 ms |
| volume -> 40% | 462 ms |
| volume -> 55% | 459 ms |
| volume -> 70% | 463 ms |
| volume -> 85% | 461 ms |
| mute on | 461 ms |
| mute off | 460 ms |

Eight of eight, spread 19 ms. `EventBus.DefaultCoalesce` is 400 ms of that by design and the
remaining ~60 ms is resolve + draw + encode + write. Lower is available only by shortening the
coalescing deadline, which is the same knob that decides what a slider drag costs.

### A slider drag

6.0 s of continuous change, 64 `SetMasterVolumeLevelScalar` calls (one every ~94 ms), plus a
1.5 s tail for the trailing coalesced wake:

| | |
|---|---|
| repaints | **15** |
| daemon CPU over the 7.5 s | 969 ms |
| CPU per repaint | ~64 ms |
| handles during | 342 (from 325 at rest) |
| threads during | 11 |

15 repaints for 64 changes is the 400 ms window working: one repaint per window for as long as
the drag lasts, and the final state always lands because the wake is trailing. Without the
debounce in `AudioSource` it would be 64; without the bus coalescing it would be 64 repaints of
two megabytes each.

### At rest

| run | window | repaints | daemon CPU over the window |
|---|---|---|---|
| audio layout, before the push path was wired | 185 s | 3, at 07:34:00.069, 07:35:00.066, 07:36:00.071 | 187 ms |
| audio layout, push path live | 125 s | 2, at 07:47:00.069, 07:48:00.075 | 156 ms |

Once a minute, within 75 ms of the whole minute, and the push path adds nothing at rest: the
callback is silent when the volume is.

### What the source costs the process

Same daemon, same 25 s settle, one layout with an `audio` source and one without:

| | with `audio` | without | delta |
|---|---|---|---|
| handles | 325 | 294 | **+31** |
| threads | 13 | 12 | **+1** |
| private bytes | 10.2 MB | 8.8 MB | **+1.4 MB** |
| working set | 3.3 MB | 3.3 MB | 0 |

The 31 handles are CoreAudio's: the device enumerator, the endpoint, the endpoint-volume object
and the RPC connection to the audio service, which is where the one extra thread comes from too.
Thread counts move by one or two between samples from thread-pool churn, so +1 is at the noise
floor; the handle count is not.

### What this found

**A signal is not a refresh.** `EventBus.Signal` buys a wake; the tick that follows only refreshes
the sources `Scheduler.IsDue` says are due, and on the whole-minute schedule `audio` was not one.
First measured run: 0 repaints from 8 volume changes and 0 from a 6 s drag - the wake happened and
found nothing to repaint. `AudioSource.NextDue` now reports "due now" while a notification is
waiting to be published, gated by the same debounce that decides whether to signal at all, so a
notification not worth a wake cannot pin the daemon at `Scheduler.MinDelay`. Any future
`ISignalSource` needs the same pair; a source that signals without also becoming due is a no-op.

### How these were taken, and what was disturbed

- `deskwall run --home <scratch>` beside the owner's live daemon (the single-instance lock is per
  runtime dir). **A scratch `run` paints the real wallpaper**; the owner's daemon reclaims it on
  its next content change, within a minute.
- The measurements **change the machine's master volume and mute state**. Both were read first
  (volume 1.0, unmuted) and restored exactly afterwards.
- The latency and drag numbers need the daemon to turn `AudioSource.Changed` into
  `bus.Signal(name)`, which is lane A's A1. That one subscription was applied to `DaemonLoop.cs`
  locally as measurement scaffolding and reverted; it is not in `lane/audio-source`.
