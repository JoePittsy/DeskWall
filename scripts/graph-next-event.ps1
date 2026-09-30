#Requires -Version 7
<#
.SYNOPSIS
  The next few calendar events from Microsoft Graph, as one line of JSON for a DeskWall command source.

.DESCRIPTION
  Reads /me/calendarView for the coming -Hours and prints the next -Count events. Two ways to get a
  token, never a prompt unless -Login is given:

    -Flow mg (default)  The Microsoft.Graph.Authentication module's own token cache, the one the
                        owner's ms-todo skill uses (%LOCALAPPDATA%\.IdentityService\mg.msal.cache.*).
                        Silent Connect-MgGraph; Windows sign-in (WAM) for the first time. Works in a
                        tenant that enforces device-compliance Conditional Access, because WAM
                        carries the device's state and device code cannot.
    -Flow devicecode    Device-code sign-in against the public Microsoft Graph PowerShell client,
                        refresh token kept in <runtime dir>\calendar-token.txt, DPAPI-protected for
                        the current user. For a personal Microsoft account, or a tenant without
                        that policy.

  First sign-in, once, in a visible pwsh 7 window (or `deskwall calendar login`):
      pwsh -File graph-next-event.ps1 -Login [-Flow devicecode]
  -Pause waits for Enter before exiting, so a window opened just for the sign-in stays readable.

  Output (stdout, one line):
      {"status":"ok","account":"...","count":2,"events":[{...}],"next":{...}}
  Each event: subject, start, end (Unix seconds; list them in the source's unixTimeFields),
  startText/endText (local HH:mm), minutesUntil (whole minutes, negative once started),
  inProgress, isOnline, joinUrl, location. "next" is events[0] and absent when there is none.

  Exit codes: 0 ok. 2 no usable token: prints {"status":"signin-required",...} with an empty list,
  so a layout can say so. 1 anything else (network, Graph): prints nothing to stdout, so the
  command source keeps the last good events rather than blanking them.
#>
param(
    [switch]$Login,
    [switch]$Pause,
    [ValidateSet('mg', 'devicecode')][string]$Flow = 'mg',
    [ValidateRange(1, 20)][int]$Count = 3,
    [ValidateRange(1, 168)][int]$Hours = 12,
    [switch]$IncludeAllDay,
    [string]$Tenant = 'common',
    [string]$RuntimeDir = $(if ($env:DESKWALL_HOME) { $env:DESKWALL_HOME } else { Join-Path $env:LOCALAPPDATA 'DeskWall' })
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)

$ClientId = '14d82eec-204b-4c2f-b7e8-296a70dab67e'   # Microsoft Graph PowerShell, public client
$Scope = 'Calendars.Read offline_access'
$TokenFile = Join-Path $RuntimeDir 'calendar-token.txt'

class SignInRequired : Exception { SignInRequired([string]$m) : base($m) {} }

function Say([string]$text) { try { if ($Login) { Write-Host $text } else { [Console]::Error.WriteLine($text) } } catch { } }

# ---- token: Microsoft.Graph.Authentication ---------------------------------------------------

function Connect-Mg {
    Import-Module Microsoft.Graph.Authentication -ErrorAction Stop
    if (-not $Login) {
        # A hidden console (Task Scheduler, Start-Process -WindowStyle Hidden) gives WAM a parent
        # window nobody can see, and Connect-MgGraph then waits on an invisible prompt until it is
        # killed. With no console at all - how DeskWall's command source runs it - WAM refuses at
        # once ("A window handle must be configured"), which is the answer wanted here. Freeing a
        # hidden console gets the same refusal; the script then cannot print and exits 1, which a
        # command source treats as "keep the last values".
        Add-Type -Namespace DeskWall -Name Win32 -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("kernel32.dll")] public static extern System.IntPtr GetConsoleWindow();
[System.Runtime.InteropServices.DllImport("kernel32.dll")] public static extern bool FreeConsole();
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool IsWindowVisible(System.IntPtr h);
'@
        $w = [DeskWall.Win32]::GetConsoleWindow()
        if ($w -ne [IntPtr]::Zero -and -not [DeskWall.Win32]::IsWindowVisible($w)) { [void][DeskWall.Win32]::FreeConsole() }
    }
    try {
        Connect-MgGraph -Scopes 'Calendars.Read' -ContextScope CurrentUser -NoWelcome -ErrorAction Stop *> $null
    }
    catch {
        throw [SignInRequired]::new("Connect-MgGraph: $($_.Exception.Message)")
    }
    $ctx = Get-MgContext
    if (-not $ctx -or -not $ctx.Account) { throw [SignInRequired]::new('Connect-MgGraph returned no account.') }
    if ($ctx.Scopes -notcontains 'Calendars.Read') { throw [SignInRequired]::new("The cached token lacks Calendars.Read (has: $($ctx.Scopes -join ', ')).") }
    return $ctx.Account
}

