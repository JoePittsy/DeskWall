#Requires -Version 7
<#
.SYNOPSIS
    Publish deskwall.exe and DeskWall.Designer.exe into one install folder and restart the daemon.

.DESCRIPTION
    Both projects are published self-contained for win-x64 into a staging folder under %TEMP%, then
    merged: the designer's output first and the daemon's copied over it, except WindowsBase.dll,
    where the designer's full WPF copy must win over the daemon's 16 KB facade. The daemon's
    System.Diagnostics.EventLog.dll and .Messages.dll (the newer NuGet package pair) win over the
    designer's runtime-pack copies. Any other file the two outputs disagree on stops the script
    rather than being guessed at.

    The daemon running on the target runtime dir is stopped with the installed deskwall.exe
    `stop`; an install from before `stop` existed answers "unknown command" (exit 2), and the newly
    built exe's `stop` is used instead - it finds an old daemon the same way, because the default
    runtime dir's host window title is unchanged. The files are then copied over the install dir
    and `deskwall.exe run` is started detached, as the HKCU Run entry does.

    The runtime dir (%LOCALAPPDATA%\DeskWall or -Home) is never read or written by this script.

.PARAMETER InstallDir
    Where the merged output goes. Default: %LOCALAPPDATA%\Programs\DeskWall.

.PARAMETER Home
    Passed through as `--home` to stop and run. Omit for the default runtime dir.

.PARAMETER Aot
    Publish the daemon as native AOT. Needs the MSVC linker (VS "Desktop development with C++").

.PARAMETER NoRestart
    Stop and copy, but do not start the daemon again.
