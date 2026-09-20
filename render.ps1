<#
Renders the wallpaper in two layers so a per-minute tick stays cheap:
  base    = Spotlight photo + game covers. Cached as PNG in $DataDir; rebuilt only when the
            set/order of games, their cover geometry, or the photo changes.
  overlay = clock (HH:mm) + disk bars, drawn onto a copy of the base every run.
Output is JPEG (~2 MB) rather than PNG (~15 MB) because it is written every minute.
#>
param([switch]$Apply, [switch]$ForceBase)
$sw = [Diagnostics.Stopwatch]::StartNew()
$cpu0 = (Get-Process -Id $pid).TotalProcessorTime
. "$PSScriptRoot\data.ps1" -Top 4

$canvasW = 3440; $canvasH = 1440; $taskbar = 48
$right = $ColLeft + $ColW
$base = 'C:\Windows\SystemApps\MicrosoftWindows.Client.CBS_cw5n1h2txyewy\DesktopSpotlight\Assets\Images\image_3.jpg'
$basePng = Join-Path $DataDir 'deskwall-base.png'
$baseKeyPath = Join-Path $DataDir 'deskwall-base.key'
$outFile = Join-Path $DataDir 'deskwall.jpg'

$white = [Drawing.Color]::FromArgb(235, 255, 255, 255)
$shadow = [Drawing.Color]::FromArgb(150, 0, 0, 0)
function Text($gr, $s, $x, $ty, $f, $align) {
  $fmt = New-Object Drawing.StringFormat; if ($align -eq 'right') { $fmt.Alignment = 'Far' }
  $gr.DrawString($s, $f, (New-Object Drawing.SolidBrush $shadow), [float]($x + 1), [float]($ty + 1), $fmt)
  $gr.DrawString($s, $f, (New-Object Drawing.SolidBrush $white), [float]$x, [float]$ty, $fmt)
}

# ---- base layer -------------------------------------------------------------------------
$key = ($Games | ForEach-Object { $_.Id }) -join ',' ; $key += '|' + (($CoverRects | ForEach-Object { "$($_.X),$($_.Y),$($_.W),$($_.H)" }) -join ';') + '|' + $base
$haveBase = (Test-Path $basePng) -and (Test-Path $baseKeyPath) -and ((Get-Content $baseKeyPath -Raw) -eq $key)
$baseRebuilt = $false
if ($ForceBase -or -not $haveBase) {
  $bmp = New-Object Drawing.Bitmap $canvasW, $canvasH
  $gr = [Drawing.Graphics]::FromImage($bmp)
  $gr.SmoothingMode = 'AntiAlias'; $gr.InterpolationMode = 'HighQualityBicubic'
  $src = [Drawing.Image]::FromFile($base)
  $scale = [Math]::Max($canvasW / $src.Width, $canvasH / $src.Height)
  $sw2 = [float]($src.Width * $scale); $sh2 = [float]($src.Height * $scale)
  $gr.DrawImage($src, [float](($canvasW - $sw2) / 2), [float](($canvasH - $sh2) / 2), $sw2, $sh2); $src.Dispose()
  $tileFont = New-Object Drawing.Font('Segoe UI', 18, [Drawing.FontStyle]::Regular, [Drawing.GraphicsUnit]::Pixel)
  for ($i = 0; $i -lt $Games.Count; $i++) {
    $gm = $Games[$i]; $r = $CoverRects[$i]
    if ($gm.Cover -and (Test-Path $gm.Cover)) {
      $c = [Drawing.Image]::FromFile($gm.Cover); $gr.DrawImage($c, $r.X, $r.Y, $r.W, $r.H); $c.Dispose()
    } else {
      $gr.FillRectangle((New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(140, 0, 0, 0))), $r.X, $r.Y, $r.W, $r.H)
      $rect = New-Object Drawing.RectangleF ($r.X + 14), ($r.Y + 14), ($r.W - 28), ($r.H - 28)
      $gr.DrawString($gm.Name, $tileFont, (New-Object Drawing.SolidBrush $white), $rect)
    }
  }
  $gr.Dispose(); $bmp.Save($basePng, [Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
  Set-Content $baseKeyPath $key -NoNewline
  $baseRebuilt = $true
}

# ---- overlay ----------------------------------------------------------------------------
$bmp = [Drawing.Bitmap]::FromFile($basePng)
$gr = [Drawing.Graphics]::FromImage($bmp)
$gr.SmoothingMode = 'AntiAlias'; $gr.TextRenderingHint = 'AntiAliasGridFit'

# clock: HH:mm, right-aligned, top of the column
$clockFont = New-Object Drawing.Font('Segoe UI Light', 64, [Drawing.FontStyle]::Regular, [Drawing.GraphicsUnit]::Pixel)
Text $gr (Get-Date -Format 'HH:mm') $right ($ClockTop - 8) $clockFont 'right'

# disks, bottom-anchored above the taskbar
$font = New-Object Drawing.Font('Segoe UI', 15, [Drawing.FontStyle]::Regular, [Drawing.GraphicsUnit]::Pixel)
$rowH = 46; $barH = 6
$y = $canvasH - $taskbar - 40 - ($Disks.Count * $rowH)
foreach ($d in $Disks) {
  Text $gr "$($d.Letter):" $ColLeft $y $font 'left'
  Text $gr ("{0:N0} GB free" -f $d.FreeGB) $right $y $font 'right'
  $by = $y + 26
  $gr.FillRectangle((New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(70, 255, 255, 255))), $ColLeft, $by, $ColW, $barH)
  $fillCol = $white; if ((1 - $d.UsedPct) -lt 0.15) { $fillCol = [Drawing.Color]::FromArgb(255, 209, 52, 56) }
  $gr.FillRectangle((New-Object Drawing.SolidBrush $fillCol), $ColLeft, $by, [int]($ColW * $d.UsedPct), $barH)
  $y += $rowH
}
$gr.Dispose()

$jpeg = [Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object { $_.MimeType -eq 'image/jpeg' }
$ep = New-Object Drawing.Imaging.EncoderParameters 1
$ep.Param[0] = New-Object Drawing.Imaging.EncoderParameter ([Drawing.Imaging.Encoder]::Quality), 92L
$tmp = "$outFile.tmp"; $bmp.Save($tmp, $jpeg, $ep); $bmp.Dispose()
Move-Item $tmp $outFile -Force

if ($Apply) {
  if (-not ('WP' -as [type])) { Add-Type -TypeDefinition 'using System.Runtime.InteropServices; public class WP { [DllImport("user32.dll", SetLastError=true)] public static extern int SystemParametersInfo(int uAction, int uParam, string lpvParam, int fuWinIni); }' }
  Set-ItemProperty 'HKCU:\Control Panel\Desktop' -Name WallpaperStyle -Value '10'
  Set-ItemProperty 'HKCU:\Control Panel\Desktop' -Name TileWallpaper -Value '0'
  [void][WP]::SystemParametersInfo(0x14, 0, $outFile, 3)
}

$cpu = ((Get-Process -Id $pid).TotalProcessorTime - $cpu0).TotalMilliseconds
"{0} | data={1} | base={2} | out={3:N1} MB | wall={4:N0} ms | cpu={5:N0} ms" -f (Get-Date -Format 'HH:mm:ss'), $DataSource, $(if ($baseRebuilt) { 'rebuilt' } else { 'cached' }), ((Get-Item $outFile).Length / 1MB), $sw.ElapsedMilliseconds, $cpu
