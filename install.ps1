#requires -Version 5.1
<#
.SYNOPSIS
    Installs (or uninstalls) the Winzilla WinUI 3 app for the current user.
.EXAMPLE
    & ([scriptblock]::Create((irm "https://raw.githubusercontent.com/judgewest2000/winzilla/main/install.ps1")))
.EXAMPLE
    & ([scriptblock]::Create((irm "https://raw.githubusercontent.com/judgewest2000/winzilla/main/install.ps1"))) -Uninstall
#>
[CmdletBinding()]
param(
    [string]$Repo = 'judgewest2000/winzilla',
    [string]$Version = 'latest',
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\Winzilla'),
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$appName = 'Winzilla'
$exeName = 'Winzilla.exe'
$startMenu = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\$appName.lnk"
$uninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$appName"

function Stop-App {
    $exe = Join-Path $InstallDir $exeName
    Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($exeName)) -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq $exe } |
        Stop-Process -Force
}

if ($Uninstall) {
    Stop-App
    if (Test-Path $InstallDir) { Remove-Item $InstallDir -Recurse -Force }
    if (Test-Path $startMenu) { Remove-Item $startMenu -Force }
    if (Test-Path $uninstallKey) { Remove-Item $uninstallKey -Recurse -Force }
    Write-Host "$appName uninstalled." -ForegroundColor Green
    return
}

$arch = switch ($env:PROCESSOR_ARCHITECTURE) {
    'AMD64' { 'x64' }
    'ARM64' { 'arm64' }
    'x86'   { if ($env:PROCESSOR_ARCHITEW6432 -eq 'AMD64') { 'x64' } elseif ($env:PROCESSOR_ARCHITEW6432 -eq 'ARM64') { 'arm64' } else { 'x86' } }
    default { throw "Unsupported architecture: $env:PROCESSOR_ARCHITECTURE" }
}
$assetName = "$appName-win-$arch.zip"

$api = if ($Version -eq 'latest') {
    "https://api.github.com/repos/$Repo/releases/latest"
} else {
    "https://api.github.com/repos/$Repo/releases/tags/$Version"
}

Write-Host "Looking up $Version release of $Repo..." -ForegroundColor Cyan
$release = Invoke-RestMethod -Uri $api -Headers @{ 'User-Agent' = "$appName-installer" }
$asset = $release.assets | Where-Object { $_.name -eq $assetName } | Select-Object -First 1
if (-not $asset) { throw "Release $($release.tag_name) has no asset named $assetName." }

$tmp = Join-Path ([IO.Path]::GetTempPath()) ("$appName-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp | Out-Null
try {
    $zip = Join-Path $tmp $assetName
    Write-Host "Downloading $assetName ($([math]::Round($asset.size / 1MB)) MB)..." -ForegroundColor Cyan
    Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $zip -UseBasicParsing

    $staging = Join-Path $tmp 'extracted'
    Expand-Archive -Path $zip -DestinationPath $staging -Force
    if (-not (Test-Path (Join-Path $staging $exeName))) { throw "$exeName not found in $assetName." }

    Stop-App
    if (Test-Path $InstallDir) { Remove-Item $InstallDir -Recurse -Force }
    New-Item -ItemType Directory -Path (Split-Path $InstallDir) -Force | Out-Null
    Move-Item -Path $staging -Destination $InstallDir
}
finally {
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}

$exePath = Join-Path $InstallDir $exeName
$shell = New-Object -ComObject WScript.Shell
$lnk = $shell.CreateShortcut($startMenu)
$lnk.TargetPath = $exePath
$lnk.WorkingDirectory = $InstallDir
$lnk.Save()

$ver = $release.tag_name.TrimStart('v')
New-Item -Path $uninstallKey -Force | Out-Null
$uninstallCmd = "powershell.exe -NoProfile -ExecutionPolicy Bypass -Command `"& ([scriptblock]::Create((irm 'https://raw.githubusercontent.com/$Repo/main/install.ps1'))) -Uninstall -InstallDir '$InstallDir'`""
@{
    DisplayName     = $appName
    DisplayVersion  = $ver
    Publisher       = $Repo.Split('/')[0]
    InstallLocation = $InstallDir
    DisplayIcon     = $exePath
    UninstallString = $uninstallCmd
    NoModify        = 1
    NoRepair        = 1
}.GetEnumerator() | ForEach-Object {
    $type = if ($_.Value -is [int]) { 'DWord' } else { 'String' }
    New-ItemProperty -Path $uninstallKey -Name $_.Key -Value $_.Value -PropertyType $type -Force | Out-Null
}

Write-Host "$appName $ver installed to $InstallDir" -ForegroundColor Green