#>
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\DeskWall'),
    # $Home is a read-only automatic variable, so the parameter is named RuntimeHome; -Home works.
    [Alias('Home')][string]$RuntimeHome,
    [switch]$Aot,
    [switch]$NoRestart
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$InstallDir = [IO.Path]::GetFullPath($InstallDir)
# Quoted by hand: Start-Process joins its argument list with spaces and does not quote.
$homeArgs = if ($RuntimeHome) { @('--home', ('"' + [IO.Path]::GetFullPath($RuntimeHome).TrimEnd('\') + '"')) } else { @() }

# The Claude harness sets NoDefaultCurrentDirectoryInExePath=1, which breaks VsDevCmd's bare
# vswhere.exe call inside the AOT link step (CLAUDE.md). Harmless everywhere else.
$installer = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
$env:PATH = "$installer;$env:PATH"

# Processes running an exe from the install dir. Both sides go through Get-Item so they compare in
# long form: a process started through an 8.3 path (%TEMP% is often C:\Users\JOSEPH~1\...) reports
# that short path, and a plain string prefix match would silently find nothing.
function Get-InstallDirProcess([string]$name = '*') {
    if (-not (Test-Path $InstallDir)) { return }
    $dir = (Get-Item $InstallDir).FullName
    $names = @(Get-ChildItem $dir -Filter '*.exe' -File | ForEach-Object BaseName | Where-Object { $_ -like $name })
    if (-not $names) { return }
    Get-Process -Name $names -ErrorAction SilentlyContinue | Where-Object {
        $_.Path -and (Get-Item $_.Path).DirectoryName -eq $dir
    }
}

$designers = @(Get-InstallDirProcess 'DeskWall.Designer')
if ($designers) {
    throw "DeskWall.Designer is running from $InstallDir (pid $($designers.Id -join ', ')); close it first."
}

if ($Aot) {
    $vswhere = Join-Path $installer 'vswhere.exe'
    $vc = if (Test-Path $vswhere) { & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath }
    if (-not $vc) {
        throw '-Aot needs the MSVC linker: install the Visual Studio "Desktop development with C++" workload, or publish without -Aot (self-contained JIT).'
    }
}

# Runs deskwall.exe to completion and returns its exit code and output. It is a WinExe, so a plain
# call would not wait and its console output would be lost (CLAUDE.md).
function Invoke-Deskwall([string]$exe, [string[]]$arguments) {
    $out = New-TemporaryFile; $err = New-TemporaryFile
    try {
        $p = Start-Process -FilePath $exe -ArgumentList $arguments -Wait -NoNewWindow -PassThru `
            -RedirectStandardOutput $out -RedirectStandardError $err
        [pscustomobject]@{ Exit = $p.ExitCode; Output = ((Get-Content $out -Raw) + (Get-Content $err -Raw)).Trim() }
    }
    finally { Remove-Item $out, $err -ErrorAction SilentlyContinue }
}

$staging = Join-Path ([IO.Path]::GetTempPath()) ('deskwall-publish-' + [guid]::NewGuid().ToString('N'))
try {
    $designerOut = Join-Path $staging 'designer'
    $daemonOut = Join-Path $staging 'daemon'
    $merged = Join-Path $staging 'merged'

    Write-Host "publishing DeskWall.Designer (self-contained JIT)"
    dotnet publish (Join-Path $repo 'src\DeskWall.Designer') -c Release -r win-x64 --self-contained -o $designerOut -nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "designer publish failed ($LASTEXITCODE)" }

    Write-Host "publishing deskwall ($(if ($Aot) { 'native AOT' } else { 'self-contained JIT' }))"
    $aotProp = if ($Aot) { '-p:PublishAot=true' } else { '-p:PublishAot=false' }
    dotnet publish (Join-Path $repo 'src\DeskWall.Daemon') -c Release -r win-x64 --self-contained $aotProp -o $daemonOut -nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "daemon publish failed ($LASTEXITCODE)" }

    # Merge. The conflict list is the reviewed one; a new entry means a dependency moved and
    # someone has to decide which copy both executables can live with.
    $designerWins = @('WindowsBase.dll')
    $daemonWins = @('System.Diagnostics.EventLog.dll', 'System.Diagnostics.EventLog.Messages.dll')
    Copy-Item -Path (Join-Path $designerOut '*') -Destination (New-Item -ItemType Directory $merged) -Recurse
    foreach ($f in Get-ChildItem $daemonOut -Recurse -File) {
        $rel = [IO.Path]::GetRelativePath($daemonOut, $f.FullName)
        $dest = Join-Path $merged $rel
        if (Test-Path $dest) {
            if ((Get-FileHash $dest).Hash -eq (Get-FileHash $f.FullName).Hash) { continue }
            if ($designerWins -contains $rel) { continue }
            if ($daemonWins -notcontains $rel) {
                throw "designer and daemon publish different copies of $rel; review which one both can use and add it to the merge rule in scripts\publish.ps1"
            }
        }
        New-Item -ItemType Directory -Force (Split-Path $dest) | Out-Null
        Copy-Item $f.FullName $dest -Force
    }

    # Stop the daemon for this runtime dir.
    $installedExe = Join-Path $InstallDir 'deskwall.exe'
    $stagedExe = Join-Path $merged 'deskwall.exe'
    $stopper = if (Test-Path $installedExe) { $installedExe } else { $stagedExe }
    $r = Invoke-Deskwall $stopper ($homeArgs + 'stop')
    if ($r.Exit -eq 2 -and $stopper -eq $installedExe) {
        Write-Host "installed deskwall.exe has no 'stop' (older build); using the new one"
        $r = Invoke-Deskwall $stagedExe ($homeArgs + 'stop')
    }
    Write-Host $r.Output
    if ($r.Exit -ne 0) { throw "deskwall stop failed ($($r.Exit)); nothing was copied" }

    # Anything else still running from the install dir (a daemon on another --home, a one-shot
    # tick) holds its files open; say who, rather than fail half-way through the copy.
    $holders = @(Get-InstallDirProcess)
    if ($holders) {
        throw "still running from ${InstallDir}: $(($holders | ForEach-Object { "$($_.Name) pid $($_.Id)" }) -join ', '); nothing was copied"
    }

    New-Item -ItemType Directory -Force $InstallDir | Out-Null
    Copy-Item -Path (Join-Path $merged '*') -Destination $InstallDir -Recurse -Force
    if ($Aot) {
        # Left over from a JIT install; a native deskwall.exe never reads them.
        foreach ($stale in 'deskwall.dll', 'deskwall.deps.json', 'deskwall.runtimeconfig.json') {
            Remove-Item (Join-Path $InstallDir $stale) -ErrorAction SilentlyContinue
        }
    }
    $version = (Get-Item $installedExe).VersionInfo.ProductVersion
    Write-Host "installed $version into $InstallDir"

    if ($NoRestart) { Write-Host 'not restarted (-NoRestart)'; return }
    $p = Start-Process -FilePath $installedExe -ArgumentList ($homeArgs + 'run') -PassThru
    # A `run` that finds another daemon on this runtime dir hands it a refresh and exits at once;
    # a PID for that would be a lie.
    if ($p.WaitForExit(2000)) { throw "deskwall run exited straight away (exit $($p.ExitCode)); see the log in the runtime dir" }
    Write-Host "started deskwall $version, pid $($p.Id)"
}
finally {
    Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
}
