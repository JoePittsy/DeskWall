# Sources

A source is declared in a layout file's `sources` array:

```json
{ "name": "steam", "type": "http", "every": 600, "settings": { "url": "...", "unixTimeFields": "..." } }
```

| Field | Notes |
|---|---|
| `name` | The root field bindings use to reach this source's values (`steam.json...`). |
| `type` | One of `time`, `disks`, `system`, `hardware`, `command`, `http`, `rss`, `file` (`SourceFactory.Create`). Anything else throws when the layout is loaded. |
| `every` | Seconds between refreshes. Optional; each type has its own default (below). Ignored by `time`, which is always due on the next whole minute. **A top-level field, not a `settings` key** -- each type's "Settings" line below names it only to give its default, and a source that finds `every` inside `settings` ignores it. |
| `settings` | A flat string-to-string map; each source type documents its own keys below. |

Source of truth for this document: `src/DeskWall.Core/Sources/*.cs` (the `FromDef` method on
each source reads its own `settings` keys and defaults) and `SourceFactory.cs`.

## Failure and staleness (applies to every source)

- A source that throws from `RefreshAsync` is marked failed (`SourceSnapshot.Failed`); its
  **previous** successful values keep publishing (spec 3.2 -- a partial or blank record must
  never overwrite the last good one).
- A failing source is retried on a backed-off schedule: its own interval, doubled per
  consecutive failure, capped at 15 minutes (`Scheduler.BackOff`). This is also what the daemon's
  wake timer uses, so a dead endpoint does not pin the daemon awake on `MinDelay` (250 ms)
  forever.
- The daemon (not `deskwall tick`, which runs once) additionally drops a source's values from the
  tree entirely once it has missed `StaleAfter` (3) of its own scheduled refreshes
  (`SourceRegistry.Tree(sources, now)`), so a bound component falls back to its own default
  instead of showing a number that is merely old. `StaleChanged` logs one WARN going stale and
  one INFO coming back, not one line per tick.
- Network and command sources never run on the tick thread (`AsyncSource`); a tick reads whatever
  the last completed fetch published, and a fetch finishing later posts a wake so the next tick
  picks it up.

## `time`

No settings. Always due on the next whole minute (`NextDue` rounds up to `:00`).

Publishes: `now` (`TimeValue`), `date` (`TextValue`, `yyyy-MM-dd`), `weekday` (`TextValue`, e.g.
`Saturday`), and how far through the day, week and year local time is:

| Field | Meaning |
|---|---|
| `dayFraction`, `weekFraction`, `yearFraction` | 0..1, rounded to 4 decimals |
| `dayPercent`, `weekPercent`, `yearPercent` | the same, 0..100, rounded to a whole number |

Both forms exist because a `bar`'s `fraction` wants 0..1 and a `text` wants the percent, and a
format string cannot multiply by 100. The **week starts on Monday** (`((int)DayOfWeek + 6) % 7`),
not on Sunday. The year divides by 366 in a leap year and 365 otherwise. All three are derived
from the same local `now` the clock publishes, so a "day progress" widget needs no script.

## `disks`

No settings read. Default `every`: 300 s.

Enumerates fixed, ready drives (`DriveInfo.GetDrives().Where(Fixed && IsReady)`). Publishes
`drives`: a list keyed by `letter`, each item: `letter`, `label`, `free` (bytes, `NumberValue`),
`total` (bytes), `freeGB`, `totalGB` (both rounded), `usedFraction` (0 when `total <= 0`).

## `system`

No settings read beyond `every` (default 900 s).

Publishes: `uptime` (seconds), `uptimeText` (`"3d 4h"` or `"4h 12m"` under a day), `bootedAt`
(`TimeValue`), `machine`, `user`, `pendingReboot` (`BoolValue`: true if any of the standard
Windows reboot-pending registry markers exists -- CBS `RebootPending`, Windows Update
`RebootRequired`, or a non-empty `PendingFileRenameOperations`), `daysSinceCrash` (`-1` if none
found), `lastCrashAt` (`TimeValue`, omitted entirely when no crash is known).

The crash scan reads at most the last 2000 entries of the System event log for id 41
(Kernel-Power) or 1001 (BugCheck) with entry type Error or Warning. It runs **once per process**
and is cached (`SystemSource.CachedCrashEvent`): the answer cannot change while the machine has
not rebooted, and the scan can take seconds on a busy log, which would otherwise block the tick
thread (which, in the daemon, is also the message pump) every 15 minutes.

## `hardware`

Settings: `every` (seconds between publishes, default 60), `sample` (seconds between readings,
default 10), `window` (seconds averaged over, default 300). Each is read from `settings` as a
positive number; anything unparseable or <= 0 falls back to the default. `every` is the ordinary
top-level `every` field.

