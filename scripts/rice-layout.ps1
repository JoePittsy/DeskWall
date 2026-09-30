#Requires -Version 7
<#
.SYNOPSIS
  Derive layouts/alpine-rice.json from alpine-vision-photos.json: the same features, one palette.
  Every edit checks the property exists first and prints what it could not apply, so a renamed
  key shows up here rather than as a silently ignored field.
#>
param(
    [string]$In = 'layouts/alpine-vision-photos.json',
    [string]$Out = 'layouts/alpine-rice.json',
    # 'rice' (Nord on the photo) or 'vapor' (hot pink and cyan on the synthesised wallpaper, scripts/vapor-wallpaper.ps1).
    [ValidateSet('rice', 'vapor')][string]$Theme = 'rice'
)
$ErrorActionPreference = 'Stop'
$j = Get-Content $In -Raw | ConvertFrom-Json -Depth 20
$missed = [System.Collections.Generic.List[string]]::new()

# Palette. Alpha first, as the layout writes colours.
if ($Theme -eq 'vapor') {
    $ink = '#F2F6E9FF'; $dim = '#B3B9A6D6'; $frost = '#FFFF71CE'; $second = '#FF01CDFE'
    $warn = '#FFFFB86C'; $danger = '#FFFF3860'; $track = '#40F6E9FF'
    $j.baseImage.bind = $j.baseImage.bind.Replace('assets/alpine/ridge-', 'assets/vapor/ridge-')
} else {
    $ink = '#E6ECEFF4'; $dim = '#99D8DEE9'; $frost = '#FF88C0D0'; $second = '#FF5E81AC'   # the darker Nord blue: the light one read as grey on the hills
    $warn = '#FFEBCB8B'; $danger = '#FFBF616A'; $track = '#4DD8DEE9'
}

function Get-Comp([string]$id) { $c = $j.components | Where-Object id -eq $id; if (-not $c) { $missed.Add("component $id") }; $c }
function Set-Prop($obj, [string]$name, $value, [string]$where) {
    if ($null -eq $obj) { return }
    if ($obj.PSObject.Properties[$name]) { $obj.$name = $value }
    # A property at its default is simply absent from the JSON; these are known layout keys, so add them.
    elseif ($name -in 'font','weight','align','color','size','effect','thresholdFill','thickness','glow','track','fill') { $obj | Add-Member $name $value }
    else { $missed.Add("$where.$name") }
}
# Recolour every non-transparent #AARRGGBB inside a format string: RGB to $rgb, alpha to $alpha
# (or kept when $alpha is ''). Transparent stops stay transparent so a Step/Blend keeps its shape.
function Recolour([string]$text, [string]$rgb, [string]$alpha) {
    [regex]::Replace($text, '#([0-9A-Fa-f]{2})([0-9A-Fa-f]{6})', {
        param($m)
        if ($m.Groups[1].Value -eq '00') { return "#00$rgb" }
        $a = if ($alpha) { $alpha } else { $m.Groups[1].Value }
        "#$a$rgb"
    })
}
function Recolour-Prop($obj, [string]$name, [string]$rgb, [string]$alpha, [string]$where) {
    if ($null -eq $obj) { return }
    $p = $obj.PSObject.Properties[$name]
    if (-not $p) { $missed.Add("$where.$name"); return }
    if ($p.Value -is [string]) { $obj.$name = Recolour $p.Value $rgb $alpha }
    elseif ($p.Value.PSObject.Properties['bind']) { $p.Value.bind = Recolour $p.Value.bind $rgb $alpha }
}