function Get-GraphMg([string]$uri) {
    Invoke-MgGraphRequest -Method GET -Uri $uri -Headers @{ Prefer = 'outlook.timezone="UTC"' } -OutputType PSObject
}

# ---- token: device code ----------------------------------------------------------------------

function Save-Refresh([string]$tenant, [string]$refresh) {
    New-Item -ItemType Directory -Force -Path $RuntimeDir | Out-Null
    $protected = ConvertTo-SecureString -String $refresh -AsPlainText -Force | ConvertFrom-SecureString
    @{ tenant = $tenant; refresh = $protected } | ConvertTo-Json -Compress | Set-Content -LiteralPath $TokenFile -Encoding utf8
}

function Read-Refresh {
    if (-not (Test-Path -LiteralPath $TokenFile)) { throw [SignInRequired]::new("No token at $TokenFile.") }
    try {
        $saved = Get-Content -LiteralPath $TokenFile -Raw | ConvertFrom-Json
        $plain = [Net.NetworkCredential]::new('', ($saved.refresh | ConvertTo-SecureString)).Password
        return @{ tenant = $saved.tenant; refresh = $plain }
    }
    catch { throw [SignInRequired]::new("The token at $TokenFile cannot be read by this user: $($_.Exception.Message)") }
}

function Invoke-Token([string]$tenant, [hashtable]$body) {
    try {
        Invoke-RestMethod -Method POST -Uri "https://login.microsoftonline.com/$tenant/oauth2/v2.0/token" -Body $body -TimeoutSec 20
    }
    catch {
        $detail = $_.ErrorDetails.Message
        $code = if ($detail) { try { ($detail | ConvertFrom-Json).error } catch { $null } }
        if ($code -in 'invalid_grant', 'interaction_required', 'invalid_client', 'unauthorized_client') {
            throw [SignInRequired]::new("Token endpoint: $code")
        }
        throw
    }
}

