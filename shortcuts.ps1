. "$PSScriptRoot\data.ps1" -Top 4
$out = $DataDir   # set by data.ps1: %LOCALAPPDATA%\DeskWall
"data source: $DataSource"

# Icon placement, derived by pixel-diffing the live desktop against the wallpaper (2026-09-20,
# 48px icons, 100% scaling): the shortcut-arrow overlay is a 13x13 square whose top-left is at
# (item.x + 0, item.y + 40). To sit it $Pad px from the cover's bottom-left corner:
#   item.x = cover.X + Pad            item.y = cover.Y + cover.H - Pad - 13 - 40
$Pad = 5; $ArrowDx = 0; $ArrowDy = 40; $ArrowSize = 13
$IconDx = $Pad - $ArrowDx; $IconDyFromBottom = -($Pad + $ArrowSize + $ArrowDy)

# 1. transparent 256px icon (PNG-in-ICO)
$ico = "$out\blank.ico"
if (-not (Test-Path $ico)) {
  $bmp = New-Object Drawing.Bitmap 256, 256; $ms = New-Object IO.MemoryStream; $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png); $png = $ms.ToArray(); $bmp.Dispose()
  $bw = New-Object IO.BinaryWriter ([IO.File]::Create($ico))
  $bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]1)
  $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$png.Length); $bw.Write([uint32]22)
  $bw.Write($png); $bw.Close()
}

# 2. shortcuts named with 1..N non-breaking spaces so no label shows; slot i always maps to cover i
$desktop = [Environment]::GetFolderPath('Desktop')
$wsh = New-Object -ComObject WScript.Shell
$paths = @()
for ($i = 0; $i -lt $Games.Count; $i++) {
  $gm = $Games[$i]
  $lnk = Join-Path $desktop ([string]::new([char]0xA0, $i + 1) + '.lnk')
  $s = $wsh.CreateShortcut($lnk)
  $s.TargetPath = "$env:LOCALAPPDATA\Playnite\Playnite.DesktopApp.exe"
  $s.Arguments = "--start $($gm.Id)"
  $s.WorkingDirectory = "$env:LOCALAPPDATA\Playnite"
  $s.IconLocation = "$ico,0"
  $s.Description = "Play $($gm.Name)"
  $s.Save()
  $paths += $lnk
  "slot $($i + 1) -> $($gm.Name)"
}

# 3. positions anchored to each cover's bottom-left
$xs = @(); $ys = @()
foreach ($r in $CoverRects) { $xs += ($r.X + $IconDx); $ys += ($r.Y + $r.H + $IconDyFromBottom) }

# 4. position via IFolderView::SelectAndPositionItems on the desktop view
if (-not ('DeskIcons' -as [type])) { Add-Type -Path "$PSScriptRoot\DeskIcons.cs" }
Start-Sleep 2
[DeskIcons]::Position([string[]]$paths, [int[]]$xs, [int[]]$ys)
Start-Sleep 1
for ($k = 0; $k -lt $paths.Count; $k++) { $got = [DeskIcons]::Get($paths[$k]); "  slot $($k + 1) wanted ($($xs[$k]),$($ys[$k]))  got ($($got[0]),$($got[1]))  $($Games[$k].Name)" }
