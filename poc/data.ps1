# Shared data for the DeskWall dry run.
# Outputs $Games (top N installed, most recent first), $Disks and $CoverRects.
# Recency = later of Playnite LastActivity and Steam's own LastPlayed (localconfig.vdf),
# because Playnite only records sessions it launched itself.
# Playnite locks games.db exclusively while running, so a successful read is cached to
# state.json and used as the fallback when the database cannot be opened.
param([int]$Top = 4)

Add-Type -AssemblyName System.Drawing
$lib = "$env:APPDATA\Playnite\library"
# runtime state (cache, rendered wallpaper, blank icon) lives outside the repo
$DataDir = "$env:LOCALAPPDATA\DeskWall"
if (-not (Test-Path $DataDir)) { New-Item -ItemType Directory $DataDir | Out-Null }
$cachePath = Join-Path $DataDir 'state.json'
$steamPlugin = 'CB91DFC9-B977-43BF-8E70-55F46E410FAB'

# Steam last-played per appid (always readable)
$steamLast = @{}
$lc = Get-ChildItem 'C:\Program Files (x86)\Steam\userdata\*\config\localconfig.vdf' -EA SilentlyContinue | Sort-Object Length -Descending | Select-Object -First 1
if ($lc) {
  $txt = Get-Content $lc.FullName -Raw
  foreach ($m in [regex]::Matches($txt, '"(\d+)"\s*\{[^}]*?"LastPlayed"\s*"(\d+)"')) {
    $steamLast[$m.Groups[1].Value] = [DateTimeOffset]::FromUnixTimeSeconds([long]$m.Groups[2].Value).LocalDateTime
  }
}

# Playnite library: live read if possible, else cache
$all = $null; $DataSource = 'live'
try {
  [Reflection.Assembly]::LoadFrom("$env:LOCALAPPDATA\Playnite\LiteDB.dll") | Out-Null
  $db = New-Object LiteDB.LiteDatabase("Filename=$lib\games.db;ReadOnly=true")
  $all = foreach ($g in $db.GetCollection('Game').FindAll()) {
    if (-not $g['IsInstalled'].AsBoolean -or $g['Hidden'].AsBoolean) { continue }
    $last = $null
    if ($g.ContainsKey('LastActivity') -and $g['LastActivity'].IsDateTime) { $last = $g['LastActivity'].AsDateTime.ToLocalTime() }
    $cover = $null; if ($g.ContainsKey('CoverImage')) { $cover = Join-Path "$lib\files" $g['CoverImage'].AsString }
    $appid = $null; if ($g['PluginId'].AsGuid.ToString().ToUpper() -eq $steamPlugin) { $appid = $g['GameId'].AsString }
    [pscustomobject]@{ Id = $g['_id'].AsGuid.ToString(); Name = $g['Name'].AsString; PlayniteLast = $last; SteamAppId = $appid; Cover = $cover }
  }
  $db.Dispose()
  # dates stored as ISO strings; PS 5.1 ConvertTo-Json otherwise expands DateTime into an object
  @($all | Select-Object Id, Name, SteamAppId, Cover, @{ n = 'PlayniteLast'; e = { if ($_.PlayniteLast) { $_.PlayniteLast.ToString('o') } else { $null } } }) | ConvertTo-Json -Depth 3 | Set-Content $cachePath -Encoding UTF8
} catch {
  if (-not (Test-Path $cachePath)) { throw "Playnite database is locked and no cache exists at $cachePath. Close Playnite once to seed it." }
  $DataSource = 'cache ' + (Get-Item $cachePath).LastWriteTime.ToString('yyyy-MM-dd HH:mm')
  # PS 5.1 ConvertFrom-Json emits a JSON array as ONE pipeline object; assign first, then foreach enumerates it
  $parsed = Get-Content $cachePath -Raw | ConvertFrom-Json
  $all = foreach ($x in $parsed) {
    $pl = $null
    if ($x.PlayniteLast -is [datetime]) { $pl = $x.PlayniteLast }
    elseif ($x.PlayniteLast) { $pl = [datetime]::Parse([string]$x.PlayniteLast, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind) }
    [pscustomobject]@{ Id = $x.Id; Name = $x.Name; PlayniteLast = $pl; SteamAppId = $x.SteamAppId; Cover = $x.Cover }
  }
}

# merge recency
$rows = foreach ($r in $all) {
  $last = $r.PlayniteLast; $source = 'playnite'
  if ($r.SteamAppId -and $steamLast.ContainsKey($r.SteamAppId) -and ($null -eq $last -or $steamLast[$r.SteamAppId] -gt $last)) { $last = $steamLast[$r.SteamAppId]; $source = 'steam' }
  if ($null -eq $last) { continue }
  [pscustomobject]@{ Id = $r.Id; Name = $r.Name; Last = $last; Source = $source; Cover = $r.Cover }
}

$Games = @($rows | Sort-Object Last -Descending | Select-Object -First $Top)
$Disks = @(foreach ($d in (Get-PSDrive C, D)) { [pscustomobject]@{ Letter = $d.Name; FreeGB = [math]::Round($d.Free/1GB); UsedPct = $d.Used/($d.Used+$d.Free) } })

# column geometry shared by renderer and shortcut placer (3440x1440, taskbar 48)
# top-down: clock (ClockH) | gap | covers | ... | disks block anchored to the bottom
# 172px wide so four 2:3 covers (258 tall) fit between clock and disks: 142 + 4*258 + 3*14 = 1216 <= 1236
$CanvasW = 3440; $CanvasH = 1440; $Taskbar = 48
$ColW = 172; $ColLeft = $CanvasW - 48 - $ColW; $Gap = 14
$ClockTop = 48; $ClockH = 70
$ColTop = $ClockTop + $ClockH + 24
$DisksH = 92; $DisksTop = $CanvasH - $Taskbar - 40 - $DisksH
$GamesH = $DisksTop - 24 - $ColTop
# widget rects (tile-local drawing happens at 0,0; the compositor blits at X,Y)
$Rects = @{
  clock = [pscustomobject]@{ X = $ColLeft; Y = $ClockTop; W = $ColW; H = $ClockH }
  games = [pscustomobject]@{ X = $ColLeft; Y = $ColTop;   W = $ColW; H = $GamesH }
  disks = [pscustomobject]@{ X = $ColLeft; Y = $DisksTop; W = $ColW; H = $DisksH }
}
$CoverRects = @()
$y = $ColTop
foreach ($gm in $Games) {
  $h = [int]($ColW * 1.5)
  if ($gm.Cover -and (Test-Path $gm.Cover)) { $c = [Drawing.Image]::FromFile($gm.Cover); $h = [int]($ColW * $c.Height / $c.Width); $c.Dispose() }
  $CoverRects += [pscustomobject]@{ X = $ColLeft; Y = $y; W = $ColW; H = $h }
  $y += $h + $Gap
}