function Start-DeviceCode {
    $dc = Invoke-RestMethod -Method POST -Uri "https://login.microsoftonline.com/$Tenant/oauth2/v2.0/devicecode" `
        -Body @{ client_id = $ClientId; scope = $Scope } -TimeoutSec 20
    Write-Host $dc.message
    $deadline = (Get-Date).AddSeconds([int]$dc.expires_in)
    $interval = [Math]::Max(1, [int]$dc.interval)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds $interval
        try {
            $tok = Invoke-RestMethod -Method POST -Uri "https://login.microsoftonline.com/$Tenant/oauth2/v2.0/token" -TimeoutSec 20 -Body @{
                grant_type = 'urn:ietf:params:oauth:grant-type:device_code'; client_id = $ClientId; device_code = $dc.device_code
            }
            Save-Refresh $Tenant $tok.refresh_token
            return $tok.access_token
        }
        catch {
            $code = try { ($_.ErrorDetails.Message | ConvertFrom-Json).error } catch { $null }
            if ($code -eq 'authorization_pending') { continue }
            if ($code -eq 'slow_down') { $interval += 5; continue }
            throw "Device-code sign-in failed: $(if ($code) { $code } else { $_.Exception.Message })"
        }
    }
    throw 'Device-code sign-in timed out.'
}

function Get-DeviceAccess {
    $saved = Read-Refresh
    $tok = Invoke-Token $saved.tenant @{ grant_type = 'refresh_token'; client_id = $ClientId; refresh_token = $saved.refresh; scope = $Scope }
    # Refresh tokens rotate; keep the newest so the chain does not age out.
    if ($tok.refresh_token -and $tok.refresh_token -ne $saved.refresh) { Save-Refresh $saved.tenant $tok.refresh_token }
    return $tok.access_token
}

function Get-GraphRaw([string]$access, [string]$uri) {
    try {
        Invoke-RestMethod -Method GET -Uri "https://graph.microsoft.com/$uri" -TimeoutSec 20 -Headers @{
            Authorization = "Bearer $access"; Prefer = 'outlook.timezone="UTC"'
        }
    }
    catch {
        if ($_.Exception.Response.StatusCode.value__ -eq 401) { throw [SignInRequired]::new('Graph answered 401.') }
        throw
    }
}

# ---- the read --------------------------------------------------------------------------------

function ConvertTo-Event($e, [datetimeoffset]$now) {
    $start = [datetimeoffset]::new([datetime]::SpecifyKind([datetime]::Parse($e.start.dateTime, [cultureinfo]::InvariantCulture), 'Utc'))
    $end = [datetimeoffset]::new([datetime]::SpecifyKind([datetime]::Parse($e.end.dateTime, [cultureinfo]::InvariantCulture), 'Utc'))
    $join = if ($e.onlineMeeting -and $e.onlineMeeting.joinUrl) { $e.onlineMeeting.joinUrl } elseif ($e.onlineMeetingUrl) { $e.onlineMeetingUrl } else { '' }
    [ordered]@{
        subject      = [string]$e.subject
        start        = $start.ToUnixTimeSeconds()
        end          = $end.ToUnixTimeSeconds()
        startText    = $start.ToLocalTime().ToString('HH:mm', [cultureinfo]::InvariantCulture)
        endText      = $end.ToLocalTime().ToString('HH:mm', [cultureinfo]::InvariantCulture)
        minutesUntil = [int][Math]::Truncate(($start - $now).TotalMinutes)
        inProgress   = ($start -le $now)
        isOnline     = [bool]($e.isOnlineMeeting -or $join)
        joinUrl      = [string]$join
        location     = [string]$e.location.displayName
        isAllDay     = [bool]$e.isAllDay
    }
}

function Read-Calendar([scriptblock]$get) {
    $now = [datetimeoffset]::UtcNow
    $from = $now.ToString('yyyy-MM-ddTHH:mm:ssZ', [cultureinfo]::InvariantCulture)
    $to = $now.AddHours($Hours).ToString('yyyy-MM-ddTHH:mm:ssZ', [cultureinfo]::InvariantCulture)
    $select = 'subject,start,end,isAllDay,isCancelled,showAs,isOnlineMeeting,onlineMeeting,onlineMeetingUrl,location'
    $uri = "v1.0/me/calendarView?startDateTime=$from&endDateTime=$to&`$orderby=start/dateTime&`$top=50&`$select=$select"
    $raw = & $get $uri
    $events = @(foreach ($e in @($raw.value)) {
            if ($e.isCancelled) { continue }
            if ($e.isAllDay -and -not $IncludeAllDay) { continue }
            ConvertTo-Event $e $now
        }) | Select-Object -First $Count
    return @($events)
}

function Write-Result([string]$status, [string]$account, [object[]]$events) {
    $out = [ordered]@{ status = $status; account = $account; count = $events.Count; events = $events }
    if ($events.Count -gt 0) { $out.next = $events[0] }
    $out | ConvertTo-Json -Depth 5 -Compress
}

function Invoke-Main {
    try {
        if ($Flow -eq 'mg') {
            if ($Login) {
                Import-Module Microsoft.Graph.Authentication -ErrorAction Stop
                Say 'Signing in with Windows (WAM); pick the account whose calendar DeskWall should show.'
                Connect-MgGraph -Scopes 'Calendars.Read' -ContextScope CurrentUser -NoWelcome -ErrorAction Stop
            }
            $account = Connect-Mg
            $events = Read-Calendar { param($u) Get-GraphMg $u }
        }
        else {
            $access = if ($Login) { Start-DeviceCode } else { Get-DeviceAccess }
            $me = Get-GraphRaw $access 'v1.0/me?$select=userPrincipalName'
            $account = [string]$me.userPrincipalName
            $events = Read-Calendar { param($u) Get-GraphRaw $access $u }
        }
        if ($Login) { Say "Signed in as $account. DeskWall's calendar source will now read silently." }
        [Console]::Out.WriteLine((Write-Result 'ok' $account $events))
        return 0
    }
    catch [SignInRequired] {
        Say "graph-next-event: sign-in required ($($_.Exception.Message)). Run: deskwall calendar login"
        [Console]::Out.WriteLine((Write-Result 'signin-required' '' @()))
        return 2
    }
    catch {
        Say "graph-next-event: $($_.Exception.Message)"
        return 1
    }
}

$code = Invoke-Main
if ($Pause) { try { $null = Read-Host 'Press Enter to close' } catch { } }
exit $code