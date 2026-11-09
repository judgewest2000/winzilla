#requires -Version 5.1
<#
.SYNOPSIS
    Production (Release) builds of every Winzilla variant into .\dist.
.DESCRIPTION
    - winui3         : WinUI 3 (self-contained, via winui3\publish-release.ps1)
    - winforms-core  : WinForms on modern .NET (self-contained, single file)
    - winforms-netfx : WinForms on .NET Framework 4.8
.EXAMPLE
    .\build-all.ps1
    .\build-all.ps1 -Zip -Clean
#>
[CmdletBinding()]
param(
    [ValidateSet('x86', 'x64', 'ARM64')]
    [string]$Platform = $(if ($env:PROCESSOR_ARCHITECTURE -eq 'AMD64') { 'x64' } else { $env:PROCESSOR_ARCHITECTURE }),
    [string]$OutputDir = (Join-Path $PSScriptRoot 'dist'),
    [switch]$Zip,
    [switch]$Clean
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$rid = "win-$($Platform.ToLowerInvariant())"

if ($Clean -and (Test-Path $OutputDir)) { Remove-Item $OutputDir -Recurse -Force }
New-Item -ItemType Directory -Force $OutputDir | Out-Null

function Invoke-Step([string]$Name, [scriptblock]$Action) {
    Write-Host "`n=== $Name ===" -ForegroundColor Cyan
    & $Action
    if ($LASTEXITCODE -ne 0) { throw "$Name failed with exit code $LASTEXITCODE" }
}

function Compress-Output([string]$Folder, [string]$Name) {
    if (-not $Zip) { return }
    $zipPath = Join-Path $OutputDir "$Name.zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Compress-Archive -Path (Join-Path $Folder '*') -DestinationPath $zipPath
    Write-Host "Created $zipPath" -ForegroundColor Green
}

# 1. WinUI 3
$winuiOut = Join-Path $OutputDir "winui3-$rid"
Invoke-Step 'WinUI 3' {
    & (Join-Path $PSScriptRoot 'winui3\publish-release.ps1') -Platform $Platform -OutputDir (Join-Path $OutputDir '_winui3')
    if (Test-Path $winuiOut) { Remove-Item $winuiOut -Recurse -Force }
    Move-Item (Join-Path $OutputDir "_winui3\$rid") $winuiOut
    Remove-Item (Join-Path $OutputDir '_winui3') -Recurse -Force
    $global:LASTEXITCODE = 0
}
Compress-Output $winuiOut "Winzilla-winui3-$rid"

# 2. WinForms on modern .NET
$coreOut = Join-Path $OutputDir "winforms-core-$rid"
Invoke-Step 'WinForms (.NET)' {
    dotnet publish (Join-Path $PSScriptRoot 'winforms-core\Winzilla.WinForms.csproj') `
        -c Release -r $rid --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -o $coreOut
}
Compress-Output $coreOut "Winzilla-winforms-core-$rid"

# 3. WinForms on .NET Framework 4.8
$netfxOut = Join-Path $OutputDir 'winforms-netfx'
Invoke-Step 'WinForms (.NET Framework 4.8)' {
    dotnet build (Join-Path $PSScriptRoot 'winforms-netfx\Winzilla.WinForms.NetFx.csproj') `
        -c Release -o $netfxOut
}
Compress-Output $netfxOut 'Winzilla-winforms-netfx'

Write-Host "`nAll builds complete: $OutputDir" -ForegroundColor Green
Get-ChildItem $OutputDir | ForEach-Object { Write-Host "  $($_.Name)" }
