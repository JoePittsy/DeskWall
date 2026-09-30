# Lane report: presence sources (notifications, calendar, real sunset)

Branch `spike/presence-sources`, not merged. One commit per source:

- `9b42664` notifications
- `39a5036` calendar
- `946fc08` time/sun

Everything was measured on JOES-XPS-17 against a scratch `DESKWALL_HOME`; nothing touched
`%LOCALAPPDATA%\DeskWall`, layouts, Render, Tick, Host or publish.

Build: 0 warnings (the AOT analyzers included).

Tests: Core 518 -> 570, Designer 591 -> 591, all passing. The Designer count is unchanged because
its changes are edits to existing tests: the expected lists and the ValueCatalog label check.

## What works

**`notifications`** (push, zero-config, in `SourceForms.BuiltIn`/`Insert.DefaultSources`):

- Publishes `status`, `count`, `apps` (`{name, count}`, most first), `latestApp`, `latestTitle`,
  `latestText`, `latestAt` and `changedAt`.
- `include`/`exclude` filters take the app display name or its AUMID.
- `denied`/`unavailable` publish `status` alone.

Measured:

- Access is `Allowed` at the default privacy setting and nothing prompts.
- A toast reaches the bus about 1.4 s after `Show()`.
- A read costs 280-540 ms wall and 60-230 ms CPU, almost all of it inside the notification
  service; reads run off the tick thread.
- Idle cost is zero: 0 store events in 10 s.
- A scratch `tick` rendered the count and the latest title correctly.

**Deviation from the brief:** `NotificationChanged` throws `0x80070490` in an unpackaged process,
which both executables are. So the source watches the platform's own store
(`wpndatabase.db*`) instead of subscribing. Because every read makes the platform write that store
back, reads go through a session-wide gate (`Local\DeskWall.NotificationReads`), so the daemon and
the designer's Data panel cannot feed each other.

**Dismissals are the weak spot.** A cleared centre is sometimes not written to disk until the next
read, so a non-empty centre is also re-read every `every` s (default 300, on the minute tick).
Worst case, a dismissal takes five minutes to leave the wallpaper. `docs/sources.md` has the full
account and the remaining blind spot.

**Calendar** (a `command` recipe, not a default source):

- `scripts/graph-next-event.ps1` (pwsh 7) prints the next 3 events in 12 h from
  `/me/calendarView`, as the brief's fields plus `startText`/`endText`, `inProgress` and `isAllDay`,
  with `next` = `events[0]`.
- Exit codes: 0 ok; 2 prints `status: signin-required` for the layout to show; 1 prints nothing,
  so the last events stay.
- Verified through the real command source: a scratch tick drew "calendar: sign in" and exit
  code 2.
- Event shaping was checked offline against a fabricated Graph response: in-progress, all-day,
  cancelled and count cap.
- The device-code endpoint accepts the client id and scopes for both `common` and `consumers`.

**Deviation:** the default token path is the Graph PowerShell module's own cache (`-Flow mg`,
Windows sign-in / WAM), not device code. The owner's ms-todo skill uses exactly that, and it says
never to use device code for the TrueNorth tenant, because device-compliance Conditional Access
blocks it. The device-code flow from the brief is still there as `-Flow devicecode`, with the
refresh token DPAPI-protected in `<runtime>\calendar-token.txt`.

`deskwall calendar login` (Program.cs) opens pwsh 7 in a console window of its own rather than
"in the console". WAM needs a visible parent window, and a WinExe shares its caller's prompt
badly. The command waits for the window to close; the plumbing was checked with a stand-in
script.

**Sun:**

- `time` takes `sunrise`/`sunset` (either `HH:mm` or a binding path) and publishes a real
  `phase` (±40 min twilights), `sunFraction`, `nightFraction` and `sunrise`/`sunset`.
- Paths read `SourceTree.Latest`, which `SourceRegistry.Tree()` sets, so they lag by one tick.
- Without settings, `phase` is unchanged.
- Live check against the real Open-Meteo feed for Leeds: rise 07:06, set 18:45, `night` at 20:42,
  `nightFraction` 0.1591 (117.9 of 741 min).

## Needs the owner

- **Sign in once:** `deskwall calendar login` (or `pwsh -File graph-next-event.ps1 -Login`) after
  copying the script to `%LOCALAPPDATA%\DeskWall\scripts\`. `publish.ps1` does not ship `scripts/`.
- **The silent path could not be proven here.** `Connect-MgGraph` from an agent shell failed
  ("A window handle must be configured") even for the already-cached `Tasks.ReadWrite` scope, so
  whether it refreshes silently after a real sign-in is still unmeasured. If it does not, the mg
  flow needs an MSAL cache read without WAM.
- **Decide whether the weather URL gains `&daily=sunrise,sunset`**
  (`widgets/weather.json`, placed by `column-system.json`), and set the two time settings. Both
  are in the docs; this lane did not edit layouts or widgets.

## Needs JOES-PC

- The notification access check: the privacy setting may differ from this machine's.
- Toast-to-signal latency and read cost on the i7-6700K.
- Whether the work tenant blocks `-Flow devicecode` there, as the ms-todo skill says it does.
- A look at the sun-driven phase on the ultrawide once a layout binds it.

## Left for other lanes

- **Tick:** `deskwall tick --preview time.at=...` pins time with `TimeSource.Fields(now)`, which
  knows nothing about the sun, so a preview shows the fixed thresholds.   The fix is a small change in `PreviewOverrides`: pass the live `time.sunrise`/`time.sunset` to
  `Fields(now, rise, set)`.
- **ValueCatalog:** it labels command values by type (`command.json.*`), so calendar paths
  cannot be labelled without labelling every command script. They show as their paths.
