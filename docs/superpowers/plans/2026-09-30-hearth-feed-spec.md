# Hearth feed spec: writing `hearth-recent.json` for DeskWall

Status: not built -- tracked in #35.

Implementation spec for part 1 of `docs/superpowers/plans/2026-09-30-lane-hearth.md`, written
because the Hearth repo (`C:\Users\JosephPitts\source\repos\Hearth` or
`D:\Source\Personal\Hearth`) is not present on this machine. Point an agent at this one file on
JOES-PC, where Hearth lives, and it has everything needed: the feed schema, the atomic write, the
Steam merge (ported from `poc/data.ps1`), and the error handling. It does not need
`docs/sources.md` or the widget open at the same time, though both exist and match this spec --
`docs/sources.md` "Recipe: Hearth recently-played games", `layouts/widgets/recent-games.json`.

DeskWall's side (the `file` source, the recipe, the `recent-games` widget, a scratch render
against a fake feed) is already built and proved in this same lane; this document is only the
Hearth (Playnite add-on) side. Nothing in DeskWall needs to change for it to work: the `file`
source at `src/DeskWall.Core/Sources/FileSource.cs` already watches the file and reacts within
about a second of the rename below.

## What Hearth must do

On **`OnGameStarted`**, **`OnGameStopped`** and **`OnLibraryUpdated`** (and, as cheap insurance,
once on **`OnApplicationStarted`** so the feed is not stale from last session if nothing else
fires soon after launch): read Playnite's own game database, merge in Steam's own last-played
times, pick the 8 most recent installed and non-hidden games, and write
`%LOCALAPPDATA%\DeskWall\feeds\hearth-recent.json` atomically. Every one of those four call sites
must be wrapped so nothing it does can throw out past the handler -- an add-on that crashes
Playnite is a much worse failure than a stale or missing wallpaper feed.

```
public override void OnGameStarted(OnGameStartedEventArgs args) => SafeWrite();
public override void OnGameStopped(OnGameStoppedEventArgs args) => SafeWrite();
public override void OnLibraryUpdated(OnLibraryUpdatedEventArgs args) => SafeWrite();
public override void OnApplicationStarted(OnApplicationStartedEventArgs args) => SafeWrite();

private void SafeWrite()
{
    try { FeedWriter.Write(PlayniteApi); }
    catch (Exception ex) { logger.Error(ex, "Hearth: failed to write the DeskWall feed"); }
}
```

**Check the installed `Playnite.SDK` package for the exact override shapes before writing this.**
Different Playnite SDK generations pass either a plain `Game` (plus, for `OnGameStopped`, an
`elapsedSeconds`) or an `...EventArgs` wrapper carrying `.Game`; the toolbox-generated plugin
project's own template (`Toolbox.exe new GenericPlugin ...`) shows the exact signatures for
whatever SDK version is referenced. The logic below (`FeedWriter.Write`) does not care which
shape called it, because it always re-reads the whole database rather than trusting the event's
own payload -- one game's start/stop is cheap enough to react to by recomputing the full list
from scratch, so there is no per-event state to keep in sync with reality, and no failure mode
where two events race and the second undoes the first's work.

There is no need to debounce these four call sites against each other: a full `Write` is a
database scan over a few hundred games at most, a regex over one `localconfig.vdf`, and one
atomic rename, well under what four events arriving within the same second cost noticeably. Do
not add a debounce unless it is actually seen to matter -- it is one more place to get wrong for
no measured benefit.

## The feed file

Path: `%LOCALAPPDATA%\DeskWall\feeds\hearth-recent.json` (create the `feeds` folder if it does not
exist; `Directory.CreateDirectory` is idempotent and safe to call every time). UTF-8, no BOM,
compact or indented -- DeskWall's `file` source parses either.

```json
{
  "writtenAt": "2026-09-30T19:42:11.4218273+01:00",
  "games": [
    {
      "id": "3f2a9c1e-8b7d-4a6f-9e2c-1d4b5a6c7e8f",
      "name": "Hollow Knight",
      "lastPlayed": "2026-09-30T18:55:02.0000000+01:00",
      "cover": "C:\\Users\\JosephPitts\\AppData\\Roaming\\Playnite\\library\\files\\3f2a9c1e-8b7d-4a6f-9e2c-1d4b5a6c7e8f\\cover.jpg",
      "installed": true,
      "playtimeMinutes": 742,
      "launch": "\"C:\\Users\\JosephPitts\\AppData\\Local\\Playnite\\Playnite.DesktopApp.exe\" --start 3f2a9c1e-8b7d-4a6f-9e2c-1d4b5a6c7e8f"
    },
    {
      "id": "7a1b2c3d-4e5f-6071-8293-a4b5c6d7e8f9",
      "name": "Celeste",
      "lastPlayed": "2026-09-29T21:03:47.0000000+01:00",
      "cover": "C:\\Users\\JosephPitts\\AppData\\Roaming\\Playnite\\library\\files\\7a1b2c3d-4e5f-6071-8293-a4b5c6d7e8f9\\cover.jpg",
      "installed": true,
      "playtimeMinutes": 1310,
      "launch": "\"C:\\Users\\JosephPitts\\AppData\\Local\\Playnite\\Playnite.DesktopApp.exe\" --start 7a1b2c3d-4e5f-6071-8293-a4b5c6d7e8f9"
    }
  ]
}
```

