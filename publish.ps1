<#
    publish.ps1 - one-shot setup for publishing this folder to GitHub.

    Create an EMPTY repository named steam-offline-mode on GitHub first
    (no README, no .gitignore, no license - this script supplies all three),
    then run:

        .\publish.ps1 -User <your-github-login>

    Everything here is local except the final push.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$User,
    [string]$Repo = 'steam-offline-mode',
    [string]$Name,
    [switch]$NoPush
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (Test-Path (Join-Path $PSScriptRoot '.git')) {
    throw 'A .git folder already exists here. Remove it first or push manually.'
}

# --- commit identity --------------------------------------------------------
# GitHub's noreply address keeps the real mailbox out of a history that can
# never be rewritten once other people have cloned it.
$id = $null
try {
    $api = Invoke-RestMethod -Uri "https://api.github.com/users/$User" -Headers @{
        'User-Agent' = 'steam-offline-mode-publish'
    }
    $id = $api.id
    if (-not $Name) { $Name = if ($api.name) { $api.name } else { $User } }
} catch {
    Write-Warning "Could not reach the GitHub API for '$User'; falling back to the short noreply form."
    if (-not $Name) { $Name = $User }
}

$email = if ($id) { "$id+$User@users.noreply.github.com" } else { "$User@users.noreply.github.com" }

Write-Host "author : $Name <$email>" -ForegroundColor Cyan
Write-Host "repo   : https://github.com/$User/$Repo" -ForegroundColor Cyan

# --- fill the licence placeholder ------------------------------------------
$licensePath = Join-Path $PSScriptRoot 'LICENSE'
$license = Get-Content $licensePath -Raw
if ($license -match 'YOUR_GITHUB_LOGIN') {
    $license = $license -replace 'YOUR_GITHUB_LOGIN', $Name
    [System.IO.File]::WriteAllText($licensePath, $license, (New-Object System.Text.UTF8Encoding $false))
    Write-Host 'LICENSE: copyright holder filled in' -ForegroundColor DarkGray
}

# --- repository -------------------------------------------------------------
# git reports progress and line-ending notices on stderr. Under
# $ErrorActionPreference = 'Stop' PowerShell promotes any native stderr write to
# a terminating NativeCommandError, so a harmless warning would abort the
# publish halfway. Exit codes are the only reliable signal here.
function Invoke-Git {
    $saved = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & git @args 2>&1 | ForEach-Object { "$_" } | Write-Verbose }
    finally { $ErrorActionPreference = $saved }
    if ($LASTEXITCODE -ne 0) {
        throw "git $($args -join ' ') failed with exit code $LASTEXITCODE"
    }
}

Invoke-Git init -b main
Invoke-Git config user.name  $Name
Invoke-Git config user.email $email

Invoke-Git add -A
Invoke-Git commit -m @'
Steam offline mode toggle

A single-button Windows app that blocks the Steam client's own executables
in Windows Firewall so Steam starts in Offline Mode. Games keep their
network access.

- WPF UI composed in code, no XAML, built with the csc.exe that ships with
  Windows - no SDK, no NuGet, no project file
- Firewall rules are driven through the NetSecurity cmdlets rather than
  netsh, whose output is localised and whose exit codes are unreliable
- Both pipes of the child PowerShell are drained asynchronously; reading one
  to the end before the other deadlocks once progress CLIXML fills stderr
'@

Write-Host 'commit created' -ForegroundColor Green

Invoke-Git remote add origin "https://github.com/$User/$Repo.git"

if ($NoPush) {
    Write-Host 'Stopping before push (-NoPush). Run: git push -u origin main' -ForegroundColor Yellow
    return
}

Write-Host 'pushing - a browser or credential prompt may appear...' -ForegroundColor Cyan
Invoke-Git push -u origin main

Write-Host ''
Write-Host "published: https://github.com/$User/$Repo" -ForegroundColor Green
Write-Host 'To cut a release with a prebuilt exe:' -ForegroundColor DarkGray
Write-Host '    git tag v1.0.0; git push origin v1.0.0' -ForegroundColor DarkGray
Write-Host 'GitHub Actions then builds it and attaches the exe plus its SHA256.' -ForegroundColor DarkGray
