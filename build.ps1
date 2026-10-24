# Builds dist\SteamOffline.exe with the .NET Framework compiler that ships with
# Windows - no SDK, no NuGet, no project file.

$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$src  = Join-Path $root 'src'
$dist = Join-Path $root 'dist'
$fw   = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$csc  = Join-Path $fw 'csc.exe'

if (-not (Test-Path $csc)) { throw "csc.exe not found at $csc" }
if (-not (Test-Path $dist)) { New-Item -ItemType Directory -Path $dist | Out-Null }

& (Join-Path $src 'make-icon.ps1')

$refs = @(
    "$fw\WPF\PresentationFramework.dll"
    "$fw\WPF\PresentationCore.dll"
    "$fw\WPF\WindowsBase.dll"
    "$fw\System.Xaml.dll"
    "$fw\System.dll"
    "$fw\System.Core.dll"
    "$fw\System.Xml.dll"
)
foreach ($r in $refs) { if (-not (Test-Path $r)) { throw "missing reference: $r" } }

$exe = Join-Path $dist 'SteamOffline.exe'

$cscArgs = @(
    '/nologo'
    '/target:winexe'
    '/platform:x64'
    '/optimize+'
    '/warnaserror-'
    "/out:$exe"
    "/win32icon:$src\icon.ico"
    "/win32manifest:$src\app.manifest"
)
$cscArgs += ($refs | ForEach-Object { "/reference:$_" })
$cscArgs += "$src\Program.cs"

Write-Host 'compiling...' -ForegroundColor Cyan
& $csc $cscArgs
if ($LASTEXITCODE -ne 0) { throw "csc failed with exit code $LASTEXITCODE" }

$info = Get-Item $exe
Write-Host ("built {0} ({1:N0} bytes)" -f $info.FullName, $info.Length) -ForegroundColor Green