# 1. Delete: washes, halos, glows, the kitsch.
$drop = 'sky-grade','sky-top','sky-band','stars-big','stars-warm','sun-glow','moon-halo','heat-shimmer','lightning','snow-big','uptime-piste','valley-count'
# Vapor: heat patches speckle a flat silhouette at idle, so no snowfields.
if ($Theme -eq 'vapor') { $drop += 'gpu-snowfield','cpu-snowfield' }
$j.components = @($j.components | Where-Object { $_.id -notin $drop })
if ($Theme -eq 'vapor') {
    # The mountain as a filled shape above the sun and moon, so they set behind it. Its path is the
    # ridge's closed to the valley floor; the rect is chosen so the ridge points land exactly where
    # the ridge bar draws them (the bar insets its path by thickness/2 + glow, this shape by nothing).
    $rb = $j.components | Where-Object id -eq 'ridge'
    $pts = [regex]::Matches($rb.shape, '(-?[\d.]+),(-?[\d.]+)') | ForEach-Object { [double]$_.Groups[1].Value, [double]$_.Groups[2].Value }
    $xs = @(); $ys = @(); for ($i = 0; $i -lt $pts.Count; $i += 2) { $xs += $pts[$i]; $ys += $pts[$i + 1] }
    $minX = ($xs | Measure-Object -Minimum).Minimum; $maxX = ($xs | Measure-Object -Maximum).Maximum
    $minY = ($ys | Measure-Object -Minimum).Minimum; $maxY = ($ys | Measure-Object -Maximum).Maximum
    $inset = 3 / 2 + 12; $horizon = 1000
    $sy = ($rb.rect[3] - 2 * $inset) / ($maxY - $minY)
    $top = $rb.rect[1] + $inset
    $bottomY = $minY + ($horizon - $top) / $sy
    $inv = [Globalization.CultureInfo]::InvariantCulture
    $closed = $rb.shape.TrimEnd() + " L$($maxX.ToString('0.#', $inv)),$($bottomY.ToString('0.#', $inv)) L$($minX.ToString('0.#', $inv)),$($bottomY.ToString('0.#', $inv)) Z"
    $sil = [pscustomobject]@{
        type = 'bar'; id = 'silhouette'; rect = @([int]($rb.rect[0] + $inset), [int]$top, [int]($rb.rect[2] - 2 * $inset), [int]($horizon - $top)); z = -8
        fraction = 1; threshold = 2; track = '#00000000'; thickness = 0; shape = $closed
        fill = [pscustomobject]@{ bind = 'time.phase | "?night=#FF160A2A,dawn=#FF2A1240,day=#FF4A2C7A,*=#FF1E0C36"' }
    }
    $j.components = @($j.components) + $sil
    $sun = $j.components | Where-Object id -eq 'sun'; if ($sun) { $sun.rect[2] = 560; $sun.rect[3] = 560 }
    $moon = $j.components | Where-Object id -eq 'moon'; if ($moon) { $moon.rect[2] = 240; $moon.rect[3] = 240 }
}

# 2. Every text: one family, no effects.
foreach ($c in $j.components | Where-Object type -eq 'text') {
    Set-Prop $c 'font' 'Bahnschrift' $c.id
    if ($c.PSObject.Properties['effect']) { $c.effect = 'None' } else { $c | Add-Member effect 'None' }
}

# 3. Sky and weather in palette colours.
foreach ($id in 'stars-small','stars-mid') { Recolour-Prop (Get-Comp $id) 'fill' 'ECEFF4' ($id -eq 'stars-small' ? 'B3' : '73') $id }
Recolour-Prop (Get-Comp 'rain') 'fill' 'ECEFF4' '40' 'rain'
Recolour-Prop (Get-Comp 'snow-small') 'fill' 'ECEFF4' '73' 'snow-small'
Recolour-Prop (Get-Comp 'fog') 'fill' 'ECEFF4' '4D' 'fog'
Recolour-Prop (Get-Comp 'storm-sky') 'fill' '2E3440' '66' 'storm-sky'
Recolour-Prop (Get-Comp 'thunder-flash') 'fill' '88C0D0' '40' 'thunder-flash'
Recolour-Prop (Get-Comp 'disk-warning-sky') 'fill' 'BF616A' '4D' 'disk-warning-sky'
# Keep the heat ramp's own alphas (0 when cool), only move its hue into the palette.
foreach ($id in 'gpu-snowfield','cpu-snowfield') { Recolour-Prop (Get-Comp $id) 'fill' 'BF616A' '' $id; Recolour-Prop (Get-Comp $id) 'glowColor' 'BF616A' '' $id }

