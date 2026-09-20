# Disks tile: one row per drive - letter, GB free, used-space bar (red under 15% free). Every 5 min.
function Render-disks($gr, $w, $h) {
  $font = New-Object Drawing.Font('Segoe UI', 15, [Drawing.FontStyle]::Regular, [Drawing.GraphicsUnit]::Pixel)
  $rowH = 46; $barH = 6; $y = 0
  foreach ($d in $Disks) {
    Text $gr "$($d.Letter):" 0 $y $font 'left'
    Text $gr ("{0:N0} GB free" -f $d.FreeGB) $w $y $font 'right'
    $by = $y + 26
    $gr.FillRectangle((New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(70, 255, 255, 255))), 0, $by, $w, $barH)
    $fill = $script:White; if ((1 - $d.UsedPct) -lt 0.15) { $fill = [Drawing.Color]::FromArgb(255, 209, 52, 56) }
    $gr.FillRectangle((New-Object Drawing.SolidBrush $fill), 0, $by, [int]($w * $d.UsedPct), $barH)
    $y += $rowH
  }
  return (($Disks | ForEach-Object { "$($_.Letter)=$($_.FreeGB)" }) -join ',')
}