| Field | Type | Notes |
|---|---|---|
| `writtenAt` | string, ISO 8601 | `DateTimeOffset.Now.ToString("o")`, this write's timestamp -- lets a human (or `deskwall verify`, eventually) tell a stale feed from a missing one. |
| `games` | array, **at most 8**, most-recent-first | Installed, non-hidden games only; a game with no known last-played time at all (never launched, e.g. freshly imported) is left out rather than sorted to the bottom -- it is not "recent". |
| `games[].id` | string | `game.Id.ToString()` (Playnite's own GUID). `JsonValues.Parse` on the DeskWall side picks the first of `id`/`appid`/`key`/`letter`/`name` present as the list's key field, so putting `id` first keeps that automatic and content-key diffing cheap. |
| `games[].name` | string | `game.Name`. |
| `games[].lastPlayed` | string, ISO 8601 | The **merged** last-played time (below), not Playnite's raw `LastActivity` -- round-trip format (`"o"`) so it parses unambiguously regardless of the reading machine's culture. |
| `games[].cover` | string, absolute path | `PlayniteApi.Database.GetFullFilePath(game.CoverImage)`. `""` when `game.CoverImage` is null or empty (do not drop the game from the list over a missing cover; DeskWall's `image` component draws its own fallback plate for an empty/unreadable source, same as a missing local file). |
| `games[].installed` | bool | `game.IsInstalled`. Always `true` in practice, since the query below filters to installed games, but write the real value rather than a hardcoded `true` in case that filter ever loosens. |
| `games[].playtimeMinutes` | integer | `game.Playtime` is **seconds**; write `(int)Math.Round(game.Playtime / 60.0)`. |
| `games[].launch` | string | A ready-to-run command line: the currently running Playnite executable's own path, quoted, plus `--start <id>` -- see "The launch command" below. This is what the `recent-games` widget's `shortcut` component points a desktop icon at verbatim; DeskWall never parses it. |

## Selecting and ordering the 8 games

Pseudocode (the real code should split this from the `IPlayniteAPI` calls so it is unit-testable
without Playnite running, the way `poc/data.ps1`'s merge step is a plain loop over plain objects):

```csharp
var steamLast = ReadSteamLastPlayed();               // appid (string) -> DateTimeOffset, below

var rows =
    from g in playniteApi.Database.Games
    where g.IsInstalled && !g.Hidden
    let playniteLast = g.LastActivity.HasValue
        ? (DateTimeOffset?)new DateTimeOffset(g.LastActivity.Value)
        : null
    let steamAppId = g.PluginId == SteamPluginId ? g.GameId : null
    let merged = Merge(playniteLast, steamAppId, steamLast)
    where merged.HasValue
    orderby merged.Value descending
    select new { Game = g, LastPlayed = merged.Value };

var top8 = rows.Take(8).ToList();
```

`Merge` is exactly `poc/data.ps1`'s rule, restated: Steam's own time wins only when it is later
than Playnite's (or Playnite has none at all).

```csharp
static DateTimeOffset? Merge(DateTimeOffset? playniteLast, string? steamAppId,
    IReadOnlyDictionary<string, DateTimeOffset> steamLast)
{
    if (steamAppId != null && steamLast.TryGetValue(steamAppId, out var steamTime))
        if (playniteLast is null || steamTime > playniteLast.Value)
            return steamTime;
    return playniteLast;
}
```

`SteamPluginId` is the Steam library plugin's well-known GUID, the same one `poc/data.ps1` and
`scripts/playnite-recent.ps1` use: `CB91DFC9-B977-43BF-8E70-55F46E410FAB` (compare
case-insensitively; `Guid` equality already ignores case). `g.GameId` is the Steam appid as a
string only when `g.PluginId` is that GUID -- for any other plugin, `GameId` is a different
library's own identifier and must not be looked up in `steamLast`.

## The Steam merge: reading `localconfig.vdf`

Playnite only records `LastActivity` for sessions **it** launched. A game started from Steam's own
UI (or from a Steam Deck, a different PC, etc.) never touches Playnite's database, so its real
last-played time only exists in Steam's own `localconfig.vdf`. This is the one gotcha CLAUDE.md
calls out by name (see "POC gotchas... Playnite" and "Open threads (POC-specific)"), and it has no
better fix than a regex scrape -- there is no supported Steam API for "did I play this locally
recently" that does not require the same Web API key this whole lane exists to avoid.

```csharp
static readonly Regex SteamAppBlock = new Regex(
    "\"(\\d+)\"\\s*\\{[^}]*?\"LastPlayed\"\\s*\"(\\d+)\"", RegexOptions.Compiled);

static IReadOnlyDictionary<string, DateTimeOffset> ReadSteamLastPlayed()
{
    var result = new Dictionary<string, DateTimeOffset>();
    try
    {
        const string userdata = @"C:\Program Files (x86)\Steam\userdata";
        if (!Directory.Exists(userdata)) return result;   // no Steam on this machine: not an error
        var file = Directory.EnumerateFiles(userdata, "localconfig.vdf", SearchOption.AllDirectories)
            .Select(p => new FileInfo(p))
            .OrderByDescending(f => f.Length)
            .FirstOrDefault();
        if (file is null) return result;
        var text = File.ReadAllText(file.FullName);
        foreach (Match m in SteamAppBlock.Matches(text))
        {
            var appId = m.Groups[1].Value;
            var unixSeconds = long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            result[appId] = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToLocalTime();
        }
    }
    catch (Exception ex) { logger.Warn(ex, "Hearth: could not read Steam's localconfig.vdf"); }
    return result;
}
```

Ported line for line from `poc/data.ps1`'s `$steamLast` block: multiple Steam profiles under
`userdata` (one per Steam account that has signed in on this PC) each have their own
`config\localconfig.vdf`; the largest file is the one actually in use, exactly as the POC picks
it (`Sort-Object Length -Descending | Select-Object -First 1`). `"LastPlayed"` appears once per
app block (`"<appid>" { ... "LastPlayed" "<unix seconds>" ... }`), and the regex is deliberately
non-greedy (`[^}]*?`) so it matches up to the *first* `LastPlayed` inside that specific app's
braces rather than running past the closing `}` into the next app's block.

**Known limitation, carried over from the POC on purpose:** the regex assumes no nested `{ }`
block appears before `"LastPlayed"` inside an app's entry. It has held for every `localconfig.vdf`
seen on JOES-PC; if Valve ever nests something before `LastPlayed`, this silently stops picking up
that app's Steam time (not a crash -- the game just falls back to Playnite's own `LastActivity`,
or drops out of the top 8 if it has none). Do not "fix" this by parsing the whole VDF into a real
tree unless it actually breaks; the payoff is not there for eight numbers scraped once per event.

## The launch command

```csharp
var exe = Process.GetCurrentProcess().MainModule.FileName;   // running inside Playnite right now
var launch = $"\"{exe}\" --start {game.Id}";
```

Use the **currently running** executable's own path rather than hardcoding
`Playnite.DesktopApp.exe` the way `scripts/playnite-recent.ps1` does: the add-on can just as
easily be loaded inside `Playnite.FullscreenApp.exe`, and the two are different executables at
different paths. Reading `Process.GetCurrentProcess().MainModule.FileName` is always the one that
is actually installed and actually running, with no assumption about which mode the owner uses.

## Atomic write

Temp file in the **same directory** as the target (so the rename that follows is same-volume and
therefore atomic), then replace:

```csharp
var feedsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "DeskWall", "feeds");
Directory.CreateDirectory(feedsDir);
var target = Path.Combine(feedsDir, "hearth-recent.json");
var temp = target + ".tmp";

File.WriteAllText(temp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

if (File.Exists(target))
    File.Replace(temp, target, destinationBackupFileName: null);   // atomic same-volume replace
else
    File.Move(temp, target);                                       // first write: nothing to replace
```

**Playnite plugins target .NET Framework 4.6.2** (the SDK's own minimum): `File.Move(string,
string, bool)` with an overwrite flag is a .NET 5 / .NET Standard 2.1 addition and is **not
available** on 4.6.2, so a naive `File.Move(temp, target, true)` will not compile. `File.Replace`
has existed since .NET Framework 1.1 and is the correct atomic primitive here -- it fails if
`temp` and `target` are on different volumes, which they never are since `temp` is written
beside `target`. Do not `File.Delete(target)` then `File.Move(temp, target)`: that reopens the
same window this whole exercise exists to close (a reader's `file` source, mid-refresh, briefly
sees "no file" and throws -- survivable, spec 3.2 keeps the previous values, but needless).

## Error handling

- **Every one of the four event handlers catches everything** `FeedWriter.Write` can throw and
  logs it (`logger.Error(ex, "...")`, the SDK's own `ILogger` via `api.CreateLogger()`) rather
  than letting it propagate. Nothing this add-on does may crash Playnite or block a game
  launching -- a wrong or stale wallpaper feed is a rounding error next to that.
- **`ReadSteamLastPlayed` catches its own failures internally** (shown above) and returns an
  empty map rather than throwing, so a Steam install that is missing, moved or mid-update never
  stops Playnite-only games from being written; those games simply keep whatever `LastActivity`
  Playnite itself recorded.
- **A missing or unreadable `game.CoverImage`** is not an error: write `""` for `cover` (above),
  never skip the game or throw.
- **Do not write a partial or empty `games` list over a good one on a genuine failure.** If the
  database query itself throws (Playnite's own `Database.Games` should not, but guard anyway),
  let that propagate to the one outer `try/catch` in the event handler and skip the write
  entirely for this event -- the *previous* `hearth-recent.json` stays on disk and DeskWall keeps
  showing it, which is exactly the "last good value never overwritten by a failure" rule
  `docs/sources.md` already applies on the DeskWall side (spec 3.2). A write that *does* complete
  should always be a complete, self-consistent record: never write `games` in two separate passes
  where a crash between them could leave the file half old, half new (the atomic rename above
  already gives this for free -- the temp file is fully built in memory as one JSON string before
  anything touches disk).
- **No exception should ever be silently eaten with no trace.** Every `catch` above logs; none of
  them are bare `catch { }`.

## Suggested shape for the plugin project

A `GenericPlugin` (`Playnite.SDK.Plugins.GenericPlugin`), created with Playnite's own `Toolbox.exe
new GenericPlugin "Hearth" <path>` so the manifest and project scaffolding match whatever SDK
version is installed. Manifest (`extension.yaml`, next to the built DLL):

```yaml
Id: <a new GUID -- Toolbox generates one>
Name: Hearth
Author: Joe Pitts
Version: 1.0.0
Module: Hearth.dll
Type: GenericPlugin
```

Suggested internal split (keeps the merge/selection logic unit-testable without a running
Playnite, the way `poc/data.ps1`'s own merge step is a plain loop over plain objects with no
LiteDB or `IPlayniteAPI` in it):

| Type | Job |
|---|---|
| `HearthPlugin : GenericPlugin` | The four event overrides; each is one `try/catch` around a call into `FeedWriter`. |
| `FeedWriter` | Takes `IPlayniteAPI`, does the `Database.Games` query and the `GetFullFilePath`/`MainModule.FileName` calls, builds the plain data (below), hands it to `FeedSerializer`, and does the atomic write. |
| `SteamLastPlayed` | `ReadSteamLastPlayed()` above -- no Playnite types in its signature, so it is testable against a hand-written `localconfig.vdf` fixture. |
| `HearthFeed`, `HearthGame` | Plain POCOs mirroring the JSON schema exactly (field names and casing matter: DeskWall's JSON parsing is case-insensitive on keys, so casing is a style choice, not a requirement -- but match the example above for a human diffing the file). Serialize with whatever JSON library the SDK already pulls in (`Newtonsoft.Json`, which every Playnite host process has loaded) rather than adding a new dependency. |

## What to verify once this is built on JOES-PC

1. Start a Playnite-launched game, stop it: `%LOCALAPPDATA%\DeskWall\feeds\hearth-recent.json`
   updates within the same second, `games[0]` is that game, `lastPlayed` is close to now.
2. Launch a game from Steam's own client directly (not through Playnite) and quit it, then cause
   any Hearth event to fire again (start/stop a different game, or restart Playnite so
   `OnApplicationStarted` fires): that Steam-launched game's `lastPlayed` reflects the Steam
   session even though Playnite never saw it start.
3. Hide a game in Playnite: it drops out of `games` on the next write.
4. Uninstall a game that was in the top 8: it drops out; the next-most-recent installed game
   takes its place, `games` never exceeds 8 entries.
5. Point `deskwall tick --layout <a layout using the recent-games widget> --force --measure` (or
   register it with `deskwall layouts set`) at the real feed once it exists, and confirm the
   covers and launch targets are correct -- this is the same widget already proved against a
   fake feed in this lane (`docs/superpowers/plans/2026-09-30-lane-hearth-report.md`).