# 4. Sun and moon: smaller, no halos.
# Rice: small discs on the photo. Vapor: the sun is the hero, sized to sit behind the peaks.
$sunSize = $Theme -eq 'vapor' ? 420 : 140; $moonSize = $Theme -eq 'vapor' ? 200 : 120
$sun = Get-Comp 'sun'; if ($sun) { $sun.rect[2] = $sunSize; $sun.rect[3] = $sunSize }
$moon = Get-Comp 'moon'; if ($moon) { $moon.rect[2] = $moonSize; $moon.rect[3] = $moonSize }

# 5. The ridge and the foothills: thin, one accent each.
$r = Get-Comp 'ridge'
Set-Prop $r 'thickness' 3 'ridge'; Set-Prop $r 'glow' 12 'ridge'; Set-Prop $r 'track' '#59D8DEE9' 'ridge'; Set-Prop $r 'fill' $frost 'ridge'
Set-Prop $r 'glowColor' $frost 'ridge'   # the phase-coloured glow was the old palette leaking through
$rm = Get-Comp 'ridge-muted'
Set-Prop $rm 'thickness' 3 'ridge-muted'; Set-Prop $rm 'glow' 12 'ridge-muted'; Recolour-Prop $rm 'fill' 'BF616A' '' 'ridge-muted'; Recolour-Prop $rm 'track' 'BF616A' '' 'ridge-muted'
Recolour-Prop $rm 'glowColor' 'BF616A' '' 'ridge-muted'
$f = Get-Comp 'cpu-foothills'
Set-Prop $f 'thickness' 3 'cpu-foothills'; Set-Prop $f 'glow' 8 'cpu-foothills'; Set-Prop $f 'glowStrength' 0.25 'cpu-foothills'
Set-Prop $f 'stroke' $second 'cpu-foothills'; Set-Prop $f 'glowColor' $second 'cpu-foothills'; Set-Prop $f 'areaFill' ('#1A' + $second.Substring(3)) 'cpu-foothills'
if ($f) { $f.rect = @(0, 780, 3440, 660) }   # lower and shallower: it echoes the ridge, it does not compete

# 6. Now playing, bottom-left.
$art = Get-Comp 'now-art'; if ($art) { $art.rect = @(40, 1328, 72, 72) }
$t = Get-Comp 'now-title'; if ($t) { $t.rect = @(128, 1334, 600, 26); Set-Prop $t 'size' 20 'now-title'; Set-Prop $t 'weight' 400 'now-title'; Set-Prop $t 'color' $ink 'now-title' }
$a = Get-Comp 'now-artist'; if ($a) { $a.rect = @(128, 1362, 600, 20); Set-Prop $a 'size' 14 'now-artist'; Set-Prop $a 'weight' 400 'now-artist'; Set-Prop $a 'color' $dim 'now-artist' }
$p = Get-Comp 'now-progress'; if ($p) { $p.rect = @(128, 1392, 240, 2); Set-Prop $p 'track' '#00000000' 'now-progress'; Set-Prop $p 'fill' $frost 'now-progress'; Recolour-Prop $p 'thresholdFill' '88C0D0' 'FF' 'now-progress' }

