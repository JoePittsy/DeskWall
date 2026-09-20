# Games tile: covers of the most recently played installed games, stacked. Every 10 min.
# Returns a key of game ids + geometry; when it changes the compositor re-places the shortcuts.
function Render-games($gr, $w, $h) {
  $tileFont = New-Object Drawing.Font('Segoe UI', 18, [Drawing.FontStyle]::Regular, [Drawing.GraphicsUnit]::Pixel)
  for ($i = 0; $i -lt $Games.Count; $i++) {
    $gm = $Games[$i]; $r = $CoverRects[$i]
    $x = $r.X - $ColLeft; $y = $r.Y - $ColTop          # tile-local
    if ($y + $r.H -gt $h) { break }                      # never draw into the disks block
    if ($gm.Cover -and (Test-Path $gm.Cover)) {
      $c = [Drawing.Image]::FromFile($gm.Cover); $gr.DrawImage($c, $x, $y, $r.W, $r.H); $c.Dispose()
    } else {
      $gr.FillRectangle((New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(140, 0, 0, 0))), $x, $y, $r.W, $r.H)
      $rect = New-Object Drawing.RectangleF ($x + 14), ($y + 14), ($r.W - 28), ($r.H - 28)
      $gr.DrawString($gm.Name, $tileFont, (New-Object Drawing.SolidBrush $script:White), $rect)
    }
  }
  return (($Games | ForEach-Object { $_.Id }) -join ',') + '|' + (($CoverRects | ForEach-Object { "$($_.X),$($_.Y),$($_.W),$($_.H)" }) -join ';')
}
