#requires -Version 5.1
<#
.SYNOPSIS
    Creates a production (Release, self-contained) build of Winzilla.
.EXAMPLE
    .\publish-release.ps1
    .\publish-release.ps1 -Platform ARM64 -Zip
#>
[CmdletBinding()]
param(
    [ValidateSet('x86', 'x64', 'ARM64')]
    [string]$Platform = $(if ($env:PROCESSOR_ARCHITECTURE -eq 'AMD64') { 'x64' } else { $env:PROCESSOR_ARCHITECTURE }),
    [string]$OutputDir = (Join-Path $PSScriptRoot 'publish'),
    [switch]$Zip,
    [switch]$Clean
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$project = Join-Path $PSScriptRoot 'Winzilla.csproj'
$rid = "win-$($Platform.ToLowerInvariant())"
$target = Join-Path $OutputDir $rid

if ($Clean -and (Test-Path $target)) {
    Remove-Item $target -Recurse -Force
}

Write-Host "Publishing Winzilla (Release, $rid) to $target" -ForegroundColor Cyan

dotnet publish $project `
    -c Release `
    -p:Platform=$Platform `
    -r $rid `
    --self-contained true `
    -p:PublishReadyToRun=true `
    -o $target
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

if ($Zip) {
    $zipPath = Join-Path $OutputDir "Winzilla-$rid.zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Compress-Archive -Path (Join-Path $target '*') -DestinationPath $zipPath
    Write-Host "Created $zipPath" -ForegroundColor Green
}

Write-Host "Done: $target" -ForegroundColor Green
