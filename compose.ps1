<#
Compositor. Runs every minute from the scheduled task.
  1. Each widget in $Widgets renders a transparent PNG tile into $DataDir\tiles when its tile
     is missing, older than its Every (seconds), or -Force is given. Widgets are dot-sourced
     functions in .\widgets\<name>.ps1 taking ($gr, $w, $h) and drawing at 0,0.
  2. The cached full-size photo (base) plus every tile is blitted into one JPEG and applied.
Cost is dominated by the JPEG encode (~300 ms); a widget only pays when it is due.
#>
param([switch]$Apply, [switch]$Force, [string[]]$Only)
$sw = [Diagnostics.Stopwatch]::StartNew()
$cpu0 = (Get-Process -Id $pid).TotalProcessorTime
. "$PSScriptRoot\data.ps1" -Top 4

$Widgets = @(
  @{ Name = 'clock'; Every = 60 },
  @{ Name = 'disks'; Every = 300 },
  @{ Name = 'games'; Every = 600; After = { & "$PSScriptRoot\shortcuts.ps1" | Out-Null } }   # re-place icons when covers change
)

$tilesDir = Join-Path $DataDir 'tiles'; if (-not (Test-Path $tilesDir)) { New-Item -ItemType Directory $tilesDir | Out-Null }
$photo = 'C:\Windows\SystemApps\MicrosoftWindows.Client.CBS_cw5n1h2txyewy\DesktopSpotlight\Assets\Images\image_3.jpg'
$basePng = Join-Path $DataDir 'base.png'; $baseKey = Join-Path $DataDir 'base.key'
$outFile = Join-Path $DataDir 'deskwall.jpg'

# shared text helper for widgets: white with a 1px dark shadow
$script:White = [Drawing.Color]::FromArgb(235, 255, 255, 255)
$script:Shadow = [Drawing.Color]::FromArgb(150, 0, 0, 0)
function Text($gr, $s, $x, $y, $f, $align) {
  $fmt = New-Object Drawing.StringFormat; if ($align -eq 'right') { $fmt.Alignment = 'Far' }
  $gr.DrawString($s, $f, (New-Object Drawing.SolidBrush $script:Shadow), [float]($x + 1), [float]($y + 1), $fmt)
  $gr.DrawString($s, $f, (New-Object Drawing.SolidBrush $script:White), [float]$x, [float]$y, $fmt)
}

# ---- base: the photo scaled/cropped to the canvas, cached ------------------------------------
if (-not (Test-Path $basePng) -or -not (Test-Path $baseKey) -or (Get-Content $baseKey -Raw) -ne $photo) {
  $bmp = New-Object Drawing.Bitmap $CanvasW, $CanvasH
  $gr = [Drawing.Graphics]::FromImage($bmp); $gr.InterpolationMode = 'HighQualityBicubic'
  $src = [Drawing.Image]::FromFile($photo)
  $scale = [Math]::Max($CanvasW / $src.Width, $CanvasH / $src.Height)
  $sw2 = [float]($src.Width * $scale); $sh2 = [float]($src.Height * $scale)
  $gr.DrawImage($src, [float](($CanvasW - $sw2) / 2), [float](($CanvasH - $sh2) / 2), $sw2, $sh2); $src.Dispose(); $gr.Dispose()
  $bmp.Save($basePng, [Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
  Set-Content $baseKey $photo -NoNewline
}

# ---- widgets: render tiles that are due ------------------------------------------------------
$rendered = @()
foreach ($wd in $Widgets) {
  if ($Only -and $Only -notcontains $wd.Name) { continue }
  $tile = Join-Path $tilesDir "$($wd.Name).png"
  $due = $Force -or -not (Test-Path $tile) -or (((Get-Date) - (Get-Item $tile).LastWriteTime).TotalSeconds -ge $wd.Every - 5)
  if (-not $due) { continue }
  $r = $Rects[$wd.Name]
  . "$PSScriptRoot\widgets\$($wd.Name).ps1"
  $bmp = New-Object Drawing.Bitmap $r.W, $r.H, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $gr = [Drawing.Graphics]::FromImage($bmp)
  $gr.SmoothingMode = 'AntiAlias'; $gr.InterpolationMode = 'HighQualityBicubic'; $gr.TextRenderingHint = 'AntiAliasGridFit'
  $before = $null; if (Test-Path "$tile.key") { $before = Get-Content "$tile.key" -Raw }
  $key = & "Render-$($wd.Name)" $gr $r.W $r.H     # widget returns a content key (or $null)
  $gr.Dispose(); $bmp.Save($tile, [Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
  if ($null -ne $key) { Set-Content "$tile.key" ([string]$key) -NoNewline }
  $rendered += $wd.Name
  if ($wd.After -and ([string]$key) -ne ([string]$before)) { & $wd.After }
}

# ---- composite ---------------------------------------------------------------------------------
$bmp = [Drawing.Bitmap]::FromFile($basePng)
$gr = [Drawing.Graphics]::FromImage($bmp)
foreach ($wd in $Widgets) {
  $tile = Join-Path $tilesDir "$($wd.Name).png"; if (-not (Test-Path $tile)) { continue }
  $r = $Rects[$wd.Name]; $t = [Drawing.Image]::FromFile($tile); $gr.DrawImage($t, $r.X, $r.Y, $r.W, $r.H); $t.Dispose()
}
$gr.Dispose()
$jpeg = [Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object { $_.MimeType -eq 'image/jpeg' }
$ep = New-Object Drawing.Imaging.EncoderParameters 1
$ep.Param[0] = New-Object Drawing.Imaging.EncoderParameter ([Drawing.Imaging.Encoder]::Quality), 92L
$tmp = "$outFile.tmp"; $bmp.Save($tmp, $jpeg, $ep); $bmp.Dispose(); Move-Item $tmp $outFile -Force

if ($Apply) {
  if (-not ('WP' -as [type])) { Add-Type -TypeDefinition 'using System.Runtime.InteropServices; public class WP { [DllImport("user32.dll", SetLastError=true)] public static extern int SystemParametersInfo(int uAction, int uParam, string lpvParam, int fuWinIni); }' }
  Set-ItemProperty 'HKCU:\Control Panel\Desktop' -Name WallpaperStyle -Value '10'
  Set-ItemProperty 'HKCU:\Control Panel\Desktop' -Name TileWallpaper -Value '0'
  [void][WP]::SystemParametersInfo(0x14, 0, $outFile, 3)
}

$cpu = ((Get-Process -Id $pid).TotalProcessorTime - $cpu0).TotalMilliseconds
"{0} | data={1} | rendered=[{2}] | out={3:N1} MB | wall={4:N0} ms | cpu={5:N0} ms" -f (Get-Date -Format 'HH:mm:ss'), $DataSource, ($rendered -join ','), ((Get-Item $outFile).Length / 1MB), $sw.ElapsedMilliseconds, $cpu
