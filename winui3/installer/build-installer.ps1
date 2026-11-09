#requires -Version 5.1
<#
.SYNOPSIS
    Publishes Winzilla and builds Setup.exe (Inno Setup) plus the zip used by install.ps1.
.EXAMPLE
    .\build-installer.ps1 -Platform x64 -Version 1.0.0
#>
[CmdletBinding()]
param(
    [ValidateSet('x86', 'x64', 'ARM64')]
    [string]$Platform = 'x64',
    [string]$Version = '1.0.0'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot

& (Join-Path $root 'publish-release.ps1') -Platform $Platform -Zip -Clean

$iscc = (Get-Command iscc.exe -ErrorAction SilentlyContinue).Source
if (-not $iscc) {
    $iscc = @(
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $iscc) { throw 'Inno Setup 6 not found. Install with: winget install JRSoftware.InnoSetup' }

& $iscc "/DArch=$($Platform.ToLowerInvariant())" "/DAppVersion=$Version" (Join-Path $PSScriptRoot 'Winzilla.iss')
if ($LASTEXITCODE) { throw 'iscc failed' }

Write-Host 'Upload these to the GitHub release:' -ForegroundColor Green
Get-ChildItem (Join-Path $root 'publish') -Filter "Winzilla-*$($Platform.ToLowerInvariant())*" -File | ForEach-Object { Write-Host "  $($_.FullName)" }
