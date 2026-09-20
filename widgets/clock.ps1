# Clock tile: HH:mm right-aligned. Every 60 s.
function Render-clock($gr, $w, $h) {
  $font = New-Object Drawing.Font('Segoe UI Light', 64, [Drawing.FontStyle]::Regular, [Drawing.GraphicsUnit]::Pixel)
  $now = Get-Date -Format 'HH:mm'
  Text $gr $now $w -8 $font 'right'
  return $now
}
