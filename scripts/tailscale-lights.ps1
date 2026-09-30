#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$exe = Join-Path $env:ProgramFiles 'Tailscale\tailscale.exe'
if (-not (Test-Path -LiteralPath $exe)) { '{"peers":[],"status":"unavailable"}'; exit }
$status = & $exe status --json | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { throw 'Tailscale status failed.' }
$peers = @(if ($status.BackendState -eq 'Running' -and $status.Peer) {
    foreach ($property in $status.Peer.PSObject.Properties) {
        if ($property.Value.Online) { [pscustomobject]@{ id=$property.Name } }
    }
})
@{ peers=$peers; status=$status.BackendState } | ConvertTo-Json -Depth 4 -Compress
