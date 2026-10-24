<#
    steam-offline-core.ps1
    Shared logic for the CLI (steam-offline.ps1) and the GUI (steam-offline-gui.ps1).
    Dot-source this file; it defines functions only and performs no actions.
#>

$script:RulePrefix = 'SteamOfflineMode'

function Test-IsAdmin {
    $identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-SteamRoot {
    param([string]$Override)

    if ($Override) {
        if (Test-Path $Override) { return (Resolve-Path $Override).Path }
        throw "Path not found: $Override"
    }

    $candidates = @()

    foreach ($key in @('HKCU:\Software\Valve\Steam',
                       'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam',
                       'HKLM:\SOFTWARE\Valve\Steam')) {
        try {
            $p = Get-ItemProperty -Path $key -ErrorAction Stop
            if ($p.SteamPath)   { $candidates += $p.SteamPath }
            if ($p.InstallPath) { $candidates += $p.InstallPath }
            if ($p.SteamExe)    { $candidates += (Split-Path $p.SteamExe -Parent) }
        } catch { }
    }

    $candidates += @("${env:ProgramFiles(x86)}\Steam", "$env:ProgramFiles\Steam")

    foreach ($c in $candidates) {
        if (-not $c) { continue }
        $c = $c -replace '/', '\'
        if (Test-Path (Join-Path $c 'steam.exe')) { return (Resolve-Path $c).Path }
    }

    throw 'Steam not found. Pass -SteamPath "D:\Steam" manually.'
}

function Get-SteamTargets {
    param([Parameter(Mandatory)][string]$SteamRoot)

    # Client binaries only. Games live under steamapps\ and stay untouched.
    $clientExeNames = @(
        'steam.exe'
        'steamwebhelper.exe'    # path moves between client versions (bin\cef\cef.winXX\)
        'steamservice.exe'
        'steamerrorreporter.exe'
        'steamerrorreporter64.exe'
    )

    $targets = @(Join-Path $SteamRoot 'steam.exe')
    foreach ($dir in @('bin', 'package')) {
        $search = Join-Path $SteamRoot $dir
        if (Test-Path $search) {
            $targets += Get-ChildItem -Path $search -Recurse -File -ErrorAction SilentlyContinue |
                        Where-Object { $clientExeNames -contains $_.Name } |
                        Select-Object -ExpandProperty FullName
        }
    }

    $targets = $targets | Where-Object { Test-Path $_ } | Sort-Object -Unique
    if (-not $targets) { throw "No Steam client binaries found under $SteamRoot" }
    $targets
}

function Get-OfflineRules {
    Get-NetFirewallRule -DisplayName "$script:RulePrefix*" -ErrorAction SilentlyContinue
}

function Test-SteamBlocked {
    [bool](Get-OfflineRules)
}

function Set-SteamUnblocked {
    $rules = @(Get-OfflineRules)
    if ($rules.Count) { $rules | Remove-NetFirewallRule }
    $rules.Count
}

function Set-SteamBlocked {
    param([Parameter(Mandatory)][string[]]$Targets)

    Set-SteamUnblocked | Out-Null   # idempotent: clear first so re-runs don't duplicate

    $i = 0
    foreach ($exe in $Targets) {
        $i++
        foreach ($dir in @('Outbound', 'Inbound')) {
            New-NetFirewallRule `
                -DisplayName "$script:RulePrefix - $(Split-Path $exe -Leaf) [$i/$dir]" `
                -Group       $script:RulePrefix `
                -Direction   $dir `
                -Program     $exe `
                -Action      Block `
                -Profile     Any `
                -Enabled     True | Out-Null
        }
    }
    $Targets.Count
}

function Stop-SteamClient {
    # Steam caches its online state; a restart is what makes the block take effect.
    $procs = Get-Process -Name 'steam', 'steamwebhelper', 'steamerrorreporter' -ErrorAction SilentlyContinue
    if (-not $procs) { return 0 }
    $procs | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 800
    $procs.Count
}

function Start-SteamClient {
    param([Parameter(Mandatory)][string]$SteamRoot)
    $exe = Join-Path $SteamRoot 'steam.exe'
    if (-not (Test-Path $exe)) { throw "steam.exe not found at $exe" }
    Start-Process -FilePath $exe
}