# 7. Right column: dials, drives, the state lines. x 3000..3420.
foreach ($m in 'cpu','ram','gpu','battery') {
    $d = Get-Comp "dial-$m"
    Set-Prop $d 'thickness' 4 "dial-$m"; Set-Prop $d 'track' $track "dial-$m"
    Set-Prop $d 'fill' ($m -eq 'battery' ? "~0=$danger,0.2=$warn,0.5=$frost" : "~0=$frost,0.7=$frost,0.85=$warn,0.95=$danger") "dial-$m"
    $v = Get-Comp "dial-$m-value"; Set-Prop $v 'size' 22 "dial-$m-value"; Set-Prop $v 'weight' 400 "dial-$m-value"; Set-Prop $v 'color' $ink "dial-$m-value"
    $l = Get-Comp "dial-$m-label"; Set-Prop $l 'size' 12 "dial-$m-label"; Set-Prop $l 'weight' 400 "dial-$m-label"; Set-Prop $l 'color' $dim "dial-$m-label"
}
$dr = Get-Comp 'drives'
if ($dr) {
    foreach ($tp in $dr.template) {
        if ($tp.type -eq 'text') { Set-Prop $tp 'font' 'Bahnschrift' "drives.$($tp.id)"; Set-Prop $tp 'weight' 400 "drives.$($tp.id)"; Set-Prop $tp 'size' 20 "drives.$($tp.id)"; if ($tp.PSObject.Properties['color']) { $tp.color = $ink } }
        if ($tp.type -eq 'bar') { Set-Prop $tp 'track' $track "drives.$($tp.id)"; Set-Prop $tp 'fill' $frost "drives.$($tp.id)"; Recolour-Prop $tp 'thresholdFill' 'BF616A' 'FF' "drives.$($tp.id)" }
    }
}
$vp = Get-Comp 'valley-peers'; if ($vp) { foreach ($tp in $vp.template) { foreach ($n in 'fill','track') { if ($tp.PSObject.Properties[$n]) { Recolour-Prop $tp $n 'EBCB8B' 'B3' "valley-peers.$($tp.id)" } } } }
$bt = Get-Comp 'battery-text'; if ($bt) { $bt.rect = @(3000, 524, 420, 28); Set-Prop $bt 'size' 18 'battery-text'; Set-Prop $bt 'weight' 400 'battery-text'; Set-Prop $bt 'color' $dim 'battery-text'; Set-Prop $bt 'align' 'right' 'battery-text' }
$rb = Get-Comp 'reboot-pending'; if ($rb) { $rb.rect = @(3000, 556, 420, 28); Set-Prop $rb 'size' 18 'reboot-pending'; Set-Prop $rb 'weight' 400 'reboot-pending'; Set-Prop $rb 'color' $warn 'reboot-pending'; Set-Prop $rb 'align' 'right' 'reboot-pending' }
$rc = Get-Comp 'recent-crash'; if ($rc) { $rc.rect = @(3000, 588, 420, 28); Set-Prop $rc 'size' 18 'recent-crash'; Set-Prop $rc 'weight' 400 'recent-crash'; Set-Prop $rc 'color' $warn 'recent-crash'; Set-Prop $rc 'align' 'right' 'recent-crash' }

# 8. The clock: Bahnschrift Light 160 over the main peak, no shadow.
$clock = $j.copies | Where-Object id -eq 'clock-1'
if ($clock) {
    $clock.y = 48
    $o = $clock.overrides
    $o.'components.clock.font' = 'Bahnschrift Light'; $o.'components.clock.size' = '160'; $o.'components.clock.effect' = 'None'
    $o.'components.clock.rect' = '0,0,837,200'; $o.'components.clock.effectRadius' = 'auto'
    if ($o.PSObject.Properties['components.clock.color']) { $o.'components.clock.color' = $ink } else { $o | Add-Member 'components.clock.color' $ink }
} else { $missed.Add('copy clock-1') }

$j | ConvertTo-Json -Depth 20 | Set-Content $Out -Encoding utf8
"wrote ${Out}: $($j.components.Count) components, $($j.copies.Count) copies"
if ($missed.Count) { "could not apply:"; $missed | ForEach-Object { "  $_" } }
