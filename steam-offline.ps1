#Requires -Version 5.1
<#
    steam-offline.ps1
    Command-line toggle for the Windows Firewall rules that block Steam's own
    processes from reaching the network, so Steam falls back to Offline Mode.

    Game executables are NOT blocked - only the Steam client itself.
    For a window with a button, run steam-offline-gui.ps1 instead.
#>

[CmdletBinding()]
param(
    [ValidateSet('toggle', 'block', 'unblock', 'status')]
    [string]$Action = 'toggle',

    [string]$SteamPath
)

. (Join-Path $PSScriptRoot 'steam-offline-core.ps1')

# --- self-elevate -----------------------------------------------------------
if (-not (Test-IsAdmin)) {
    Write-Host 'Requesting administrator rights...' -ForegroundColor Yellow
    $psArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass',
                '-File', "`"$PSCommandPath`"", '-Action', $Action)
    if ($SteamPath) { $psArgs += @('-SteamPath', "`"$SteamPath`"") }
    Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $psArgs
    return
}

$steamRoot = Get-SteamRoot -Override $SteamPath
$targets   = @(Get-SteamTargets -SteamRoot $steamRoot)

function Invoke-Block {
    $n = Set-SteamBlocked -Targets $targets
    $targets | ForEach-Object { Write-Host "  blocked  $_" -ForegroundColor DarkGray }
    Write-Host "`nSteam is now OFFLINE ($n binaries blocked)." -ForegroundColor Green
    Write-Host 'Restart Steam - it will prompt to start in Offline Mode.' -ForegroundColor Green
}

function Invoke-Unblock {
    $n = Set-SteamUnblocked
    if ($n) { Write-Host "Removed $n firewall rule(s)." -ForegroundColor Green }
    Write-Host 'Steam is ONLINE again.' -ForegroundColor Green
}

Write-Host ''
Write-Host "Steam root: $steamRoot" -ForegroundColor Cyan

switch ($Action) {
    'status' {
        if (Test-SteamBlocked) { Write-Host 'STATUS: BLOCKED (Steam offline)' -ForegroundColor Red }
        else                   { Write-Host 'STATUS: ALLOWED (Steam online)'  -ForegroundColor Green }
    }
    'block'   { Invoke-Block }
    'unblock' { Invoke-Unblock }
    'toggle'  {
        if (Test-SteamBlocked) { Invoke-Unblock } else { Invoke-Block }
    }
}

Write-Host ''
if ($Host.Name -eq 'ConsoleHost' -and -not $env:STEAM_OFFLINE_NOPAUSE) {
    Write-Host 'Press any key to close...' -ForegroundColor DarkGray
    $null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')
}