CPU, RAM and (when an NVIDIA GPU is present) GPU load, averaged over a rolling window. A
`System.Threading.Timer` inside the source, started on the first `RefreshAsync` and stopped on
`Dispose`, takes one reading of each metric every `sample` seconds into a fixed ring of
`window / sample` slots (at least 1). `RefreshAsync` itself only reads the rings, so the daemon's
schedule is unchanged: the source is due every `every` seconds like any other, and the daemon
still wakes once a minute. A reading that fails is skipped, not recorded as zero.

| Field | Meaning |
|---|---|
| `cpu` | average load over the window, 0..1 |
| `cpuPct` | the same as an integer 0..100, for text |
| `cpuNow` | latest single reading, 0..1 |
| `ram` | average used fraction over the window, 0..1 |
| `ramPct` | the same as an integer 0..100 |
| `ramUsedGB`, `ramTotalGB` | latest reading, decimal GB to one decimal |
| `gpu`, `gpuPct`, `gpuNow` | GPU utilisation, same shapes as `cpu`; absent with no GPU reader |
| `gpuMemory` | GPU memory used fraction, average over the window; absent likewise |
| `gpuTempC` | average GPU temperature, integer degrees C; absent likewise |
| `gpuTempFraction` | `gpuTempC / 100`, so a dial can bind it without arithmetic |
| `samples` | readings in the window right now (0..30 with the defaults) |
| `window` | window length in seconds |

All values are `NumberValue`. Fractions are published rounded to 3 decimals so a content key does
not change for a difference below a pixel (the same rule as `ResolvedBar`). A refresh with zero
samples publishes only `samples: 0` and `window`, and every bound property falls back to its own
default; nothing throws out of `RefreshAsync`.

CPU load comes from `GetSystemTimes` (`1 - dIdle / d(Kernel + User)`), so it needs two readings:
the first sample after a start contributes nothing. RAM comes from `GlobalMemoryStatusEx`.

