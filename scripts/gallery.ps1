#Requires -Version 5.1
<#
.SYNOPSIS
  Render the layout in ten pinned states and save half-size PNGs, so a change can be judged
  against midnight, a storm or a full drive without waiting for one.

.DESCRIPTION
  Copies the layout (default: the live one) into a scratch home with the shipped assets and
  recipes, then runs one `deskwall tick --preview ... --force --no-apply --no-shortcuts` per scene
  and scales the frame to 1720x720 into -Out\<scene>.png. Every scene starts from the same
  baseline pins (clear, noon, quiet machine, nothing playing) and changes only what it is about.
  Finishes with -Out\all.png, a 2 x 5 montage. Never touches the live wallpaper or desktop.

.EXAMPLE
  .\scripts\gallery.ps1 -Layout layouts\alpine-vision.json
  .\scripts\gallery.ps1 -Exe "$env:LOCALAPPDATA\Programs\DeskWall\deskwall.exe"
  .\scripts\gallery.ps1 -Layout layouts\alpine-vision.json -Only storm,snow
#>
param(
    [string]$Layout = (Join-Path $env:LOCALAPPDATA 'DeskWall\column-system.json'),
    [string]$Exe = '',
    [string]$Scratch = (Join-Path $env:TEMP 'dw-gallery'),
    [string]$Out = '',
    [string[]]$Only = @()
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not $Exe) { $Exe = Join-Path $repo 'src\DeskWall.Daemon\bin\Debug\net10.0-windows10.0.19041.0\deskwall.exe' }
if (-not $Out) { $Out = Join-Path $repo 'docs\superpowers\plans\gallery' }
if (-not (Test-Path -LiteralPath $Exe)) { throw "No deskwall.exe at $Exe; build first or pass -Exe." }
if (-not (Test-Path -LiteralPath $Layout)) { throw "No layout at $Layout." }
$Layout = (Resolve-Path -LiteralPath $Layout).Path
if ($Scratch -ieq (Join-Path $env:LOCALAPPDATA 'DeskWall')) { throw 'The scratch home must not be the live runtime dir.' }

Add-Type -AssemblyName System.Drawing

# ---- scratch home ------------------------------------------------------------------------
New-Item -ItemType Directory -Force -Path $Scratch, $Out | Out-Null
foreach ($d in 'assets', 'scripts', 'widgets', 'media') { New-Item -ItemType Directory -Force -Path (Join-Path $Scratch $d) | Out-Null }
Copy-Item -Recurse -Force -Path (Join-Path $repo 'assets\*') -Destination (Join-Path $Scratch 'assets')
foreach ($s in 'playnite-recent.ps1', 'tailscale-lights.ps1') {
    Copy-Item -Force -LiteralPath (Join-Path $repo "scripts\$s") -Destination (Join-Path $Scratch 'scripts')
}
$liveWidgets = Join-Path $env:LOCALAPPDATA 'DeskWall\widgets'
if (Test-Path -LiteralPath $liveWidgets) { Copy-Item -Force -Path (Join-Path $liveWidgets '*.json') -Destination (Join-Path $Scratch 'widgets') }
$layoutCopy = Join-Path $Scratch 'layout.json'
Copy-Item -Force -LiteralPath $Layout -Destination $layoutCopy

