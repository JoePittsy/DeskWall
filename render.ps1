param([string]$OutDir = "$env:LOCALAPPDATA\DeskWall", [switch]$Apply)
. "$PSScriptRoot\data.ps1" -Top 4

function Render($outPath, $games, $rects, $disks, $basePath) {
  $canvasW = 3440; $canvasH = 1440; $taskbar = 48
  $bmp = New-Object Drawing.Bitmap $canvasW, $canvasH
  $gr = [Drawing.Graphics]::FromImage($bmp)
  $gr.SmoothingMode = 'AntiAlias'; $gr.InterpolationMode = 'HighQualityBicubic'; $gr.TextRenderingHint = 'AntiAliasGridFit'
  $src = [Drawing.Image]::FromFile($basePath)
  $scale = [Math]::Max($canvasW / $src.Width, $canvasH / $src.Height)
  $sw = [float]($src.Width * $scale); $sh = [float]($src.Height * $scale)
  $gr.DrawImage($src, [float](($canvasW - $sw) / 2), [float](($canvasH - $sh) / 2), $sw, $sh); $src.Dispose()

  $colW = 180; $right = $canvasW - 48; $left = $right - $colW
  $white = [Drawing.Color]::FromArgb(235, 255, 255, 255)
  $shadow = [Drawing.Color]::FromArgb(150, 0, 0, 0)
  $font = New-Object Drawing.Font('Segoe UI', 15, [Drawing.FontStyle]::Regular, [Drawing.GraphicsUnit]::Pixel)
  $tileFont = New-Object Drawing.Font('Segoe UI', 18, [Drawing.FontStyle]::Regular, [Drawing.GraphicsUnit]::Pixel)
  function Text($s, $x, $ty, $f, $align) {
    $fmt = New-Object Drawing.StringFormat; if ($align -eq 'right') { $fmt.Alignment = 'Far' }
    $gr.DrawString($s, $f, (New-Object Drawing.SolidBrush $shadow), [float]($x + 1), [float]($ty + 1), $fmt)
    $gr.DrawString($s, $f, (New-Object Drawing.SolidBrush $white), [float]$x, [float]$ty, $fmt)
  }

  for ($i = 0; $i -lt $games.Count; $i++) {
    $gm = $games[$i]; $r = $rects[$i]
    if ($gm.Cover -and (Test-Path $gm.Cover)) {
      $c = [Drawing.Image]::FromFile($gm.Cover); $gr.DrawImage($c, $r.X, $r.Y, $r.W, $r.H); $c.Dispose()
    } else {
      $gr.FillRectangle((New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(140, 0, 0, 0))), $r.X, $r.Y, $r.W, $r.H)
      $rect = New-Object Drawing.RectangleF ($r.X + 14), ($r.Y + 14), ($r.W - 28), ($r.H - 28)
      $gr.DrawString($gm.Name, $tileFont, (New-Object Drawing.SolidBrush $white), $rect)
    }
  }

  $rowH = 46; $barH = 6
  $y = $canvasH - $taskbar - 40 - ($disks.Count * $rowH)
  foreach ($d in $disks) {
    Text "$($d.Letter):" $left $y $font 'left'
    Text ("{0:N0} GB free" -f $d.FreeGB) $right $y $font 'right'
    $by = $y + 26
    $gr.FillRectangle((New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(70, 255, 255, 255))), $left, $by, $colW, $barH)
    $fillCol = $white; if ((1 - $d.UsedPct) -lt 0.15) { $fillCol = [Drawing.Color]::FromArgb(255, 209, 52, 56) }
    $gr.FillRectangle((New-Object Drawing.SolidBrush $fillCol), $left, $by, [int]($colW * $d.UsedPct), $barH)
    $y += $rowH
  }
  $gr.Dispose(); $bmp.Save($outPath, [Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
}

$base = 'C:\Windows\SystemApps\MicrosoftWindows.Client.CBS_cw5n1h2txyewy\DesktopSpotlight\Assets\Images\image_3.jpg'
$outFile = Join-Path $OutDir 'deskwall-real.png'
Render $outFile $Games $CoverRects $Disks $base
foreach ($gm in $Games) { "  {0:yyyy-MM-dd HH:mm} via {1,-8} {2}" -f $gm.Last, $gm.Source, $gm.Name }

if ($Apply) {
  Add-Type -TypeDefinition 'using System.Runtime.InteropServices; public class WP { [DllImport("user32.dll", SetLastError=true)] public static extern int SystemParametersInfo(int uAction, int uParam, string lpvParam, int fuWinIni); }'
  Set-ItemProperty 'HKCU:\Control Panel\Desktop' -Name WallpaperStyle -Value '10'
  Set-ItemProperty 'HKCU:\Control Panel\Desktop' -Name TileWallpaper -Value '0'
  "wallpaper set: " + [WP]::SystemParametersInfo(0x14, 0, $outFile, 3)
}
