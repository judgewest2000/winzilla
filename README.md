# Winzilla

Winzilla is a lightweight Windows file-transfer client for **FTP** and **SFTP**. Connect to a remote server and move files between it and your PC, using native Windows shell icons and a Mica-backed look.

It comes in three variants:

| Folder | Description |
|---|---|
| `winui3` | Modern WinUI 3 app (self-contained, .NET 10) |
| `winforms-core` | WinForms on modern .NET (self-contained, single file) |
| `winforms-netfx` | WinForms on .NET Framework 4.8 |

## Install (WinUI 3)

Requires Windows 10 1809 or later. Installs per user, no admin rights needed.

### Option 1: PowerShell one-liner

```powershell
& ([scriptblock]::Create((irm "https://raw.githubusercontent.com/judgewest2000/winzilla/main/install.ps1")))
```

This downloads the latest release for your architecture (x64, ARM64 or x86) to `%LOCALAPPDATA%\Programs\Winzilla`, then adds a Start Menu shortcut and an Apps & Features entry.

Options (append after the closing parentheses):

- `-Version v1.0.0` installs a specific release
- `-InstallDir <path>` installs somewhere else
- `-Uninstall` removes Winzilla

### Option 2: Setup.exe

Download `Winzilla-Setup-<arch>.exe` from the [latest release](https://github.com/judgewest2000/winzilla/releases/latest) and run it.

### Uninstall

Use Windows Settings > Apps, or run the one-liner above with `-Uninstall`.

## About

Winzilla is open source, released under the [MIT License](LICENSE). Copyright (c) 2026 James West. In the WinUI 3 app, click **About** in the bottom-right corner to see the version and license.

## Updates

On startup, the WinUI 3 app checks GitHub for a newer release and asks whether to update. Updating downloads the matching `Winzilla-Setup-<arch>.exe`, verifies its SHA-256 checksum, upgrades in place and restarts the app.

## Build from source

Requires the .NET 10 SDK.

```powershell
# Build every variant into .\dist
.\build-all.ps1 -Zip -Clean

# WinUI 3 only
cd winui3
.\publish-release.ps1 -Platform x64 -Zip
```

## Publish a release

Requires [Inno Setup 6](https://jrsoftware.org/isinfo.php) (`winget install JRSoftware.InnoSetup`) and the GitHub CLI.

```powershell
cd winui3\installer
.\build-installer.ps1 -Platform x64 -Version 1.0.0
gh release create v1.0.0 ..\publish\Winzilla-win-x64.zip ..\publish\Winzilla-Setup-x64.exe --title v1.0.0
```

The zip must be named `Winzilla-win-<arch>.zip` for `install.ps1` to find it. Repeat for `ARM64` and `x86` as needed.