# ---- placeholder album art -----------------------------------------------------------------
$art = Join-Path $Scratch 'media\placeholder-art.png'
$bmp = New-Object System.Drawing.Bitmap 400, 400
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'
$rect = New-Object System.Drawing.Rectangle 0, 0, 400, 400
$grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(255, 255, 94, 58)), ([System.Drawing.Color]::FromArgb(255, 88, 36, 160)), 45
$g.FillRectangle($grad, $rect)
$ring = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(220, 255, 236, 200)), 18
$g.DrawEllipse($ring, 90, 90, 220, 220)
$g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 20, 12, 40))), 170, 170, 60, 60)
$g.Dispose(); $bmp.Save($art, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()

# ---- scenes --------------------------------------------------------------------------------
# One baseline, so a scene differs from noon only in what it is about.
$baseline = @(
    'time.at=12:00',
    'weather.json.current.weather_code=0', 'weather.json.current.temperature_2m=14', 'weather.json.current.is_day=1',
    'audio.volume=0.65', 'audio.muted=false',
    'hardware.cpu=0.2', 'hardware.ram=0.45', 'hardware.gpu=0.12', 'hardware.gpuTempFraction=0.42',
    'disks.worstUsedFraction=0.6',
    'battery.fraction=1', 'battery.percent=100', 'battery.charging=false', 'battery.onBattery=false', 'battery.opacity=0.25',
    'media.playing=false', 'media.title=', 'media.artist=', 'media.art=', 'media.progress=0',
    'system.pendingReboot=false', 'system.daysSinceCrash=30'
) -join ','

$scenes = [ordered]@{
    'midnight-clear'       = 'time.at=00:30,weather.json.current.is_day=0'
    'dawn'                 = 'time.at=06:00'
    'noon'                 = 'time.at=12:00'
    'dusk'                 = 'time.at=18:30'
    'storm'                = 'time.dayFraction=0.6,weather.json.current.weather_code=65,weather.json.current.temperature_2m=9'
    'thunder'              = 'time.at=22:40,weather.json.current.weather_code=95,weather.json.current.is_day=0,weather.json.current.temperature_2m=11'
    'snow'                 = 'time.at=15:30,weather.json.current.weather_code=75,weather.json.current.temperature_2m=-3'
    'machine-on-fire'      = 'time.at=21:30,weather.json.current.is_day=0,hardware.cpu=0.95,hardware.gpu=0.97,hardware.gpuTempFraction=0.95,hardware.ram=0.9,battery.fraction=0.15,battery.percent=15,battery.onBattery=true,battery.opacity=1'
    'drive-full-and-muted' = 'time.at=16:00,disks.worstUsedFraction=0.95,disks.drives.0.usedFraction=0.95,disks.drives.0.freeGB=12,audio.muted=true,system.pendingReboot=true,system.daysSinceCrash=2'
    'now-playing'          = "time.at=20:10,media.playing=true,media.title=Midnight City,media.artist=M83,media.art=$art,media.progress=0.42,media.position=102,media.duration=243"
}

$frame = Join-Path $Scratch 'deskwall.jpg'
$log = Join-Path $Scratch 'gallery-tick.log'
$rendered = @()
# "-Only a,b" through powershell.exe -File arrives as one string; split it either way.
$onlyScenes = @($Only | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
foreach ($name in $scenes.Keys) {
    if ($onlyScenes.Count -gt 0 -and $onlyScenes -notcontains $name) { continue }
    $pins = $baseline + ',' + $scenes[$name]
    $argLine = "--home `"$Scratch`" tick --layout `"$layoutCopy`" --force --no-apply --no-shortcuts --measure --preview `"$pins`""
    if (Test-Path -LiteralPath $frame) { Remove-Item -LiteralPath $frame }
    $p = Start-Process -FilePath $Exe -ArgumentList $argLine -Wait -NoNewWindow -PassThru -RedirectStandardOutput $log -RedirectStandardError "$log.err"
    $draw = (Select-String -LiteralPath $log -Pattern '^draw\s+(\d+)' | Select-Object -First 1)
    if ($p.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $frame)) {
        Get-Content -LiteralPath "$log.err" | Write-Warning
        throw "Scene $name failed (exit $($p.ExitCode))."
    }
    $src = [System.Drawing.Image]::FromFile($frame)
    try {
        $half = New-Object System.Drawing.Bitmap 1720, 720
        $hg = [System.Drawing.Graphics]::FromImage($half)
        $hg.InterpolationMode = 'HighQualityBicubic'
        $hg.DrawImage($src, 0, 0, 1720, 720)
        $hg.Dispose()
        $png = Join-Path $Out "$name.png"
        $half.Save($png, [System.Drawing.Imaging.ImageFormat]::Png)
        $half.Dispose()
    } finally { $src.Dispose() }
    $rendered += $name
    Write-Host ("{0,-22} {1}  ({2})" -f $name, $png, $(if ($draw) { $draw.Line.Trim() } else { 'no timing' }))
}

# ---- montage -------------------------------------------------------------------------------
$all = @($scenes.Keys | Where-Object { Test-Path -LiteralPath (Join-Path $Out "$_.png") })
$cellW = 860; $cellH = 360; $cols = 2; $rows = [Math]::Ceiling($all.Count / $cols)
$m = New-Object System.Drawing.Bitmap ($cellW * $cols), ($cellH * $rows)
$mg = [System.Drawing.Graphics]::FromImage($m)
$mg.InterpolationMode = 'HighQualityBicubic'
$mg.Clear([System.Drawing.Color]::Black)
$font = New-Object System.Drawing.Font 'Segoe UI', 15, ([System.Drawing.FontStyle]::Bold)
$shadow = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(200, 0, 0, 0))
for ($i = 0; $i -lt $all.Count; $i++) {
    $x = ($i % $cols) * $cellW; $y = [Math]::Floor($i / $cols) * $cellH
    $img = [System.Drawing.Image]::FromFile((Join-Path $Out "$($all[$i]).png"))
    $mg.DrawImage($img, $x, $y, $cellW, $cellH)
    $img.Dispose()
    $mg.FillRectangle($shadow, $x, $y + $cellH - 30, 260, 30)
    $mg.DrawString($all[$i], $font, [System.Drawing.Brushes]::White, $x + 8, $y + $cellH - 29)
}
$mg.Dispose()
$m.Save((Join-Path $Out 'all.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$m.Dispose()
Write-Host "montage $(Join-Path $Out 'all.png') ($($all.Count) scenes, rendered $($rendered.Count))"
