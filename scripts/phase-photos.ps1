#Requires -Version 5.1
<#
.SYNOPSIS
  Grade the one dusk photo of the ridge into dawn, day and night variants, so a layout can bind
  its baseImage to time.phase.

.DESCRIPTION
  Honest grades of the same frame: nothing moves, so the ridge trace in the layout still lands.
  The sky/land split comes from that trace (the 'ridge' bar's shape in -Layout, mapped into its
  rect exactly as Surface.DrawPath maps it), feathered, so the sky can be graded apart from the
  mountains without a hand-painted mask.

    night  cool, much darker, highlights (the snow) kept a little brighter than the rest, as under
           a moon. No stars: those are the layout's job.
    dawn   a rose lift on the sky, strongest at the horizon, and a warm rim on the lit faces just
           below the ridge line; the valley stays cool and dim.
    day    neutral and brighter: the warm cast taken out, the sky pulled toward a pale blue.
    dusk   the photo as shot, re-encoded at the same size and quality as the others.

  Writes <Home>\assets\alpine\ridge-<phase>.jpg at 3440x1440, JPEG quality 92. -Home defaults to
  $env:DESKWALL_HOME and refuses the live runtime dir unless -AllowLive is passed.

.EXAMPLE
  $env:DESKWALL_HOME = "$env:TEMP\dw-photo-home"; .\scripts\phase-photos.ps1
  .\scripts\phase-photos.ps1 -Home "$env:TEMP\dw-gallery" -Only night,dawn
