# Verifies the live desktop against the composed wallpaper:
#   - screenshots the right column to $DataDir\verify-desktop.png
#   - pixel-diffs each cover region to find the shortcut-arrow overlay and reports its padding
#   - saves an 8x zoom of the clock to $DataDir\clock-now.png
# Run after compose.ps1 -Apply. Expect "left pad 5, bottom pad 5" on every cover.
param([int]$WantPad = 5, [int]$Threshold = 60)
. "$PSScriptRoot\data.ps1" -Top 4

$shell = New-Object -ComObject Shell.Application
$shell.MinimizeAll(); Start-Sleep -Milliseconds 1500
$scr = New-Object Drawing.Bitmap $CanvasW, $CanvasH
$g = [Drawing.Graphics]::FromImage($scr); $g.CopyFromScreen(0, 0, 0, 0, $scr.Size); $g.Dispose()
$shell.UndoMinimizeALL()

$colShot = $scr.Clone((New-Object Drawing.Rectangle ($ColLeft - 120), 0, ($CanvasW - $ColLeft + 120), $CanvasH), $scr.PixelFormat)
$colShot.Save((Join-Path $DataDir 'verify-desktop.png'), [Drawing.Imaging.ImageFormat]::Png); $colShot.Dispose()

$wallPath = Join-Path $DataDir 'deskwall.jpg'
if (-not (Test-Path $wallPath)) { $scr.Dispose(); throw "no $wallPath - run compose.ps1 first" }
$wall = [Drawing.Bitmap]::FromFile($wallPath)
"arrow padding per cover (want $WantPad/$WantPad); diff threshold $Threshold, 2px inset to dodge JPEG ringing"
$ok = $true
for ($i = 0; $i -lt $CoverRects.Count; $i++) {
  $r = $CoverRects[$i]; $minX = 9999; $minY = 9999; $maxX = -1; $maxY = -1; $n = 0
  for ($y = $r.Y + 2; $y -lt ($r.Y + $r.H - 2); $y++) { for ($x = $r.X + 2; $x -lt ($r.X + $r.W - 2); $x++) {
    $a = $scr.GetPixel($x, $y); $b = $wall.GetPixel($x, $y)
    if ([Math]::Abs($a.R - $b.R) -gt $Threshold -or [Math]::Abs($a.G - $b.G) -gt $Threshold -or [Math]::Abs($a.B - $b.B) -gt $Threshold) {
      $n++; if ($x -lt $minX) { $minX = $x }; if ($x -gt $maxX) { $maxX = $x }; if ($y -lt $minY) { $minY = $y }; if ($y -gt $maxY) { $maxY = $y }
    }
  } }
  if ($n -eq 0) { "  slot $($i+1): NO ICON FOUND  $($Games[$i].Name)"; $ok = $false; continue }
  $left = $minX - $r.X; $bottom = ($r.Y + $r.H - 1) - $maxY
  $flag = ''; if ($left -ne $WantPad -or $bottom -ne $WantPad) { $flag = '  <-- off'; $ok = $false }
  "  slot $($i+1): left pad $left, bottom pad $bottom, diff bbox $($maxX-$minX+1)x$($maxY-$minY+1) ($n px)  $($Games[$i].Name)$flag"
}
$wall.Dispose()

$cr = $Rects.clock
$clock = $scr.Clone((New-Object Drawing.Rectangle ($cr.X - 20), ($cr.Y - 8), ($cr.W + 40), ($cr.H + 20)), $scr.PixelFormat)
$big = New-Object Drawing.Bitmap ($clock.Width * 3), ($clock.Height * 3)
$gg = [Drawing.Graphics]::FromImage($big); $gg.InterpolationMode = 'NearestNeighbor'; $gg.DrawImage($clock, 0, 0, $big.Width, $big.Height); $gg.Dispose()
$big.Save((Join-Path $DataDir 'clock-now.png'), [Drawing.Imaging.ImageFormat]::Png); $clock.Dispose(); $big.Dispose(); $scr.Dispose()
"saved $DataDir\verify-desktop.png and clock-now.png"
if ($ok) { "RESULT: OK" } else { "RESULT: CHECK FLAGGED ROWS" }
