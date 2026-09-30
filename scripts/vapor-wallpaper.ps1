#Requires -Version 7
<#
.SYNOPSIS
  Synthesise four vaporwave wallpapers (night/dawn/day/dusk) at 3440x1440 from the ridge trace in a
  layout: gradient sky, a striped sun, the mountain as a flat silhouette, a perspective grid on the
  valley floor. The ridge path is the same one the volume bar draws, so the line lands on the edge.
#>
param(
    [string]$Layout = 'layouts/alpine-rice.json',
    [string]$Out = (Join-Path $env:LOCALAPPDATA 'DeskWall\assets\vapor'),
    [int]$W = 3440, [int]$H = 1440
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force $Out | Out-Null
$j = Get-Content $Layout -Raw | ConvertFrom-Json -Depth 20
$ridge = $j.components | Where-Object id -eq 'ridge'
$rect = $ridge.rect
# Path points -> canvas: the renderer stretches the path's bounds to the rect (inset by half the stroke + glow).
$pts = [regex]::Matches($ridge.shape, '(-?[\d.]+),(-?[\d.]+)') | ForEach-Object { [Drawing.PointF]::new([float]$_.Groups[1].Value, [float]$_.Groups[2].Value) }
$minX = ($pts.X | Measure-Object -Minimum).Minimum; $maxX = ($pts.X | Measure-Object -Maximum).Maximum
$minY = ($pts.Y | Measure-Object -Minimum).Minimum; $maxY = ($pts.Y | Measure-Object -Maximum).Maximum
$inset = [float]$ridge.thickness / 2 + [float]$ridge.glow
$sx = ($rect[2] - 2 * $inset) / ($maxX - $minX); $sy = ($rect[3] - 2 * $inset) / ($maxY - $minY)
$canvasPts = $pts | ForEach-Object { [Drawing.PointF]::new($rect[0] + $inset + ($_.X - $minX) * $sx, $rect[1] + $inset + ($_.Y - $minY) * $sy) }

function C([string]$hex) { [Drawing.ColorTranslator]::FromHtml($hex) }
$phases = @{
    night = @{ top = '#12071F'; mid = '#3A1258'; low = '#7A1F6E'; sun = '#FF4FA3'; sunLo = '#FF9A3C'; hill = '#160A2A'; floor = '#0B0618'; grid = '#3DE8FF'; gridA = 110 }
    dawn  = @{ top = '#5A2A7A'; mid = '#D65A9C'; low = '#FFB48A'; sun = '#FFD36E'; sunLo = '#FF6FA8'; hill = '#2A1240'; floor = '#1A0E2E'; grid = '#7EF9FF'; gridA = 120 }
    day   = @{ top = '#7FB7FF'; mid = '#C9A6FF'; low = '#FFC6E4'; sun = '#FFF0A8'; sunLo = '#FF9ACB'; hill = '#4A2C7A'; floor = '#2E1B4F'; grid = '#B8FFFF'; gridA = 130 }
    dusk  = @{ top = '#1C0B3A'; mid = '#8A1E7A'; low = '#FF5E8E'; sun = '#FFB347'; sunLo = '#FF3F7A'; hill = '#1E0C36'; floor = '#120724'; grid = '#01CDFE'; gridA = 140 }
}
$horizon = 1000                          # where the flat valley floor begins
$vanish = [Drawing.PointF]::new($W / 2, $horizon - 40)

foreach ($name in $phases.Keys) {
    $p = $phases[$name]
    $bmp = [Drawing.Bitmap]::new($W, $H)
    $g = [Drawing.Graphics]::FromImage($bmp); $g.SmoothingMode = 'AntiAlias'
    # Sky: three-stop vertical gradient.
    $sky = [Drawing.Drawing2D.LinearGradientBrush]::new([Drawing.Point]::new(0, 0), [Drawing.Point]::new(0, $horizon), (C $p.top), (C $p.low))
    $blend = [Drawing.Drawing2D.ColorBlend]::new(3); $blend.Colors = @((C $p.top), (C $p.mid), (C $p.low)); $blend.Positions = @(0.0, 0.55, 1.0); $sky.InterpolationColors = $blend
    $g.FillRectangle($sky, 0, 0, $W, $horizon)
    # Sun: a big disc, lower half cut by horizontal bands of sky.
    $cx = 2560; $cy = 640; $r = 330
    $sunBrush = [Drawing.Drawing2D.LinearGradientBrush]::new([Drawing.Point]::new(0, $cy - $r), [Drawing.Point]::new(0, $cy + $r), (C $p.sun), (C $p.sunLo))
    $g.FillEllipse($sunBrush, $cx - $r, $cy - $r, 2 * $r, 2 * $r)
    $y = $cy + 20; $band = 6
    while ($y -lt $cy + $r) { $g.FillRectangle($sky, $cx - $r - 2, $y, 2 * $r + 4, $band); $y += $band + 34 - [int]($band * 1.2); $band += 5 }
    # Mountain silhouette from the ridge trace, flat colour, down to the horizon.
    $poly = [System.Collections.Generic.List[Drawing.PointF]]::new(); $poly.Add([Drawing.PointF]::new(0, $canvasPts[0].Y)); foreach ($cp in $canvasPts) { $poly.Add($cp) }; $poly.Add([Drawing.PointF]::new($W, $canvasPts[-1].Y)); $poly.Add([Drawing.PointF]::new($W, $horizon)); $poly.Add([Drawing.PointF]::new(0, $horizon))
    $g.FillPolygon([Drawing.SolidBrush]::new((C $p.hill)), $poly.ToArray())
    # Valley floor with a perspective grid.
    $g.FillRectangle([Drawing.SolidBrush]::new((C $p.floor)), 0, $horizon, $W, $H - $horizon)
    $gc = C $p.grid; $pen = [Drawing.Pen]::new([Drawing.Color]::FromArgb($p.gridA, $gc.R, $gc.G, $gc.B), 2)
    for ($i = -14; $i -le 14; $i++) { $g.DrawLine($pen, $vanish.X + $i * 60, $horizon, $vanish.X + $i * 900, $H) }
    $yy = $horizon + 6; $step = 10
    while ($yy -lt $H) { $g.DrawLine($pen, 0, $yy, $W, $yy); $step = [int]($step * 1.28); $yy += $step }
    $glowPen = [Drawing.Pen]::new([Drawing.Color]::FromArgb(90, $gc.R, $gc.G, $gc.B), 6); $g.DrawLine($glowPen, 0, $horizon, $W, $horizon)
    $g.Dispose()
    $enc = [Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object MimeType -eq 'image/jpeg'
    $ep = [Drawing.Imaging.EncoderParameters]::new(1); $ep.Param[0] = [Drawing.Imaging.EncoderParameter]::new([Drawing.Imaging.Encoder]::Quality, [long]92)
    $path = Join-Path $Out "ridge-$name.jpg"; $bmp.Save($path, $enc, $ep); $bmp.Dispose()
    "$name  $path"
}