**GPU.** NVML (`nvml.dll`), the library the NVIDIA driver installs in `System32`; the legacy
`%ProgramFiles%\NVIDIA Corporation\NVSMI\` folder is probed as a fallback. It is loaded with
`NativeLibrary.TryLoad` and called through unmanaged function pointers (no marshalling, native
AOT safe), so a machine with no NVIDIA GPU, no driver, or a failing `nvmlInit_v2` simply gets no
GPU reader and the source publishes none of the `gpu*` fields at all -- rather than fields that
are permanently missing a value. A layout that binds them must therefore have a sensible default
on each bound property.

**No CPU temperature.** There is no driverless, admin-free way to read core temperature on
JOES-PC (the ACPI thermal zone is access denied and is not a core reading anyway), and running
HWiNFO64 resident or shipping an MSR driver were both declined; GPU temperature from NVML is the
only temperature published.

## `command`

Settings: `command` (required), `args` (optional, may contain `{secret:name}`), `workingDir`
(optional), `every` (default 600 s), `timeout` (seconds, default 10), `parse` (`"json"` or
`"text"`; default: `json` if stdout starts with `{` or `[`, else `text`), `unixTimeFields`
(comma-separated field names to reinterpret as Unix timestamps when parsing JSON).

Runs the command hidden (no window), captures stdout as UTF-8. Publishes `text` or `json`
(whichever `parse` picked), `exitCode` (`NumberValue`), `ranAt` (`TimeValue`), and `stderr`
(`TextValue`) only when stderr is non-empty.

- A non-zero exit that printed **something** to stdout is still a success (scripts use the exit
  code as a flag the layout can bind to); a non-zero exit with **empty** stdout throws, so the
  last good values stay published.
- A timeout kills the whole process tree and throws.
- **Caveat:** `stderr` is published verbatim. A command that fails and echoes its own argument
  list back (the usual shape of a usage error) will publish the *substituted* value of any
  `{secret:...}` in `args` into a value a text component could draw on the wallpaper. Do not
  bind a component to a command's `stderr` if its `args` carry a secret.

## `http`

Settings: `url` (required, may contain `{secret:name}`), `every` (default 600 s), `timeout`
(seconds, default 10), `header.<Name>` = value (one setting per header, value may contain
secrets), `parse` (`"json"` or `"text"`; default: `json` if the response `Content-Type` says so,
else `text`), `unixTimeFields`.

Publishes `json` or `text`, `status` (`NumberValue`, the HTTP status), `fetchedAt` (`TimeValue`),
`fromCache` (`BoolValue`: true when a `304 Not Modified` served the previous body).

- Revalidates with `If-None-Match` / `If-Modified-Since` when a previous fetch supplied an ETag
  or `Last-Modified`.
- The response body is capped at 4 MB (`BoundedHttp`); a larger body fails the fetch rather than
  buffering without limit.
- A hard ceiling of six times the source's own `timeout` bounds one fetch end to end
  (`HardCeiling`). The token `AsyncSource` hands the fetch is never cancelled on its own -- the
  work must finish and report so the next tick can use the result -- so without this ceiling a
  server that completes the TCP handshake and then stalls the body never trips the connect
  timeout, and the source fails every tick for the life of the daemon instead of eventually
  giving up and retrying clean.

## `rss`

Settings: `url` (required), `every` (default 900 s), `timeout` (seconds, default 10), `max`
(items, default 20).

Accepts RSS 2.0 (`<rss><channel><item>`, `pubDate`) and Atom 1.0 (`<feed><entry>`,
`published`/`updated`, `<link rel="alternate">` preferred over other link relations). Publishes
`title`, `link` (the feed's own), and `items`: a list keyed by `link`, each item `title`, `link`,
`published` (`TimeValue`, omitted if unparsable), `summary` (HTML tags stripped, collapsed
whitespace, truncated to 500 characters), `author` (omitted if absent).

Same body cap (4 MB) and hard ceiling (`timeout * 6`) as `http`, for the same reason.

## `file`

Settings: `path` (required; `%ENV%` variables expanded), `parse` (`"json"`, `"text"` or `"rss"`;
default by extension: `.json` -> json, `.xml`/`.rss`/`.atom` -> rss, else text), `every` (default
30 s -- a cheap mtime re-check; the running daemon also wakes on a file-system watcher),
`unixTimeFields` (as `http`).

Publishes `json`, `text`, or (for `"rss"`) the same `title`/`link`/`items` shape as the `rss`
source, plus `modifiedAt` (`TimeValue`, local time), `size` (bytes), `exists` (`BoolValue`,
always `true` when this source has ever published -- see below).

- `NextDue` returns "now" whenever the file's mtime has changed since the last refresh, so any
  daemon wake picks up an edit immediately rather than waiting out the 30 s poll.
- **A missing file throws** rather than publishing `{ exists: false }`. Publishing that shape
  would be a *successful* record with false-y content that overwrites the last good list -- the
  same partial-record-over-last-good problem spec 3.2 rules out elsewhere. A producer that writes
  its output by delete-then-create would otherwise blank the bound component for one tick, every
  time it ran. A layout that wants to show "the file is missing" does that through the bound
  component's own fallback, not through a value this source publishes.

This is the seam Hearth (the owner's Playnite add-on) is expected to feed through: Hearth writes
a JSON or RSS-shaped file, a `file` source in the layout reads it, no Playnite- or LiteDB-specific
code exists in DeskWall itself (spec 4.3).

## Remote images

An `image` component (or a repeater template's image child) may bind `source` to an `http(s)`
URL. The render path only ever opens **local** files, so `RemoteImageCache`
(`src/DeskWall.Core/Render/RemoteImageCache.cs`) sits in between:

- `Lookup(url)` is synchronous and never touches the network. A hit returns the cached file path
  immediately; a miss returns `null` (the renderer draws the same fallback plate as a missing
  local file) and starts a background download.
- Cache files live at `runtime/images/<sha256-16-of-url>.img`, alongside a `.meta` file (URL,
  ETag, fetch time) used to revalidate.
- A cached file older than `maxAge` (default 24 hours) is still served immediately while a
  revalidation (conditional `GET`) runs in the background.
- One download per URL at a time (`_inFlight`); a URL that has failed recently is skipped for 10
  minutes (`NegativeTtl`) rather than retried every tick.
- A download is capped at 20 MB and must report an `image/*` content type, or it is treated as a
  failure (and negative-cached).
- When a download lands, `Landed` fires and the daemon posts a wake so the frame gets repainted
  with the real image on the next tick.
- Files nobody has looked up for 30 days are deleted by `Sweep()`, which the daemon runs once at
  startup.

## Secrets

`secrets.json` in the runtime dir is a flat `{ "name": "value", ... }` map
(`src/DeskWall.Core/Sources/Secrets.cs`). A layout never contains a secret directly; it
references one by name with `{secret:name}` inside an `http` source's `url` or `header.*`
values, or a `command` source's `args`. Substitution happens at request time
(`Secrets.Substitute`) and the resolved value is never logged -- a failed request logs the
template (`.../{secret:steamKey}/...`), not the key. An unknown secret name throws
`KeyNotFoundException` naming the secret, never a value. The daemon and `deskwall tick` never
write this file; the owner (or the designer's secrets editor) does.