#>
param(
    [string]$Source = (Join-Path $env:USERPROFILE 'Downloads\5168918.jpg'),
    [Alias('Home')][string]$RuntimeHome = $env:DESKWALL_HOME,
    [string]$Layout = '',
    [string]$RidgeId = 'ridge',
    [string[]]$Only = @(),
    [int]$Width = 3440,
    [int]$Height = 1440,
    [int]$Quality = 92,
    # Also write ridge-<phase>-check.png at half size with the skyline drawn in red.
    [switch]$Check,
    # The owner's deliberate install into %LOCALAPPDATA%\DeskWall; refused without it.
    [switch]$AllowLive
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not $Layout) { $Layout = Join-Path $repo 'layouts\alpine-vision.json' }
if (-not $RuntimeHome) { throw 'Pass -Home or set DESKWALL_HOME to a scratch runtime dir.' }
$live = Join-Path $env:LOCALAPPDATA 'DeskWall'
if (-not $AllowLive -and (Resolve-Path -LiteralPath (New-Item -ItemType Directory -Force -Path $RuntimeHome)).Path.TrimEnd('\') -ieq $live) {
    throw 'Refusing to write into the live runtime dir; point -Home / DESKWALL_HOME at a scratch folder.'
}
if (-not (Test-Path -LiteralPath $Source)) { throw "No source photo at $Source." }

Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public static class PhaseGrade
{
    static double Clamp(double v) { return v < 0 ? 0 : v > 1 ? 1 : v; }
    static double Smooth(double e0, double e1, double x) { var t = Clamp((x - e0) / (e1 - e0)); return t * t * (3 - 2 * t); }
    static double Mix(double a, double b, double t) { return a + (b - a) * t; }

    // skyline[x] is the ridge's y at column x. Sky is above it; feather is the soft edge in px.
    public static void Grade(Bitmap bmp, double[] skyline, string phase, double feather)
    {
        int w = bmp.Width, h = bmp.Height;
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        var row = new byte[data.Stride];
        try
        {
            for (int y = 0; y < h; y++)
            {
                var ptr = data.Scan0 + y * data.Stride;
                Marshal.Copy(ptr, row, 0, data.Stride);
                double vy = (double)y / (h - 1);
                for (int x = 0; x < w; x++)
                {
                    int i = x * 4;
                    double b = row[i] / 255.0, g = row[i + 1] / 255.0, r = row[i + 2] / 255.0;
                    double ridge = skyline[x];
                    // 1 in the sky, 0 in the land, feathered across the ridge line.
                    double sky = 1 - Smooth(ridge - feather, ridge + feather, y);
                    // 0 at the very top of the sky, 1 at the ridge: how close to the horizon.
                    double horizon = ridge > 1 ? Clamp(y / ridge) : 1;
                    // How far below the ridge, in px; the rim fades over the first ~200.
                    double below = y - ridge;
                    double lum = 0.2126 * r + 0.7152 * g + 0.0722 * b;
                    double nr = r, ng = g, nb = b;

                    if (phase == "night")
                    {
                        // Desaturate toward a blue-grey, darken with a toe so snow stays readable.
                        double s = 0.45;
                        nr = Mix(lum, r, s); ng = Mix(lum, g, s); nb = Mix(lum, b, s);
                        double k = Math.Pow(Clamp(lum), 1.35) / Math.Max(lum, 1e-4);   // gamma on luminance only
                        double exposure = Mix(0.50, 0.40, sky);
                        nr *= k * exposure * 0.72; ng *= k * exposure * 0.86; nb *= k * exposure * 1.18;
                        // The sky goes toward deep indigo, darker at the top than at the horizon.
                        double top = Mix(0.55, 0.0, horizon) * sky;
                        nr = Mix(nr, 0.030, top); ng = Mix(ng, 0.035, top); nb = Mix(nb, 0.090, top);
                        double glowAtHorizon = Math.Pow(horizon, 6) * sky * 0.35;
                        nr = Mix(nr, 0.10, glowAtHorizon); ng = Mix(ng, 0.13, glowAtHorizon); nb = Mix(nb, 0.24, glowAtHorizon);
                    }
                    else if (phase == "dawn")
                    {
                        // Land: a little dimmer and cooler than the shot, the valley bluest.
                        double land = 1 - sky;
                        double e = Mix(1.0, 0.86, land);
                        nr = r * e * Mix(1.0, 0.94, land); ng = g * e * Mix(1.0, 0.97, land); nb = b * e * Mix(1.0, 1.06, land);
                        // Sky: a rose lift, strongest at the horizon, lavender at the top.
                        double lift = Math.Pow(horizon, 1.6);
                        double tr = Mix(0.62, 1.00, lift), tg = Mix(0.56, 0.58, lift), tb = Mix(0.80, 0.66, lift);
                        double amount = sky * Mix(0.35, 0.75, lift);
                        // Screen-ish blend so the sky brightens toward the tint rather than going flat.
                        nr = Mix(nr, 1 - (1 - nr) * (1 - tr * 0.85), amount);
                        ng = Mix(ng, 1 - (1 - ng) * (1 - tg * 0.55), amount);
                        nb = Mix(nb, 1 - (1 - nb) * (1 - tb * 0.70), amount);
                        // Warm rim: bright faces just under the ridge catch the low sun.
                        double rim = land * Smooth(0.15, 0.45, lum) * (below > 0 ? Math.Exp(-below / 220.0) : 1);
                        nr = Mix(nr, Math.Min(1, nr * 1.28 + 0.06), rim);
                        ng = Mix(ng, ng * 1.02 + 0.015, rim);
                        nb = Mix(nb, nb * 0.80, rim);
                    }
                    else if (phase == "day")
                    {
                        // White balance: the shot's warm cast out (sky horizon is peach at dusk).
                        double land = 1 - sky;
                        nr = r * Mix(0.93, 1.00, land); ng = g; nb = b * Mix(1.10, 0.97, land);
                        // Brighter, shadows lifted more than highlights.
                        double lift = Mix(0.30, 0.45, land);
                        nr = 1 - Math.Pow(1 - Clamp(nr), 1 + lift * 1.6); ng = 1 - Math.Pow(1 - Clamp(ng), 1 + lift * 1.6); nb = 1 - Math.Pow(1 - Clamp(nb), 1 + lift * 1.6);
                        double l2 = 0.2126 * nr + 0.7152 * ng + 0.0722 * nb;
                        nr = Mix(l2, nr, 1.08); ng = Mix(l2, ng, 1.08); nb = Mix(l2, nb, 1.08);
                        // The sky toward a pale day blue, deeper at the top.
                        double deep = 1 - horizon;
                        double tr = Mix(0.78, 0.46, deep), tg = Mix(0.86, 0.64, deep), tb = Mix(0.94, 0.88, deep);
                        double amount = sky * 0.55;
                        nr = Mix(nr, tr, amount); ng = Mix(ng, tg, amount); nb = Mix(nb, tb, amount);
                    }

                    row[i] = (byte)Math.Round(Clamp(nb) * 255);
                    row[i + 1] = (byte)Math.Round(Clamp(ng) * 255);
                    row[i + 2] = (byte)Math.Round(Clamp(nr) * 255);
                }
                Marshal.Copy(row, 0, ptr, data.Stride);
            }
        }
        finally { bmp.UnlockBits(data); }
    }
}
'@

# ---- the skyline, from the layout's ridge trace ----------------------------------------------
function Get-Skyline([string]$layoutPath, [string]$id, [int]$w) {
    $j = Get-Content -Raw -LiteralPath $layoutPath | ConvertFrom-Json
    $c = $j.components | Where-Object { $_.id -eq $id } | Select-Object -First 1
    if (-not $c -or -not $c.shape) { throw "No component '$id' with a shape in $layoutPath." }
    $inv = [Globalization.CultureInfo]::InvariantCulture
    $pts = New-Object System.Collections.Generic.List[double[]]
    foreach ($m in [regex]::Matches($c.shape, '[ML]\s*(-?[\d.]+)[ ,]+(-?[\d.]+)')) {
        $pts.Add(@([double]::Parse($m.Groups[1].Value, $inv), [double]::Parse($m.Groups[2].Value, $inv)))
    }
    if ($pts.Count -lt 2) { throw "The '$id' shape has no polyline." }
    $x0 = ($pts | ForEach-Object { $_[0] } | Measure-Object -Minimum).Minimum
    $x1 = ($pts | ForEach-Object { $_[0] } | Measure-Object -Maximum).Maximum
    $y0 = ($pts | ForEach-Object { $_[1] } | Measure-Object -Minimum).Minimum
    $y1 = ($pts | ForEach-Object { $_[1] } | Measure-Object -Maximum).Maximum
    # Surface.DrawPath: inset = thickness/2 + glow (the bar's pad), then the path's bounds fill the rect.
    $thickness = if ($c.thickness) { [double]$c.thickness } else { 0 }
    $glow = if ($c.glow) { [double]$c.glow } else { 0 }
    $inset = $thickness / 2 + $glow
    $r = $c.rect
    $sx = ($r[2] - 2 * $inset) / ($x1 - $x0); $sy = ($r[3] - 2 * $inset) / ($y1 - $y0)
    $mapped = $pts | ForEach-Object { , @(($r[0] + $inset + ($_[0] - $x0) * $sx), ($r[1] + $inset + ($_[1] - $y0) * $sy)) } | Sort-Object { $_[0] }
    $sky = New-Object double[] $w
    $k = 0
    for ($x = 0; $x -lt $w; $x++) {
        while ($k -lt $mapped.Count - 2 -and $mapped[$k + 1][0] -lt $x) { $k++ }
        $a = $mapped[$k]; $b = $mapped[$k + 1]
        $t = if ($b[0] -ne $a[0]) { [Math]::Min(1, [Math]::Max(0, ($x - $a[0]) / ($b[0] - $a[0]))) } else { 0 }
        $sky[$x] = $a[1] + ($b[1] - $a[1]) * $t
    }
    return , $sky
}

$skyline = Get-Skyline $Layout $RidgeId $Width
$outDir = Join-Path $RuntimeHome 'assets\alpine'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$jpeg = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object { $_.MimeType -eq 'image/jpeg' }
$params = New-Object System.Drawing.Imaging.EncoderParameters 1
$params.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter ([System.Drawing.Imaging.Encoder]::Quality), ([long]$Quality)

$phases = @('night', 'dawn', 'day', 'dusk')
$onlyPhases = @($Only | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$src = [System.Drawing.Image]::FromFile((Resolve-Path -LiteralPath $Source).Path)
try {
    foreach ($phase in $phases) {
        if ($onlyPhases.Count -gt 0 -and $onlyPhases -notcontains $phase) { continue }
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $bmp = New-Object System.Drawing.Bitmap $Width, $Height, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try {
            $g = [System.Drawing.Graphics]::FromImage($bmp)
            $g.InterpolationMode = 'HighQualityBicubic'
            $g.PixelOffsetMode = 'HighQuality'
            $g.DrawImage($src, 0, 0, $Width, $Height)
            $g.Dispose()
            if ($phase -ne 'dusk') { [PhaseGrade]::Grade($bmp, $skyline, $phase, 10.0) }
            $path = Join-Path $outDir "ridge-$phase.jpg"
            $bmp.Save($path, $jpeg, $params)
            if ($Check) {
                $half = New-Object System.Drawing.Bitmap ([int]($Width / 2)), ([int]($Height / 2))
                $hg = [System.Drawing.Graphics]::FromImage($half)
                $hg.InterpolationMode = 'HighQualityBicubic'
                $hg.DrawImage($bmp, 0, 0, $half.Width, $half.Height)
                $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::Red), 1
                for ($x = 2; $x -lt $Width; $x += 2) { $hg.DrawLine($pen, ($x - 2) / 2, $skyline[$x - 2] / 2, $x / 2, $skyline[$x] / 2) }
                $hg.Dispose()
                $half.Save((Join-Path $outDir "ridge-$phase-check.png"), [System.Drawing.Imaging.ImageFormat]::Png)
                $half.Dispose()
            }
            Write-Host ("{0,-6} {1}  ({2} KB, {3} ms)" -f $phase, $path, [int]((Get-Item -LiteralPath $path).Length / 1KB), $sw.ElapsedMilliseconds)
        } finally { $bmp.Dispose() }
    }
} finally { $src.Dispose() }
