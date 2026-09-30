# Lane: presence sources (notifications, calendar, real sunset)

Worktree `D:\Source\Personal\DeskWall-presence`, branch `spike/presence-sources`. Read `CLAUDE.md`
(CsWin32 shapes, AOT, `DESKWALL_HOME`), `docs/sources.md`, `docs/architecture.md`, and
`src/DeskWall.Core/Sources/Audio/AudioSource.cs` + `Sources/Media*` (from `git log`) as the
pattern for a **push** source (`ISignalSource`, never polling). Another agent is rewriting the
layouts, renderer and designer on `main` right now: **stay inside `src/DeskWall.Core/Sources/`,
`SourceFactory`, the designer's `SourceForms`/`ValueCatalog`/`Insert.DefaultSources` tables,
`docs/sources.md` and `scripts/`.** Do not touch layouts, Render, Tick, Host, or publish. Do not
touch `%LOCALAPPDATA%\DeskWall`; use a scratch `DESKWALL_HOME` for anything that runs.

Full permissions, never stop to ask; commit each source separately in plain English; keep
`dotnet build` clean (AOT analyzers count) and the tests green.

## 1. `notifications` source (push)
WinRT `Windows.UI.Notifications.Management.UserNotificationListener` (TFM
`net10.0-windows10.0.19041.0` already projects it; the media source proved CsWinRT works under
AOT here). `RequestAccessAsync` once; if denied publish `status: denied` and nothing else.
Subscribe to `NotificationChanged` and publish `count`, `apps` (list of `{name, count}` ordered by
count), `latestApp`, `latestTitle`, `latestText`, `changedAt`. Settings: `include`/`exclude`
comma-separated app-name filters. Signal the bus on change.

## 2. `calendar` source (Microsoft Graph, device-code auth)
`scripts/graph-next-event.ps1`: uses the same device-code flow and token cache the owner's
`ms-todo` skill uses (see `%USERPROFILE%\.claude\skills\ms-todo` or wherever it lives; if the
skill's script is not readable, implement MSAL device-code against the public
`Microsoft Graph PowerShell` client id `14d82eec-204b-4c2f-b7e8-296a70dab67e` with scopes
`Calendars.Read offline_access`, cache the refresh token under the runtime dir with DPAPI). Prints
JSON: the next 3 events in the coming 12 h from `/me/calendarView` with `subject`, `start`,
`end`, `isOnline`, `joinUrl`, `location`, `minutesUntil`. Wire it as a **`command` source
recipe** in `docs/sources.md` (every 300 s) rather than new C#, unless the token flow needs a
first interactive sign-in, in which case add `deskwall calendar login` to `Program.cs` (the one
allowed touch outside Sources) that runs the device-code prompt in the console.

## 3. Real sunset: `time.phase`, `time.sunFraction`
`TimeSource` gains optional settings `sunrise`/`sunset` (`HH:mm`, or a binding-style path
`weather.json.daily.sunrise[0]` resolved through the registry tree at refresh) and publishes
`phase` (`night|dawn|day|dusk`: dawn = sunrise ±40 min, dusk = sunset ±40 min), `sunFraction`
(0 at sunrise, 1 at sunset, clamped, so a sun bound to it actually rises and sets when the real
one does) and `nightFraction` (the same across the night). Check how Open-Meteo's `daily`
block is requested in `layouts/column-system.json`'s weather recipe and document the two lines
to add to it (`daily=sunrise,sunset&timezone=auto`).

## Finish
Labels in `ValueCatalog`, defaults where zero-config (`notifications` yes, `calendar` no),
`docs/sources.md` entries with a recipe each, a short lane report at
`docs/superpowers/plans/2026-09-30-lane-presence-report.md` (what works, what needs the owner's
sign-in, what needs JOES-PC). Leave the branch unmerged.
