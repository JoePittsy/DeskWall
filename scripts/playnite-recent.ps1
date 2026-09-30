#Requires -Version 5.1
# A one-shot command source. Never closes Playnite and never opens the original database.
param(
    [string]$InstallDir = "$env:LOCALAPPDATA\Playnite",
    [string]$LibraryDir = "$env:APPDATA\Playnite\library",
    [string]$RuntimeDir = $(if ($env:DESKWALL_HOME) { $env:DESKWALL_HOME } else { "$env:LOCALAPPDATA\DeskWall" })
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$cache = Join-Path $RuntimeDir 'playnite-recent.json'
$temp = Join-Path ([IO.Path]::GetTempPath()) ('deskwall-playnite-' + [guid]::NewGuid().ToString('N') + '.db')
$db = $null
try {
    $dll = Join-Path $InstallDir 'LiteDB.dll'
    $original = Join-Path $LibraryDir 'games.db'
    if (-not (Test-Path -LiteralPath $dll) -or -not (Test-Path -LiteralPath $original)) {
        throw 'Playnite is not installed at the configured paths.'
    }
    Copy-Item -LiteralPath $original -Destination $temp
    [Reflection.Assembly]::LoadFrom($dll) | Out-Null
    $db = New-Object LiteDB.LiteDatabase("Filename=$temp;ReadOnly=true")
    $rows = @(foreach ($game in $db.GetCollection('Game').FindAll()) {
        if (-not $game.ContainsKey('IsInstalled') -or -not $game['IsInstalled'].AsBoolean) { continue }
        if ($game.ContainsKey('Hidden') -and $game['Hidden'].AsBoolean) { continue }
        if (-not $game.ContainsKey('LastActivity') -or -not $game['LastActivity'].IsDateTime) { continue }
        $id = $game['_id'].AsGuid.ToString()
        $cover = if ($game.ContainsKey('CoverImage')) { Join-Path (Join-Path $LibraryDir 'files') $game['CoverImage'].AsString } else { '' }
        [pscustomobject]@{ id=$id; name=$game['Name'].AsString; last=$game['LastActivity'].AsDateTime.ToString('o');
            cover=$cover; target=('"' + (Join-Path $InstallDir 'Playnite.DesktopApp.exe') + '" --start ' + $id) }
    })
    $games = @($rows | Sort-Object last -Descending | Select-Object -First 4)
    $result = [ordered]@{ games=$games; left=@($games | Select-Object -First 2); right=@($games | Select-Object -Skip 2); status='live' }
    $json = $result | ConvertTo-Json -Depth 5 -Compress
    New-Item -ItemType Directory -Force $RuntimeDir | Out-Null
    [IO.File]::WriteAllText($cache + '.tmp', $json, [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath ($cache + '.tmp') -Destination $cache -Force
    $json
} catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    if (Test-Path -LiteralPath $cache) { Get-Content -LiteralPath $cache -Raw }
    else { '{"games":[],"left":[],"right":[],"status":"unavailable"}' }
} finally {
    if ($db) { $db.Dispose() }
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Force }
}
